using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Modules.Vision;
using Convai.Runtime;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Modules;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.NarrativeDesign;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Runtime.Vision.Publishing;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;
using RtviSceneMetadata = Convai.Infrastructure.Protocol.Messages.SceneMetadata;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiVisionPublisherModuleTests
    {
        private class FakeVisionFrameSource : MonoBehaviour, IVisionFrameSource
        {
            public bool AutoReadyOnStart { get; set; } = true;
            public bool IsCapturing { get; private set; }
            public bool IsFrameReady { get; set; }
            public long FrameCount => 0;
            public (int Width, int Height) FrameDimensions => (0, 0);
            public float TargetFrameRate => 15f;
            public string SourceId => "fake";
            public RenderTexture CurrentRenderTexture { get; set; }
            public event Action FrameReady;

            public virtual void StartCapture()
            {
                IsCapturing = true;
                if (!AutoReadyOnStart || CurrentRenderTexture == null)
                    return;

                MarkFrameReady();
            }

            public virtual void StopCapture()
            {
                IsCapturing = false;
                IsFrameReady = false;
            }

            public void MarkFrameReady()
            {
                if (CurrentRenderTexture == null)
                    return;

                IsFrameReady = true;
                FrameReady?.Invoke();
            }
        }

        private sealed class FakeStatusVisionFrameSource : FakeVisionFrameSource, IVisionFrameSourceStatusProvider
        {
            public VisionSourceState StartState { get; set; } = VisionSourceState.Starting;
            public VisionSourceState State { get; private set; } = VisionSourceState.Idle;
            public VisionSourceErrorKind ErrorKind { get; private set; } = VisionSourceErrorKind.None;
            public string StatusMessage { get; private set; }
            public bool HasUsableFrame => IsFrameReady && CurrentRenderTexture != null;
            public event Action StatusChanged;

            public override void StartCapture()
            {
                base.StartCapture();
                SetStatus(StartState);
                if (AutoReadyOnStart && HasUsableFrame)
                    SetStatus(VisionSourceState.Ready);
            }

            public override void StopCapture()
            {
                base.StopCapture();
                SetStatus(VisionSourceState.Stopped);
            }

            public void SetFailed(
                VisionSourceErrorKind errorKind = VisionSourceErrorKind.Unknown,
                string message = "Source failed") =>
                SetStatus(VisionSourceState.Failed, errorKind, message);

            public void SetReady()
            {
                MarkFrameReady();
                SetStatus(VisionSourceState.Ready);
            }

            private void SetStatus(
                VisionSourceState state,
                VisionSourceErrorKind errorKind = VisionSourceErrorKind.None,
                string message = null)
            {
                State = state;
                ErrorKind = errorKind;
                StatusMessage = message;
                StatusChanged?.Invoke();
            }
        }

        private sealed class FakeRoomConnectionService : IConvaiRoomConnectionService
        {
            private event Action ConnectedHandlers;
            private event Action<SessionStateChanged> SessionStateChangedHandlers;

            public int ConnectedSubscriberCount { get; private set; }

            /// <summary>Returns Video connection type to enable vision publishing in tests.</summary>
            public ConvaiConnectionType ConnectionType { get; set; } = ConvaiConnectionType.Video;

            public SessionState CurrentState { get; private set; } = SessionState.Disconnected;
            public bool IsConnected { get; set; }
            public bool HasRoomDetails => false;
            public bool HasPendingOwnershipReconnect => false;
            public ConversationInputMode ActiveConversationInputMode => ConversationInputMode.HandsFree;
            public IRoomFacade CurrentRoom { get; set; }
            public RTVIHandler RtvHandler => null;
            public MultiCharacterRoomSession CurrentMultiCharacterSession => null;

            public event Action Connected
            {
                add
                {
                    ConnectedSubscriberCount++;
                    ConnectedHandlers += value;
                }
                remove
                {
                    ConnectedSubscriberCount--;
                    ConnectedHandlers -= value;
                }
            }

#pragma warning disable CS0067
            public event Action<SessionError> OnSessionError;
            public event Action<ConversationInputMode> ConversationInputModeChanged;
#pragma warning restore CS0067
            public event Action<SessionStateChanged> OnSessionStateChanged
            {
                add => SessionStateChangedHandlers += value;
                remove => SessionStateChangedHandlers -= value;
            }

            public void RaiseConnected()
            {
                SessionState previousState = CurrentState;
                CurrentState = SessionState.Connected;
                IsConnected = true;
                ConnectedHandlers?.Invoke();
                SessionStateChangedHandlers?.Invoke(
                    SessionStateChanged.Create(previousState, SessionState.Connected, "video-test-session"));
            }

            public void RaiseState(SessionState state)
            {
                SessionState previousState = CurrentState;
                CurrentState = state;
                IsConnected = state == SessionState.Connected;
                SessionStateChangedHandlers?.Invoke(
                    SessionStateChanged.Create(previousState, state, "video-test-session"));
            }

            public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken cancellationToken = default) =>
                ConvaiOperation<RoomSession>.Succeeded(new RoomSession(
                    "video-test-session",
                    "video-test-room",
                    "local-player",
                    DateTime.UtcNow));

            public IConvaiOperation<RoomSession> ConnectAsync(
                RoomSessionConnectOptions options,
                CancellationToken cancellationToken = default) =>
                ConnectAsync(cancellationToken);

            public IConvaiOperation<RoomSession> JoinMultiCharacterRoomAsync(
                MultiCharacterJoinOptions options,
                CancellationToken cancellationToken = default) =>
                ConnectAsync(cancellationToken);

            public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
                IConvaiCharacterAgent character,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<InteractionTargetResult>.Succeeded(default);

            public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
                string membershipId,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<InteractionTargetResult>.Succeeded(default);

            public IConvaiOperation<InteractionTargetResult> ClearInteractionTargetAsync(
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<InteractionTargetResult>.Succeeded(default);

            public IConvaiOperation<CharacterRosterUpdateResult> AddCharacterAsync(
                IConvaiCharacterAgent character,
                string characterSessionId = null,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

            public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
                string membershipId,
                string replacementTargetMembershipId = null,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

            public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
                IConvaiCharacterAgent character,
                string replacementTargetMembershipId = null,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

            public IConvaiOperation<Unit> DisconnectAsync(
                DisconnectReason reason = DisconnectReason.ClientInitiated,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<Unit>.Succeeded(
                    Unit.Value);

            public IConvaiOperation<Unit> SetConversationInputModeAsync(
                ConversationInputMode mode,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<Unit>.Succeeded(Unit.Value);

            public bool SendNarrativeTrigger(ConvaiNarrativeTriggerRequest request) => false;
            public bool UpdateSceneMetadata(IReadOnlyList<RtviSceneMetadata> sceneMetadata) => false;
            public bool UpdateTemplateKeys(Dictionary<string, string> templateKeys) => false;
            public bool SetTtsEnabled(bool ttsEnabled) => false;
            public bool SetSttMuted(bool muted) => false;
            public bool InterruptBot() => false;
            public bool KillPipeline() => false;
            public bool ForceUserStoppedSpeaking() => false;
            public bool ResetIdleTimer() => false;
            public bool RequestVisionStatus(string updateId = null) => false;
            public bool TriggerVision(ConvaiVisionTriggerRequest request) => false;
            public bool UpdateRespondMode(ConvaiRespondModeLane lane, ConvaiRespondMode mode, string updateId = null) => false;
        }

        private sealed class FakeVideoSourceFactory : IVideoSourceFactory
        {
            public List<FakeVideoSource> CreatedSources { get; } = new();

            public IVideoSource CreateFromRenderTexture(RenderTexture texture, string name = null)
            {
                var source = new FakeVideoSource(name ?? "fake-video-source");
                CreatedSources.Add(source);
                return source;
            }

            public IVideoSource CreateFromCamera(Camera camera, int width, int height, string name = null) => null;
            public IVideoSource CreateFromCanvasCapture(string name = null, int targetFrameRate = 15)
            {
                var source = new FakeVideoSource(name ?? "fake-canvas-source");
                CreatedSources.Add(source);
                return source;
            }
        }

        private sealed class FakeVideoSource : IVideoSource
        {
            public FakeVideoSource(string name) => Name = name;

            public int StartCaptureCount { get; private set; }
            public int StopCaptureCount { get; private set; }
            public int DisposeCount { get; private set; }
            public string Name { get; }
            public bool IsCapturing { get; private set; }
            public int Width => 1280;
            public int Height => 720;

            public void StartCapture()
            {
                StartCaptureCount++;
                IsCapturing = true;
            }

            public void StopCapture()
            {
                StopCaptureCount++;
                IsCapturing = false;
            }

            public void Dispose()
            {
                DisposeCount++;
                IsCapturing = false;
            }
        }

        private sealed class FakeLocalVideoTrack : ILocalVideoTrack
        {
            public FakeLocalVideoTrack(IVideoSource source, string sid)
            {
                Source = source;
                Sid = sid;
                Name = source?.Name ?? "fake-video";
            }

            public IVideoSource Source { get; }
            public string Sid { get; }
            public string Name { get; }
            public TrackKind Kind => TrackKind.Video;
            public bool IsMuted { get; private set; }
            public bool IsPublished { get; private set; } = true;
            public event Action<bool> MuteChanged;

            public void SetMuted(bool muted)
            {
                IsMuted = muted;
                MuteChanged?.Invoke(muted);
            }

            public void MarkUnpublished() => IsPublished = false;
        }

        private sealed class FakeLocalParticipant : ILocalParticipant
        {
            private readonly List<ILocalTrack> _localTracks = new();

            public int PublishVideoCallCount { get; private set; }
            public int UnpublishCallCount { get; private set; }
            public List<VideoPublishOptions> PublishedOptions { get; } = new();
            public IReadOnlyList<ILocalTrack> LocalTracks => _localTracks;
            public string Sid => "local-participant";
            public string Identity => "local";
            public string Name => "Local";
            public ParticipantMetadata Metadata => default;
            public bool IsAgent => false;
            public event Action<ParticipantMetadata> MetadataUpdated { add { } remove { } }
            public event Action<ILocalTrack> TrackPublished;
            public event Action<ILocalTrack> TrackUnpublished;

            public Task<ILocalAudioTrack> PublishAudioTrackAsync(
                IAudioSource source,
                AudioPublishOptions options = default,
                CancellationToken ct = default) =>
                Task.FromResult<ILocalAudioTrack>(null);

            public Task<ILocalVideoTrack> PublishVideoTrackAsync(
                IVideoSource source,
                VideoPublishOptions options = default,
                CancellationToken ct = default)
            {
                PublishVideoCallCount++;
                PublishedOptions.Add(options);
                var track = new FakeLocalVideoTrack(source, $"video-track-{PublishVideoCallCount}");
                _localTracks.Add(track);
                TrackPublished?.Invoke(track);
                return Task.FromResult<ILocalVideoTrack>(track);
            }

            public Task UnpublishTrackAsync(ILocalTrack track, CancellationToken ct = default)
            {
                UnpublishCallCount++;
                if (track is FakeLocalVideoTrack videoTrack)
                    videoTrack.MarkUnpublished();
                _localTracks.Remove(track);
                TrackUnpublished?.Invoke(track);
                return Task.CompletedTask;
            }

            public void SetAudioMuted(bool muted) { }

            public void SetVideoMuted(bool muted) { }
        }

        private sealed class FakeRoomFacade : IRoomFacade
        {
            public string Sid => "room";
            public string Name => "Test Room";
            public RoomState State => RoomState.Connected;
            public bool IsConnected => true;
            public ILocalParticipant LocalParticipant { get; set; }
            public IReadOnlyList<IRemoteParticipant> RemoteParticipants => Array.Empty<IRemoteParticipant>();
            public IEnumerable<IParticipant> AllParticipants => Array.Empty<IParticipant>();
            public int RemoteParticipantCount => 0;
            public event Action<IRemoteParticipant> ParticipantJoined { add { } remove { } }
            public event Action<IRemoteParticipant> ParticipantLeft { add { } remove { } }
            public event Action<IParticipant, ParticipantMetadata> ParticipantMetadataUpdated { add { } remove { } }
            public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackSubscribed { add { } remove { } }
            public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackUnsubscribed { add { } remove { } }
            public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackSubscribed { add { } remove { } }
            public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackUnsubscribed { add { } remove { } }
            public event Action<TrackSubscriptionEventArgs> TrackSubscribed { add { } remove { } }
            public event Action<TrackSubscriptionEventArgs> TrackUnsubscribed { add { } remove { } }
            public event Action<RoomState> StateChanged { add { } remove { } }
            public event Action Reconnecting { add { } remove { } }
            public event Action Reconnected { add { } remove { } }
            public event Action<DisconnectReason> Disconnected { add { } remove { } }

            public IRemoteParticipant GetParticipantBySid(string sid) => null;
            public IRemoteParticipant GetParticipantByIdentity(string identity) => null;

            public bool TryGetParticipantBySid(string sid, out IRemoteParticipant participant)
            {
                participant = null;
                return false;
            }

            public bool TryGetParticipantByIdentity(string identity, out IRemoteParticipant participant)
            {
                participant = null;
                return false;
            }
        }

        private sealed class FakeTransportProvider : ITransportProvider
        {
            private readonly IVideoSourceFactory _videoSourceFactory = new FakeVideoSourceFactory();

            public TransportCapabilities Capabilities => TransportCapabilities.Native();
            public IRealtimeTransport CreateTransport() => null;
            public IMicrophoneSourceFactory CreateMicrophoneFactory() => null;
            public IAudioStreamFactory CreateAudioStreamFactory() => null;
            public IVideoSourceFactory CreateVideoSourceFactory() => _videoSourceFactory;
            public IConvaiRoomControllerFactory CreateRoomControllerFactory() => null;
        }

        private sealed class FakeModuleContext : IModuleContext
        {
            private readonly Dictionary<Type, object> _services = new();

            public FakeModuleContext(ConvaiRuntime runtime) => Runtime = runtime;

            public ConvaiRuntime Runtime { get; }
            public IEventHub Events => Runtime.Events;
            public IAgentRegistry Agents => Runtime.Agents;
            public ITransportProvider Transport => Runtime.Transport;
            public IRuntimePreferences Preferences => Runtime.RuntimePreferences;
            public ILogger Logger => null;
            public IConvaiRoomAudioService RoomAudio => null;
            public ICredentialProvider Credentials => null;

            public bool TryGetModuleService<TService>(out TService service) where TService : class
            {
                if (_services.TryGetValue(typeof(TService), out object value))
                {
                    service = value as TService;
                    return service != null;
                }

                service = null;
                return false;
            }

            public void ProvideModuleService<TService>(TService instance) where TService : class =>
                _services[typeof(TService)] = instance;
        }

        private static ConvaiRuntime CreateRuntime()
        {
            var registry = new AgentRegistry();
            return new ConvaiRuntimeBuilder()
                .UseEventHub(new EventHub(new ImmediateScheduler(), new TestLogger()))
                .UseAgentRegistry(registry)
                .UseTransport(new FakeTransportProvider())
                .UseRoomRuntime(() => new RoomRuntimeBuilder()
                    .WithConnectionAdapter(new TestRoomConnectionRuntimeAdapter())
                    .WithAudioAdapter(new TestRoomAudioRuntimeAdapter())
                    .WithAgentRegistry(registry)
                    .Build())
                .Build();
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestLogger : ILogger
        {
            public bool IsEnabled(LogLevel level, LogCategory category) => false;
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }
            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Debug(string message, LogCategory category = LogCategory.SDK) { }
            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Info(string message, LogCategory category = LogCategory.SDK) { }
            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Warning(string message, LogCategory category = LogCategory.SDK) { }
            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Error(string message, LogCategory category = LogCategory.SDK) { }
            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Error(Exception exception, string message, LogCategory category = LogCategory.SDK) { }
            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
        }

        private sealed class TestRoomConnectionRuntimeAdapter : IRoomConnectionRuntimeAdapter
        {
            public event Action<SessionStateChanged> StateChanged;

            public SessionState CurrentState => SessionState.Disconnected;
            public bool IsConnected => false;
            public string CurrentRoomName => string.Empty;
            public string CurrentSessionId => string.Empty;
            public bool CanConnect => true;
            public bool HasStarted => true;

            public Task<bool> WaitForStartCompletionAsync(CancellationToken ct) => Task.FromResult(true);
            public bool PrepareConnection() => true;
            public Task<bool> WaitForConnectionResolutionAsync(CancellationToken ct) => Task.FromResult(true);
            public Task<RoomConnectionAttemptResult> ConnectAsync(CancellationToken ct) =>
                Task.FromResult(RoomConnectionAttemptResult.Success());
            public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

            public void NotifyStateChanged(SessionStateChanged stateChanged) => StateChanged?.Invoke(stateChanged);
        }

        private sealed class TestRoomAudioRuntimeAdapter : IRoomAudioRuntimeAdapter
        {
            public bool IsMicMuted => false;
            public void SetMicMuted(bool muted) { }
            public Task StartListeningAsync(int microphoneIndex, CancellationToken ct) => Task.CompletedTask;
            public Task StopListeningAsync(CancellationToken ct) => Task.CompletedTask;
            public bool SetCharacterMuted(string characterId, bool muted) => true;
            public bool IsCharacterMuted(string characterId) => false;
        }

        private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 1000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate())
                    return;

                await Task.Delay(10);
            }

            Assert.Fail($"Condition not met within {timeoutMs}ms.");
        }

        private static ConvaiVisionPublisher CreatePublisher(GameObject go)
        {
            var publisher = go.AddComponent<ConvaiVisionPublisher>();
            typeof(ConvaiVisionPublisher)
                .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(publisher, null);
            return publisher;
        }

        [Test]
        public async Task RegisterAsync_ResolvesLocalFrameSource()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var go = new GameObject("publisher");

            try
            {
                var publisher = CreatePublisher(go);
                var frameSource = go.AddComponent<FakeVisionFrameSource>();

                await publisher.RegisterAsync(context);

                Assert.That(publisher.FrameSource, Is.SameAs(frameSource));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task StartAsync_UsesModuleContextConnectionService_And_StopAsync_UnregistersCallbacks()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var roomConnection = new FakeRoomConnectionService();
            context.ProvideModuleService<IConvaiRoomConnectionService>(roomConnection);

            var go = new GameObject("publisher");

            try
            {
                var publisher = CreatePublisher(go);
                go.AddComponent<FakeVisionFrameSource>();

                await publisher.RegisterAsync(context);
                await publisher.StartAsync(context);

                Assert.That(roomConnection.ConnectedSubscriberCount, Is.EqualTo(1));

                await publisher.StopAsync();

                Assert.That(roomConnection.ConnectedSubscriberCount, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task StartAsync_ManualPolicy_DoesNotAutoStartCapture_WhenAlreadyConnected()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var roomConnection = new FakeRoomConnectionService
            {
                IsConnected = true
            };
            context.ProvideModuleService<IConvaiRoomConnectionService>(roomConnection);

            var go = new GameObject("publisher");

            try
            {
                var publisher = CreatePublisher(go);
                var frameSource = go.AddComponent<FakeVisionFrameSource>();

                publisher.SetPublishPolicy(VisionPublishPolicy.Manual);

                await publisher.RegisterAsync(context);
                await publisher.StartAsync(context);
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task EnablePublishing_ManualPolicy_StartsCapture_WhenConnected()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var roomConnection = new FakeRoomConnectionService
            {
                IsConnected = true
            };
            context.ProvideModuleService<IConvaiRoomConnectionService>(roomConnection);

            var go = new GameObject("publisher");

            try
            {
                var publisher = CreatePublisher(go);
                var frameSource = go.AddComponent<FakeVisionFrameSource>();

                publisher.SetPublishPolicy(VisionPublishPolicy.Manual);

                await publisher.RegisterAsync(context);
                await publisher.StartAsync(context);

                publisher.EnablePublishing(true);
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task SetPublishingEnabled_ReenabledBeforeConnect_KeepsConnectedSubscriptionAndPublishesLater()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                coordinator.SetPublishingEnabled(false);
                coordinator.SetPublishingEnabled(true);

                Assert.That(roomConnection.ConnectedSubscriberCount, Is.EqualTo(1));

                roomConnection.RaiseConnected();
                await Task.Yield();
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.True);
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task ApplyConfiguration_PreservesExplicitDisableAcrossRefresh()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                await Task.Yield();
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));

                coordinator.SetPublishingEnabled(false);
                await Task.Yield();

                coordinator.ApplyConfiguration(
                    VisionPublishPolicy.AutoCompatible,
                    0,
                    0,
                    "unity-scene");
                roomConnection.RaiseConnected();
                await Task.Yield();

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));
                Assert.That(frameSource.IsCapturing, Is.False);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task ApplyConfiguration_ManualPolicyWithoutExplicitEnable_DoesNotAutoPublish()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);
                coordinator.ApplyConfiguration(VisionPublishPolicy.Manual, 0, 0, "unity-scene");

                await coordinator.StartAsync();
                await Task.Yield();

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));
                Assert.That(frameSource.IsCapturing, Is.False);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task ApplyConfiguration_WhenPublishingOptionsChange_RepublishesWithNewOptions()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                await Task.Yield();
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));

                coordinator.ApplyConfiguration(
                    VisionPublishPolicy.HighResponsiveness,
                    12,
                    900_000,
                    "new-track");
                await Task.Yield();
                await Task.Yield();

                Assert.That(localParticipant.UnpublishCallCount, Is.EqualTo(1));
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(2));
                Assert.That(localParticipant.PublishedOptions[1].TrackName, Is.EqualTo("new-track"));
                Assert.That(localParticipant.PublishedOptions[1].MaxFrameRate, Is.EqualTo(12));
                Assert.That(localParticipant.PublishedOptions[1].MaxBitrate, Is.EqualTo(900_000));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_StatusSourceTimeout_DoesNotHangAndAllowsLaterPublish()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(100));

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(180);

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));

                frameSource.SetReady();
                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_StatusSourceDelayedFrame_PublishesBeforeTimeout()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(300));

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(50);
                frameSource.SetReady();

                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_CancellationBeforeTimeout_PropagatesCancellation()
        {
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();

            try
            {
                MethodInfo waitForFrameSourceReady = typeof(VisionPublishCoordinator).GetMethod(
                    "WaitForFrameSourceReadyAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(waitForFrameSourceReady, Is.Not.Null);

                var waitTask = (Task)waitForFrameSourceReady.Invoke(
                    coordinator,
                    new object[] { frameSource, cts.Token });
                cts.Cancel();

                OperationCanceledException exception =
                    await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(waitTask,
                        "Cancelling before the timeout must cancel the readiness wait.");
                Assert.That(exception.CancellationToken, Is.EqualTo(cts.Token));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
            }
        }

        [Test]
        public async Task PublishWait_AlreadyCancelledReadySource_PropagatesCancellation()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                frameSource.StartCapture();
                cts.Cancel();

                MethodInfo waitForFrameSourceReady = typeof(VisionPublishCoordinator).GetMethod(
                    "WaitForFrameSourceReadyAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(waitForFrameSourceReady, Is.Not.Null);

                var waitTask = (Task)waitForFrameSourceReady.Invoke(
                    coordinator,
                    new object[] { frameSource, cts.Token });

                OperationCanceledException exception =
                    await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(waitTask,
                        "An already-cancelled token must cancel the readiness wait.");
                Assert.That(exception.CancellationToken, Is.EqualTo(cts.Token));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_AwaitingPermission_SuspendsTimeoutUntilSourceReady()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(100));

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.StartState = VisionSourceState.AwaitingPermission;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(220);

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));

                frameSource.SetReady();
                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_SourceFailure_AbortsCurrentAttemptAndAllowsRecovery()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(300));

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(40);

                frameSource.SetFailed(VisionSourceErrorKind.DeviceUnavailable, "camera offline");
                await Task.Delay(80);

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));

                frameSource.SetReady();
                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_LegacySource_DelayedFrameStillPublishes()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(300));

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(50);
                frameSource.MarkFrameReady();

                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public void OnValidate_ClampsPublishOverridesToNonNegativeValues()
        {
            var go = new GameObject("publisher");

            try
            {
                var publisher = CreatePublisher(go);
                typeof(ConvaiVisionPublisher).GetField("publishFrameRateOverride")?.SetValue(publisher, -3);
                typeof(ConvaiVisionPublisher).GetField("publishBitrateOverride")?.SetValue(publisher, -99);

                typeof(ConvaiVisionPublisher)
                    .GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(publisher, null);

                Assert.That(publisher.publishFrameRateOverride, Is.EqualTo(0));
                Assert.That(publisher.publishBitrateOverride, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
