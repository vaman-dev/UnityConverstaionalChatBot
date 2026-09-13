using System;
using System.Threading.Tasks;
using Convai.Runtime.Core.Providers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Core
{
    public sealed class ConvaiConversationProviderTests
    {
        [Test]
        public async Task ConvaiConversationSession_SendWarning_DoesNotIncludeRequestText()
        {
            const string characterId = "character-123";
            const string requestText = "SENSITIVE_REQUEST_BODY_SENTINEL";
            string capturedWarning = null;

            void CaptureWarning(string condition, string _, LogType type)
            {
                if (type == LogType.Warning &&
                    condition.StartsWith("[ConvaiConversationSession]", StringComparison.Ordinal))
                    capturedWarning = condition;
            }

            IConversationSession session = await ConvaiConversationProvider.Instance
                .CreateSessionAsync(new ConversationSessionRequest(characterId, "player-123"))
                .AsTask();
            LogAssert.Expect(
                LogType.Warning,
                $"[ConvaiConversationSession] SendAsync called but conversation is handled by RTVIHandler. " +
                $"Character: {characterId}. Request content omitted from logs.");

            UnityEngine.Application.logMessageReceived += CaptureWarning;
            try
            {
                await session.SendAsync(new ConversationRequest(requestText)).AsTask();
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= CaptureWarning;
                await session.DisposeAsync();
            }

            Assert.That(capturedWarning, Is.Not.Null);
            Assert.That(capturedWarning, Does.Not.Contain(requestText));
        }
    }
}
