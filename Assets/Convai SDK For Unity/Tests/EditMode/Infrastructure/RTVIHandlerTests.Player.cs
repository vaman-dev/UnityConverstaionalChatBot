using System.Linq;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.Models;
using Convai.Infrastructure.Protocol.Messages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [Test]
        public void PlayerSpeech_OrdinaryCycle_UsesNormalTranscriptionPath()
        {
            RtviTestContext context = CreateContext();

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("working", isFinal: false));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("worked", isFinal: true));
            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject { ["text"] = "Worked" }));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(1, context.PlayerSession.StartedSessionIds.Count);
            Assert.AreEqual(1, context.PlayerSession.StoppedSessions.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    TranscriptionPhase.Listening,
                    TranscriptionPhase.Interim,
                    TranscriptionPhase.AsrFinal,
                    TranscriptionPhase.ProcessedFinal,
                    TranscriptionPhase.Completed
                },
                context.PlayerSession.Transcriptions.Select(entry => entry.Phase).ToArray());
        }

        [Test]
        public void TypedText_MultiplePendingMessages_UsesNewestMessage()
        {
            RtviTestContext context = CreateContext();

            context.Handler.SendData(new RTVIUserTextMessage("first", "typed-1"));
            context.Handler.SendData(new RTVIUserTextMessage("second", "typed-2"));
            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject { ["text"] = "second" }));

            Assert.AreEqual(1, context.PlayerSession.TypedTranscriptions.Count);
            Assert.AreEqual("typed-2", context.PlayerSession.TypedTranscriptions[0].MessageId);
            Assert.AreEqual("second", context.PlayerSession.TypedTranscriptions[0].Text);
        }

        [Test]
        public void PlayerSpeech_DuplicateStart_DoesNotStartSecondSession()
        {
            RtviTestContext context = CreateContext();

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));

            Assert.AreEqual(1, context.PlayerSession.StartedSessionIds.Count);
            Assert.IsTrue(context.Logger.Contains("duplicate user-started-speaking"));
        }

        [Test]
        public void PlayerSpeech_OrphanStopWithoutSession_IsIgnored()
        {
            RtviTestContext context = CreateContext();
            int stoppedEvents = 0;
            context.EventHub.Subscribe<PlayerSpeakingStateChanged>(evt =>
            {
                if (!evt.IsSpeaking) stoppedEvents++;
            });

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(0, stoppedEvents);
            Assert.IsEmpty(context.PlayerSession.StoppedSessions);
            Assert.IsTrue(context.Logger.Contains("without an active speech session"));
        }

        [Test]
        public void PlayerSpeech_InterimAfterPause_SynthesizesMatchingStartBeforeNextStop()
        {
            RtviTestContext context = CreateContext();
            int startedEvents = 0;
            int stoppedEvents = 0;
            context.EventHub.Subscribe<PlayerSpeakingStateChanged>(evt =>
            {
                if (evt.IsSpeaking)
                    startedEvents++;
                else
                    stoppedEvents++;
            });

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("first phrase", isFinal: false));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("second phrase", isFinal: false));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(2, startedEvents);
            Assert.AreEqual(2, stoppedEvents);
            Assert.IsFalse(context.Logger.Contains("without a preceding user-started-speaking"));
        }
    }
}
