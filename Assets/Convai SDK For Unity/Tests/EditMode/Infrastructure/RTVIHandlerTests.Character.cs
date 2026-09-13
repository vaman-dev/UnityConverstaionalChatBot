using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.RestAPI.Internal;
using Convai.Runtime.Room;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [Test]
        public void BotReady_PublishesResolvedCharacter()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            CharacterReady captured = default;
            context.EventHub.Subscribe<CharacterReady>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-ready", "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
        }

        [Test]
        public void CharacterStatusReady_PublishesReadyEventWithMembershipAndParticipantIdentity()
        {
            RtviTestContext context = CreateContext();
            var character = new TestCharacterAgent("char-1", "Camila");
            context.Registry.RegisterCharacter(character);
            var membershipDetails = new JObject
            {
                ["membership_id"] = "membership-1",
                ["character_id"] = "char-1",
                ["character_session_id"] = "character-session-1",
                ["participant_identity"] = "character:membership-1",
                ["is_initial"] = true,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
            MultiCharacterRoomSession session = context.Registry.Configure(
                new RoomDetails(
                    "token",
                    "room-name",
                    "session-1",
                    "wss://room",
                    roomSessionId: "room-1",
                    characters: new List<RoomCharacterDetails> { membershipDetails }),
                context.Registry.Characters);
            CharacterReady captured = default;
            int readyEventCount = 0;
            context.EventHub.Subscribe<CharacterReady>(evt =>
            {
                captured = evt;
                readyEventCount++;
            });

            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "character-status",
                "participant-1",
                new JObject
                {
                    ["status"] = "ready",
                    ["about"] = new JObject
                    {
                        ["membership_id"] = "membership-1",
                        ["character_id"] = "char-1",
                        ["character_session_id"] = "character-session-1",
                        ["participant_identity"] = "character:membership-1",
                        ["is_initial"] = true,
                        ["roster_epoch"] = 1
                    }
                }));

            Assert.AreEqual(1, readyEventCount);
            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual("membership-1", captured.MembershipId);
            Assert.AreEqual("character-session-1", captured.CharacterSessionId);
            Assert.AreEqual("character:membership-1", captured.ParticipantIdentity);
            Assert.IsTrue(session.IsReady);
            Assert.AreEqual("participant-1", session.InitialCharacter.ParticipantId);
        }

        [Test]
        public void CharacterStatusReady_RelayedByAnotherCharacter_PreservesBothParticipantBindings()
        {
            RtviTestContext context = CreateContext();
            var characterA = new TestCharacterAgent("char-a", "Camila");
            var characterB = new TestCharacterAgent("char-b", "Marcus");
            context.Registry.RegisterCharacter(characterA);
            context.Registry.RegisterCharacter(characterB);
            var membershipA = new JObject
            {
                ["membership_id"] = "membership-a",
                ["character_id"] = "char-a",
                ["character_session_id"] = "character-session-a",
                ["participant_identity"] = "character:membership-a",
                ["is_initial"] = true,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
            var membershipB = new JObject
            {
                ["membership_id"] = "membership-b",
                ["character_id"] = "char-b",
                ["character_session_id"] = "character-session-b",
                ["participant_identity"] = "character:membership-b",
                ["is_initial"] = false,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
            MultiCharacterRoomSession session = context.Registry.Configure(
                new RoomDetails(
                    "token",
                    "room-name",
                    "session-1",
                    "wss://room",
                    roomSessionId: "room-1",
                    characters: new List<RoomCharacterDetails> { membershipA, membershipB }),
                context.Registry.Characters);
            CharacterRoomMembership roomCharacterA = session.FindByMembershipId("membership-a");
            CharacterRoomMembership roomCharacterB = session.FindByMembershipId("membership-b");
            session.MarkReady(roomCharacterA, "participant-a");
            session.MarkReady(roomCharacterB, "participant-b");
            context.Registry.SetParticipantId("char-a", "participant-a");
            context.Registry.SetParticipantId("char-b", "participant-b");
            CharacterReady captured = default;
            context.EventHub.Subscribe<CharacterReady>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "character-status",
                "participant-a",
                new JObject
                {
                    ["status"] = "ready",
                    ["about"] = new JObject
                    {
                        ["membership_id"] = "membership-b",
                        ["character_id"] = "char-b",
                        ["character_session_id"] = "character-session-b",
                        ["participant_identity"] = "character:membership-b",
                        ["is_initial"] = false,
                        ["roster_epoch"] = 2
                    }
                }));

            Assert.AreEqual("participant-a", roomCharacterA.ParticipantId);
            Assert.AreEqual("participant-b", roomCharacterB.ParticipantId);
            Assert.IsTrue(context.Registry.TryGetCharacterByParticipantId("participant-a", out var resolvedA));
            Assert.AreSame(characterA, resolvedA);
            Assert.AreEqual("char-b", captured.CharacterId);
            Assert.AreEqual("participant-b", captured.ParticipantId);
        }

        [Test]
        public void BotLifecycle_LogOnlyRoutes_AreRegistered()
        {
            RtviTestContext context = CreateContext();

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-stopped", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-tts-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-tts-stopped", "participant-1"));

            Assert.IsTrue(context.Logger.Contains("Character LLM stopped"));
            Assert.IsTrue(context.Logger.Contains("Character TTS started"));
            Assert.IsTrue(context.Logger.Contains("Character TTS stopped"));
        }

        [Test]
        public void BotTurnCompleted_ClearsGeneratedResponseIdentity()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            var transcripts = new List<CharacterTranscriptReceived>();
            context.EventHub.Subscribe<CharacterTranscriptReceived>(transcripts.Add);

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "First", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-turn-completed", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "Second", "participant-1"));

            Assert.AreEqual(2, transcripts.Count);
            Assert.AreNotEqual(transcripts[0].ResponseId, transcripts[1].ResponseId);
        }

        [Test]
        public void BotLlmStarted_BeginsNewResponseIdentityWithoutWaitingForPriorCompletion()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            var transcripts = new List<CharacterTranscriptReceived>();
            context.EventHub.Subscribe<CharacterTranscriptReceived>(transcripts.Add);

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-output", "Interrupted answer", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-output", "Next answer", "participant-1"));

            Assert.AreEqual(2, transcripts.Count);
            Assert.AreNotEqual(transcripts[0].ResponseId, transcripts[1].ResponseId);
            Assert.AreNotEqual(transcripts[0].TurnId, transcripts[1].TurnId);
        }

        [Test]
        public void ServerMessage_BotEmotion_PublishesResolvedEmotion()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            CharacterEmotionChanged captured = default;
            context.EventHub.Subscribe<CharacterEmotionChanged>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "bot-emotion",
                new Newtonsoft.Json.Linq.JObject { ["emotion"] = "happy", ["scale"] = 3 },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("happy", captured.Emotion);
            Assert.AreEqual(3, captured.Intensity);
        }
    }
}
