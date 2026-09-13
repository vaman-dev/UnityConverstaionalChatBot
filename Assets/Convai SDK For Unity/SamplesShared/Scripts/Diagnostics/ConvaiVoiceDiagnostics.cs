using System;
using System.Text;
using Convai.Domain.DomainEvents.Session;
using Convai.Modules.LipSync;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using UnityEngine;

namespace Convai.Sample.Diagnostics
{
    /// <summary>
    /// Read-only diagnostic overlay for Convai character voice playback.
    /// Attach this to the same GameObject as the character AudioSource.
    /// Remove it after diagnosing the issue.
    /// </summary>
    [AddComponentMenu("Convai/Diagnostics/Voice Diagnostics")]
    [RequireComponent(typeof(AudioSource))]
    // LiveKit adds its AudioProbe at runtime. Run after that probe so this
    // diagnostic measures the PCM buffer after the stream has filled it.
    [DefaultExecutionOrder(10000)]
    public sealed class ConvaiVoiceDiagnostics : MonoBehaviour
    {
        [Header("Diagnostic Output")]
        [SerializeField] private bool _showOverlay = true;
        [SerializeField] private bool _logToConsole = true;
        [SerializeField] [Range(0.25f, 10f)] private float _logIntervalSeconds = 1f;

        private AudioSource _audioSource;
        private ConvaiCharacter _character;
        private ConvaiAudioOutput _audioOutput;
        private ConvaiLipSyncComponent _lipSync;
        private ConvaiRoomManager _roomManager;
        private float _nextLogTime;
        private long _audioFilterCallbackCount;
        private float _latestRms;
        private float _latestPeak;
        private string _lastReport = "Waiting for diagnostics...";

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _character = GetComponent<ConvaiCharacter>();
            _audioOutput = GetComponent<ConvaiAudioOutput>();
            _lipSync = GetComponent<ConvaiLipSyncComponent>();
            _roomManager = FindAnyObjectByType<ConvaiRoomManager>();

            if (_character == null)
                Debug.LogWarning("[Convai Voice Diagnostics] No ConvaiCharacter found on this GameObject.", this);
        }

        private void Update()
        {
            if (!_logToConsole || Time.unscaledTime < _nextLogTime) return;

            _nextLogTime = Time.unscaledTime + Mathf.Max(0.25f, _logIntervalSeconds);
            _lastReport = BuildReport();
            Debug.Log(_lastReport, this);
        }

        private void OnGUI()
        {
            if (!_showOverlay) return;

            const int width = 620;
            const int height = 310;
            GUI.Box(new Rect(12, 12, width, height), "Convai Voice Diagnostics");
            GUI.Label(new Rect(26, 42, width - 28, height - 36), _lastReport);
        }

        /// <summary>Writes an immediate diagnostic snapshot to the Unity Console.</summary>
        [ContextMenu("Log Diagnostic Snapshot")]
        public void LogDiagnosticSnapshot()
        {
            _lastReport = BuildReport();
            Debug.Log(_lastReport, this);
        }

        // Unity invokes this only for an AudioSource on this same GameObject.
        // It tells us whether audio samples are reaching Unity's output pipeline.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || data.Length == 0) return;

            float sumSquares = 0f;
            float peak = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float sample = Mathf.Abs(data[i]);
                sumSquares += data[i] * data[i];
                if (sample > peak) peak = sample;
            }

            _latestRms = Mathf.Sqrt(sumSquares / data.Length);
            _latestPeak = peak;
            System.Threading.Interlocked.Increment(ref _audioFilterCallbackCount);
        }

        private string BuildReport()
        {
            var report = new StringBuilder(2200);
            long callbackCount = System.Threading.Interlocked.Read(ref _audioFilterCallbackCount);
            float rms = _latestRms;
            float peak = _latestPeak;

            report.AppendLine("READ-ONLY snapshot");
            report.AppendLine($"Object: {name}");
            report.AppendLine($"Platform: {UnityEngine.Application.platform} | Playing: {UnityEngine.Application.isPlaying}");
            report.AppendLine();

            report.AppendLine("[Convai session]");
            if (_character == null)
            {
                report.AppendLine("FAIL: ConvaiCharacter is missing on this object.");
            }
            else
            {
                report.AppendLine($"Character: {_character.CharacterName} | ID: {_character.CharacterId}");
                report.AppendLine($"Session: {_character.SessionState} | Ready: {_character.IsCharacterReady} | Speaking signal: {_character.IsSpeaking}");
                report.AppendLine($"Remote audio enabled: {_character.IsRemoteAudioEnabled} | Configured on start: {_character.EnableRemoteAudioOnStart}");
                report.AppendLine($"In conversation: {_character.IsInConversation}");
            }

            if (_roomManager == null)
            {
                report.AppendLine("FAIL: ConvaiRoomManager was not found in the scene.");
            }
            else
            {
                report.AppendLine($"Room: {_roomManager.CurrentState} | Connection type: {_roomManager.EffectiveConnectionType}");
                report.AppendLine($"Audio playback active: {_roomManager.IsAudioPlaybackActive} | Requires user gesture: {_roomManager.RequiresUserGestureForAudio}");
                report.AppendLine($"Can enable audio playback: {_roomManager.CanEnableAudioPlayback}");
            }

            report.AppendLine();
            report.AppendLine("[Unity AudioSource]");
            if (_audioSource == null)
            {
                report.AppendLine("FAIL: AudioSource is missing.");
            }
            else
            {
                report.AppendLine($"Enabled: {_audioSource.enabled} | Active: {isActiveAndEnabled} | IsPlaying: {_audioSource.isPlaying}");
                report.AppendLine($"Volume: {_audioSource.volume:0.###} | Mute: {_audioSource.mute} | Spatial blend: {_audioSource.spatialBlend:0.###}");
            report.AppendLine($"Clip assigned: {_audioSource.clip != null} | Filter callbacks: {callbackCount}");
            report.AppendLine($"Last audio RMS: {rms:0.000000} | Peak: {peak:0.000000}");
            AppendLiveKitPcmReport(report);
            }

            report.AppendLine();
            report.AppendLine("[Lip Sync]");
            if (_lipSync == null)
            {
                report.AppendLine("WARN: ConvaiLipSyncComponent is missing.");
            }
            else
            {
                report.AppendLine($"Enabled: {_lipSync.enabled} | State: {_lipSync.EngineState} | IsPlaying: {_lipSync.IsPlaying}");
                report.AppendLine($"Talking: {_lipSync.IsTalking} | Buffered: {_lipSync.GetTotalBufferedDuration():0.000}s | Headroom: {_lipSync.GetHeadroom():0.000}s");
            }

            report.AppendLine();
            report.Append("[Diagnosis] ");
            report.AppendLine(BuildDiagnosis(callbackCount, rms));
            return report.ToString();
        }

        private string BuildDiagnosis(long callbackCount, float rms)
        {
            if (_character == null) return "Attach this component to the Convai Character object.";
            if (_roomManager == null) return "Add/enable a ConvaiRoomManager in this scene.";
            if (_character.SessionState != SessionState.Connected)
                return $"The character is not connected; current state is {_character.SessionState}.";
            if (!_character.IsRemoteAudioEnabled)
                return "Remote audio is disabled. Enable the character's Remote Audio setting.";
            if (_roomManager.RequiresUserGestureForAudio && !_roomManager.IsAudioPlaybackActive)
                return "Audio is waiting for a user gesture. Click the game once, then try again.";
            if (callbackCount == 0)
                return "Unity receives no AudioSource callbacks. Check that this script is on the same object as the active AudioSource.";
            if (_character.IsSpeaking && rms < 0.0001f)
                return "Convai says the character is speaking, but Unity is receiving silence. Check the character voice/backend response and Console.";
            if (_character.IsSpeaking && _lipSync != null && !_lipSync.IsPlaying)
                return "Voice samples are present, but Lip Sync is not consuming them; inspect the Lip Sync mapping/profile.";
            return "The local voice pipeline currently looks healthy. If you still hear nothing, check the OS output device and Unity audio settings.";
        }

        private static void AppendLiveKitPcmReport(StringBuilder report)
        {
            Type streamType = Type.GetType("LiveKit.AudioStream, LiveKit");
            if (streamType == null)
            {
                report.AppendLine("LiveKit PCM probe: unavailable (LiveKit assembly not loaded).");
                return;
            }

            object callbacks = streamType.GetProperty("PcmCallbackCount")?.GetValue(null);
            object nonZero = streamType.GetProperty("PcmNonZeroCallbackCount")?.GetValue(null);
            object liveKitRms = streamType.GetProperty("LastPcmRms")?.GetValue(null);
            object liveKitPeak = streamType.GetProperty("LastPcmPeak")?.GetValue(null);
            report.AppendLine($"LiveKit PCM callbacks: {callbacks} | Non-zero: {nonZero}");
            report.AppendLine($"LiveKit PCM RMS: {liveKitRms:0.000000} | Peak: {liveKitPeak:0.000000}");
        }
    }
}
