using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Convai.Infrastructure.Networking.Native;
using LiveKit;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients;
using LiveKit.Proto;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    public class LiveKitAudioProcessingTests
    {
        [Test]
        public void AudioBuffer_ReadDuration_ReturnsNullUntilEnoughTenMillisecondSamplesExist()
        {
            using var buffer = new AudioBuffer();

            buffer.Write(new float[200], 2, 48000);
            using AudioFrame insufficient = buffer.ReadDuration(AudioProcessingModule.FrameDurationMs);
            Assert.That(insufficient, Is.Null);

            buffer.Write(new float[960], 2, 48000);
            using AudioFrame frame = buffer.ReadDuration(AudioProcessingModule.FrameDurationMs);

            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.NumChannels, Is.EqualTo(2));
            Assert.That(frame.SampleRate, Is.EqualTo(48000));
            Assert.That(frame.SamplesPerChannel, Is.EqualTo(480));
        }

        [Test]
        public void AudioBuffer_Clear_DropsBufferedSamples()
        {
            using var buffer = new AudioBuffer();

            buffer.Write(new float[960], 2, 48000);
            buffer.Clear();

            using AudioFrame frame = buffer.ReadDuration(AudioProcessingModule.FrameDurationMs);
            Assert.That(frame, Is.Null);
        }

        [Test]
        public void MicrophoneSource_OnAudioRead_StereoInput_ForwardsMonoFrame()
        {
            var source =
                (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(source);

            float[] capturedSamples = null;
            int capturedChannels = 0;
            int capturedSampleRate = 0;
            source.AudioRead += (samples, channels, sampleRate) =>
            {
                capturedSamples = samples;
                capturedChannels = channels;
                capturedSampleRate = sampleRate;
            };

            float[] stereoSamples = { 1f, -1f, 0.5f, 0.5f };
            MethodInfo onAudioRead = typeof(MicrophoneSource).GetMethod(
                "OnAudioRead",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(onAudioRead, Is.Not.Null);
            onAudioRead.Invoke(source, new object[] { stereoSamples, 2, 16000 });

            Assert.That(capturedChannels, Is.EqualTo(1));
            Assert.That(capturedSampleRate, Is.EqualTo(16000));
            Assert.That(capturedSamples, Is.EqualTo(new[] { 0f, 0.5f }));
        }

        [Test]
        public void RtcAudioSource_ProcessedAudioRead_ConvertsProcessedPcm16ToFloat()
        {
            var source =
                (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(source);

            float[] capturedSamples = null;
            int capturedChannels = 0;
            int capturedSampleRate = 0;
            source.ProcessedAudioRead += (samples, channels, sampleRate) =>
            {
                capturedSamples = (float[])samples.Clone();
                capturedChannels = channels;
                capturedSampleRate = sampleRate;
            };

            using var frame = new AudioFrame(16000, 1, 3);
            Marshal.Copy(new short[] { short.MinValue, 0, short.MaxValue }, 0, frame.Data, 3);

            MethodInfo raiseProcessedAudioRead = typeof(RtcAudioSource).GetMethod(
                "RaiseProcessedAudioRead",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(AudioFrame) },
                null);

            Assert.That(raiseProcessedAudioRead, Is.Not.Null);
            raiseProcessedAudioRead.Invoke(source, new object[] { frame });

            Assert.That(capturedChannels, Is.EqualTo(1));
            Assert.That(capturedSampleRate, Is.EqualTo(16000));
            Assert.That(capturedSamples, Has.Length.EqualTo(3));
            Assert.That(capturedSamples[0], Is.EqualTo(-1f).Within(0.000001f));
            Assert.That(capturedSamples[1], Is.EqualTo(0f).Within(0.000001f));
            Assert.That(capturedSamples[2], Is.EqualTo(short.MaxValue / 32768f).Within(0.000001f));
        }

        [Test]
        public void NativeMicrophoneSource_PcmObserver_SubscribesToProcessedAudioOnlyWhileNeeded()
        {
            var rtcSource =
                (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(rtcSource);
            var nativeSource =
                (NativeMicrophoneSource)FormatterServices.GetUninitializedObject(typeof(NativeMicrophoneSource));
            SetPrivateField(nativeSource, "_underlyingSource", rtcSource);

            float[] capturedSamples = null;
            Action<float[], int, int> observer = (samples, _, _) => capturedSamples = samples;
            nativeSource.PcmFrame += observer;

            FieldInfo processedAudioRead = typeof(RtcAudioSource).GetField(
                "ProcessedAudioRead",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(processedAudioRead, Is.Not.Null);

            var callback = (Action<float[], int, int>)processedAudioRead.GetValue(rtcSource);
            Assert.That(callback, Is.Not.Null);

            float[] expected = { 0.25f, -0.25f };
            callback.Invoke(expected, 1, 16000);
            Assert.That(capturedSamples, Is.SameAs(expected));

            nativeSource.PcmFrame -= observer;
            Assert.That(processedAudioRead.GetValue(rtcSource), Is.Null);
        }

        [Test]
        public void RtcAudioSource_EstimateStreamDelayMs_ClampsToWebRtcLimit()
        {
            int delayMs = RtcAudioSource.EstimateStreamDelayMs(
                bufferLength: 1024,
                numBuffers: 4,
                sampleRate: 16000);

            Assert.That(delayMs, Is.EqualTo(500));
        }

        [Test]
        public void ApmReverseStream_IsActive_RequiresSuccessfulFrameAndResetsAcrossSessions()
        {
            var listenerObject = new GameObject("AEC reverse stream readiness test");
            var probe = listenerObject.AddComponent<AudioProbe>();
            using var reverseStream = new ApmReverseStream(null);
            SetPrivateField(reverseStream, "_probe", probe);
            SetPrivateField(reverseStream, "_isActive", true);

            Assert.That(reverseStream.IsActive, Is.False,
                "Attaching an AudioProbe must not report AEC active before processing a reverse frame.");

            SetPrivateField(reverseStream, "_hasProcessedReverseFrame", true);
            Assert.That(reverseStream.IsActive, Is.True);

            reverseStream.Start();
            Assert.That(reverseStream.IsActive, Is.False,
                "Restarting an output session must require a fresh processed reverse frame.");

            SetPrivateField(reverseStream, "_hasProcessedReverseFrame", true);
            reverseStream.Stop();
            Assert.That(reverseStream.IsActive, Is.False);

            UnityEngine.Object.DestroyImmediate(listenerObject);
        }

        [Test]
        public void FfiRequestExtensions_Inject_ApmRequests_SetsExpectedOneOfField()
        {
            var newApmRequest = new FfiRequest();
            newApmRequest.Inject(new NewApmRequest());
            Assert.That(newApmRequest.NewApm, Is.Not.Null);

            var processRequest = new FfiRequest();
            processRequest.Inject(new ApmProcessStreamRequest());
            Assert.That(processRequest.ApmProcessStream, Is.Not.Null);

            var reverseRequest = new FfiRequest();
            reverseRequest.Inject(new ApmProcessReverseStreamRequest());
            Assert.That(reverseRequest.ApmProcessReverseStream, Is.Not.Null);

            var delayRequest = new FfiRequest();
            delayRequest.Inject(new ApmSetStreamDelayRequest());
            Assert.That(delayRequest.ApmSetStreamDelay, Is.Not.Null);
        }

        [Test]
        public void FfiRequestExtensions_EnsureClean_ThrowsWhenApmResponseFieldsArePresent()
        {
            var response = new FfiResponse { ApmSetStreamDelay = new ApmSetStreamDelayResponse() };
            Assert.Throws<InvalidOperationException>(() => response.EnsureClean());
        }

        [Test]
        public void AudioStream_Dispose_WhenAudioSourceAlreadyDestroyed_DoesNotThrow()
        {
            var go = new GameObject("Destroyed AudioStream source");
            AudioSource source = go.AddComponent<AudioSource>();
            UnityEngine.Object.DestroyImmediate(go);

            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            SetPrivateField(stream, "_audioSource", source);
            SetPrivateField(stream, "_lock", new object());

            Assert.That(source == null, Is.True);
            Assert.DoesNotThrow(stream.Dispose);
        }

        [Test]
        public void RingBuffer_WriteExact_WhenCapacityIsInsufficient_DoesNotWritePartialChunk()
        {
            using var buffer = new RingBuffer(8);

            Assert.That(buffer.WriteExact(new byte[] { 1, 2, 3, 4, 5, 6 }), Is.True);
            Assert.That(buffer.WriteExact(new byte[] { 7, 8, 9, 10 }), Is.False);
            Assert.That(buffer.AvailableRead(), Is.EqualTo(6));

            var actual = new byte[8];
            int read = buffer.Read(actual);
            Assert.That(read, Is.EqualTo(6));
            Assert.That(actual[..read], Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void AudioStream_PlaybackSnapshot_ExposesAbsoluteSourceClockAndHealthCounters()
        {
            PropertyInfo property = typeof(AudioStream).GetProperty(nameof(AudioStream.PlaybackSnapshot));

            Assert.That(property, Is.Not.Null);
            Assert.That(property.PropertyType, Is.EqualTo(typeof(AudioStreamPlaybackSnapshot)));
            Assert.That(Enum.GetNames(typeof(AudioStreamPlaybackState)),
                Is.EquivalentTo(new[] { "Idle", "Playing", "Underrun", "Disposed" }));
        }

        [Test]
        public void AudioStream_GetPcm16Magnitude_WhenSampleIsMinimumValue_DoesNotThrow()
        {
            Assert.That(AudioStream.GetPcm16Magnitude(short.MinValue), Is.EqualTo(32768));
            Assert.That(AudioStream.GetPcm16Magnitude(short.MaxValue), Is.EqualTo(32767));
            Assert.That(AudioStream.GetPcm16Magnitude(0), Is.Zero);
        }

        [Test]
        public void AudioStream_GetPeakAndFirstSignalFrame_FindsExactInterleavedStereoFrame()
        {
            short[] samples =
            {
                0, 0,
                100, -600,
                900, 0
            };

            int peak = AudioStream.GetPeakAndFirstSignalFrame(
                samples, samples.Length, 2, 500, out int firstSignalFrame);

            Assert.That(firstSignalFrame, Is.EqualTo(1));
            Assert.That(peak, Is.EqualTo(900));
        }

        [Test]
        public void AudioStream_GetPeakAndFirstSignalFrame_MonoSilenceDoesNotStartPlayback()
        {
            short[] samples = { 0, 100, -499, 499 };

            int peak = AudioStream.GetPeakAndFirstSignalFrame(
                samples, samples.Length, 1, 500, out int firstSignalFrame);

            Assert.That(firstSignalFrame, Is.EqualTo(-1));
            Assert.That(peak, Is.EqualTo(499));
        }

        [Test]
        public void AudioStream_GetPeakAndFirstSignalFrame_ThresholdCrossingIsInclusive()
        {
            short[] samples = { 0, -500, 1000 };

            int peak = AudioStream.GetPeakAndFirstSignalFrame(
                samples, samples.Length, 1, 500, out int firstSignalFrame);

            Assert.That(firstSignalFrame, Is.EqualTo(1));
            Assert.That(peak, Is.EqualTo(1000));
        }

        [Test]
        public void AudioStreamPlaybackSnapshot_EstimateRenderedSourceFrame_InterpolatesWithinCallback()
        {
            var snapshot = new AudioStreamPlaybackSnapshot(
                1480, 48000, 1, 1, 0, AudioStreamPlaybackState.Playing,
                1480, 480, 0, 0, 0,
                callbackSourceFrameStart: 1000,
                callbackSourceFrameCount: 480,
                callbackDspTime: 10d);

            Assert.That(snapshot.EstimateRenderedSourceFrame(10d), Is.EqualTo(1000));
            Assert.That(snapshot.EstimateRenderedSourceFrame(10.005d), Is.EqualTo(1240));
            Assert.That(snapshot.EstimateRenderedSourceFrame(11d), Is.EqualTo(1480));
        }

        [Test]
        public void AudioStreamPlaybackSnapshot_EstimateRenderedSourceFrame_FreezesDuringUnderrun()
        {
            var snapshot = new AudioStreamPlaybackSnapshot(
                1480, 48000, 1, 1, 0, AudioStreamPlaybackState.Underrun,
                1480, 480, 0, 0, 1,
                callbackSourceFrameStart: 1000,
                callbackSourceFrameCount: 480,
                callbackDspTime: 10d);

            Assert.That(snapshot.EstimateRenderedSourceFrame(11d), Is.EqualTo(1480));
        }

        [Test]
        public void AudioStream_RampFromFrame_PreservesPerChannelContinuity()
        {
            var samples = new short[] { 0, 0, 1000, -1000, 2000, -2000, 3000, -3000 };
            var sourceFrame = new short[] { 4000, -4000 };

            AudioStream.RampFromFrame(samples, samples.Length, 2, sourceFrame, 4);

            Assert.That(samples[0], Is.EqualTo(3000));
            Assert.That(samples[1], Is.EqualTo(-3000));
            Assert.That(samples[^2], Is.EqualTo(3000));
            Assert.That(samples[^1], Is.EqualTo(-3000));
        }

        [Test]
        public void AudioStream_EvaluatePlaybackGain_FadesQuicklyAndReachesExactTarget()
        {
            Assert.That(AudioStream.EvaluatePlaybackGain(1f, 0f, 0, 100), Is.EqualTo(1f));
            Assert.That(AudioStream.EvaluatePlaybackGain(1f, 0f, 50, 100), Is.EqualTo(0.25f));
            Assert.That(AudioStream.EvaluatePlaybackGain(1f, 0f, 100, 100), Is.Zero);
            Assert.That(AudioStream.EvaluatePlaybackGain(0f, 1f, 100, 100), Is.EqualTo(1f));
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }
    }
}
