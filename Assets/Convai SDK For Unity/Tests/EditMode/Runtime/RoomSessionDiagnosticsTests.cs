using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Services;
using Convai.Runtime.Adapters.Networking;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class RoomSessionDiagnosticsTests
    {
        [Test]
        public void UpdateSessionState_ForcesInvalidTransition_WhenNeeded()
        {
            var logger = new TestLogger();
            var stateMachine = new SessionStateMachine(new EventHub(new ImmediateScheduler()), logger);
            var diagnostics = new RoomSessionDiagnostics(stateMachine, null, null, logger, () => "session-1");

            diagnostics.UpdateSessionState(SessionState.Connected);

            Assert.AreEqual(SessionState.Connected, stateMachine.CurrentState);
            Assert.IsTrue(logger.WarningMessages.Exists(message => message.Contains("forcing transition")));
        }

        [Test]
        public void RecordConnectionSuccess_SetsSessionTracking_AndInvokesConnectedCallback()
        {
            var sessionService = new TestSessionService();
            bool connected = false;
            var diagnostics = new RoomSessionDiagnostics(null, sessionService, null, null, () => "session-1",
                onConnected: () => connected = true);

            ConnectionContext context = diagnostics.RecordConnectionSuccess(
                "room-a", "character-session", "session-1", "character-a", true);

            Assert.AreEqual("room-a", context.RoomName);
            Assert.AreEqual("character-session", context.CharacterSessionId);
            Assert.AreEqual("session-1", context.SessionId);
            Assert.AreEqual("character-a", context.CharacterId);
            Assert.AreEqual("session-1", sessionService.ActiveSessionId);
            Assert.AreEqual("character-a", sessionService.ActiveCharacterId);
            Assert.AreEqual("character-session", sessionService.LoadStoredSession("character-a"));
            Assert.IsTrue(connected);
        }

        [Test]
        public void RecordConnectionFailure_PublishesSessionError_TransitionsState_AndInvokesCallback()
        {
            var logger = new TestLogger();
            var eventHub = new EventHub(new ImmediateScheduler());
            var stateMachine = new SessionStateMachine(eventHub, logger);
            SessionError? published = null;
            bool failed = false;
            eventHub.Subscribe<SessionError>(evt => published = evt, EventDeliveryPolicy.Immediate);
            var diagnostics = new RoomSessionDiagnostics(stateMachine, null, eventHub, logger, () => "session-42",
                onSessionError: _ => failed = true);

            diagnostics.RecordConnectionFailure(ConnectionFailure.Create(
                "connection.failed",
                "boom",
                SessionErrorStage.ConnectApi));

            Assert.AreEqual(SessionState.Error, stateMachine.CurrentState);
            Assert.IsTrue(published.HasValue);
            Assert.AreEqual("connection.failed", published.Value.ErrorCode);
            Assert.AreEqual("boom", published.Value.Message);
            Assert.AreEqual("session-42", published.Value.SessionId);
            Assert.IsTrue(failed);
        }

        [Test]
        public void CompleteDisconnectionTracking_ClearsActiveSession_AndMarksContextDisconnected()
        {
            var logger = new TestLogger();
            var stateMachine = new SessionStateMachine(new EventHub(new ImmediateScheduler()), logger);
            var sessionService = new TestSessionService();
            sessionService.SetActiveSession("character-a", "session-1");
            var diagnostics = new RoomSessionDiagnostics(stateMachine, sessionService, null, logger, () => "session-1");
            diagnostics.UpdateSessionState(SessionState.Connecting);
            diagnostics.UpdateSessionState(SessionState.Connected);
            diagnostics.UpdateSessionState(SessionState.Disconnecting);
            var context = new ConnectionContext("room-a", "character-session", "session-1", "character-a", DateTime.UtcNow.AddMinutes(-1));

            ConnectionContext updated = diagnostics.CompleteDisconnectionTracking(context, true, "Disconnected from room");

            Assert.AreEqual(SessionState.Disconnected, stateMachine.CurrentState);
            Assert.IsTrue(updated.DisconnectedAtUtc.HasValue);
            Assert.AreEqual(1, sessionService.ClearActiveSessionCallCount);
            Assert.IsTrue(logger.InfoMessages.Exists(message => message.Contains("Disconnected from room")));
        }

        [Test]
        public void HandleStateChanged_ForwardsCallbacks()
        {
            SessionStateChanged? forwarded = null;
            SessionState? forwardedState = null;
            var diagnostics = new RoomSessionDiagnostics(null, null, null, null, () => null,
                onSessionStateChanged: evt => forwarded = evt,
                onConnectionStateChanged: state => forwardedState = state);
            SessionStateChanged change = SessionStateChanged.Create(SessionState.Connecting, SessionState.Connected, "session-1");

            diagnostics.HandleStateChanged(change);

            Assert.IsTrue(forwarded.HasValue);
            Assert.AreEqual(SessionState.Connected, forwarded.Value.NewState);
            Assert.AreEqual(SessionState.Connected, forwardedState);
        }

        [Test]
        public void PublishOwnershipRebindState_PublishesEvent_AndInvokesCallback()
        {
            var eventHub = new EventHub(new ImmediateScheduler());
            RoomOwnershipRebindStateChanged? published = null;
            RoomOwnershipRebindStateChanged? forwarded = null;
            eventHub.Subscribe<RoomOwnershipRebindStateChanged>(evt => published = evt, EventDeliveryPolicy.Immediate);
            var diagnostics = new RoomSessionDiagnostics(
                null,
                null,
                eventHub,
                null,
                () => "session-1",
                onOwnershipRebindStateChanged: evt => forwarded = evt);

            RoomOwnershipRebindStateChanged stateChanged = diagnostics.PublishOwnershipRebindState(
                RoomOwnershipRebindStatus.PendingReconnect,
                true,
                SessionState.Connected,
                "char-a",
                "char-b");

            Assert.IsTrue(published.HasValue);
            Assert.AreEqual(RoomOwnershipRebindStatus.PendingReconnect, published.Value.Outcome);
            Assert.IsTrue(published.Value.HasPendingReconnect);
            Assert.AreEqual("char-a", published.Value.ActiveCharacterId);
            Assert.AreEqual("char-b", published.Value.RequestedCharacterId);
            Assert.IsTrue(forwarded.HasValue);
            Assert.AreEqual(stateChanged.Outcome, forwarded.Value.Outcome);
            Assert.AreEqual(stateChanged.RequestedCharacterId, forwarded.Value.RequestedCharacterId);
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestSessionService : ISessionService
        {
            private readonly Dictionary<string, string> _storedSessions = new(StringComparer.Ordinal);
            public string ActiveSessionId { get; private set; }
            public string ActiveCharacterId { get; private set; }
            public int ClearActiveSessionCallCount { get; private set; }
            public string LoadStoredSession(string characterId) => _storedSessions.TryGetValue(characterId, out string value) ? value : null;
            public void StoreSession(string characterId, string sessionId) => _storedSessions[characterId] = sessionId;
            public void ClearStoredSession(string characterId) => _storedSessions.Remove(characterId);
            public void ClearAllStoredSessions() => _storedSessions.Clear();
            public bool HasStoredSession(string characterId) => _storedSessions.ContainsKey(characterId);
            public bool ShouldResumeSession(string characterId, ResumePolicy policy) => HasStoredSession(characterId);
            public string GetSessionIdForConnection(string characterId, ResumePolicy policy, string explicitSessionId = null) => explicitSessionId ?? LoadStoredSession(characterId);
            public void SetActiveSession(string characterId, string sessionId) { ActiveCharacterId = characterId; ActiveSessionId = sessionId; }
            public void ClearActiveSession() { ClearActiveSessionCallCount++; ActiveCharacterId = null; ActiveSessionId = null; }
        }

        private sealed class TestLogger : ILogger
        {
            public readonly List<string> WarningMessages = new();
            public readonly List<string> InfoMessages = new();
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }
            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) { }
            public void Debug(string message, LogCategory category = LogCategory.SDK) { }
            public void Debug(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) { }
            public void Info(string message, LogCategory category = LogCategory.SDK) => InfoMessages.Add(message);
            public void Info(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) => Info(message, category);
            public void Warning(string message, LogCategory category = LogCategory.SDK) => WarningMessages.Add(message);
            public void Warning(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) => Warning(message, category);
            public void Error(string message, LogCategory category = LogCategory.SDK) { }
            public void Error(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) { }
            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }
            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) { }
            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }
    }
}
