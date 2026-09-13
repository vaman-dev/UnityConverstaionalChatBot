using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Convai.CharacterApi;
using Convai.RestAPI;
using Convai.RestAPI.Services;
using Convai.RestAPI.Transport;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RestApiIdentityAndMemoryServiceTests
    {
        private CapturingTransport _transport = null!;
        private ConvaiRestClient _client = null!;

        [SetUp]
        public void SetUp()
        {
            _transport = new CapturingTransport();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("test-api-key")
            {
                CustomTransport = _transport
            });
        }

        [TearDown]
        public void TearDown() => _client.Dispose();

        [Test]
        public async Task EndUsers_ListAsync_SendsExpectedIdentityListPayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_users\":[],\"total_count\":0,\"has_more\":false}",
                request.Url);

            await _client.EndUsers.ListAsync(limit: 25, cursor: "cursor-1", activeAfter: "2026-01-01");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured end-user list request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/user/end-users/list"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["limit"]?.Value<int>(), Is.EqualTo(25));
            Assert.That(payload["cursor"]?.Value<string>(), Is.EqualTo("cursor-1"));
            Assert.That(payload["active_after"]?.Value<string>(), Is.EqualTo("2026-01-01"));
            Assert.That(payload["active_before"], Is.Null);
        }

        [Test]
        public async Task EndUsers_ListAsync_UsesIndependentPlatformBaseUrlAndCredential()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("platform-api-key")
            {
                CustomTransport = _transport,
                Environment = ConvaiEnvironment.Beta,
                BetaBaseUrl = "https://legacy-beta.example/",
                PlatformBaseUrl = "https://platform.example/"
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_users\":[],\"total_count\":0,\"has_more\":false}",
                request.Url);

            await _client.EndUsers.ListAsync();

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected platform request.");
            Assert.That(request.Url.ToString(), Is.EqualTo("https://platform.example/user/end-users/list"));
            Assert.That(request.Headers["CONVAI-API-KEY"], Is.EqualTo("platform-api-key"));
        }

        [Test]
        public async Task EndUsers_ListAsync_UsesPlatformAuthTokenHeaderWhenConfigured()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("short-lived-token")
            {
                AuthenticationMode = ConvaiAuthenticationMode.AuthToken,
                CustomTransport = _transport,
                PlatformBaseUrl = "https://platform.example/"
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_users\":[],\"total_count\":0,\"has_more\":false}",
                request.Url);

            await _client.EndUsers.ListAsync();

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected platform request.");
            Assert.That(request.Headers["API-AUTH-TOKEN"], Is.EqualTo("short-lived-token"));
            Assert.That(request.Headers.ContainsKey("CONVAI-API-KEY"), Is.False);
        }

        [Test]
        public async Task EndUsers_UpdateMetadataAsync_SendsPatchPayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_user_id\":\"player-42\",\"last_active_ts\":\"\",\"last_ltm_usage_ts\":\"\",\"end_user_metadata\":{\"name\":\"Player 42\",\"rank\":4},\"message\":\"ok\"}",
                request.Url);

            await _client.EndUsers.UpdateMetadataAsync(
                "player-42",
                new Dictionary<string, object>
                {
                    ["name"] = "Player 42",
                    ["rank"] = 4
                });

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured end-user update request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/user/end-users/update"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["end_user_id"]?.Value<string>(), Is.EqualTo("player-42"));
            Assert.That(payload["end_user_metadata"]?["name"]?.Value<string>(), Is.EqualTo("Player 42"));
            Assert.That(payload["end_user_metadata"]?["rank"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(payload["speaker_id"], Is.Null);
        }

        [Test]
        public async Task Memory_ListAsync_UsesCharacterAndEndUserIdentityOnly()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"memories\":[],\"total_count\":0,\"page\":1,\"page_size\":50,\"has_more\":false}",
                request.Url);

            await _client.Memory.ListAsync("char-123", "player-42", page: 2, pageSize: 10);

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured memory list request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/memory/list"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["character_id"]?.Value<string>(), Is.EqualTo("char-123"));
            Assert.That(payload["end_user_id"]?.Value<string>(), Is.EqualTo("player-42"));
            Assert.That(payload["page"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(payload["page_size"]?.Value<int>(), Is.EqualTo(10));
            Assert.That(payload["speaker_id"], Is.Null);
        }

        [Test]
        public async Task Characters_GetMemoryEnabledAsync_ReadsCharacterMemoryFlag()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"response\":{\"character_id\":\"char-123\",\"memory_settings\":{\"enabled\":true}}}",
                request.Url);

            bool enabled = await _client.Characters.GetMemoryEnabledAsync("char-123");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character details request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(enabled, Is.True);
        }

        [Test]
        public async Task Characters_GetMemoryEnabledAsync_UsesConfiguredEnvironmentBaseUrl()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("test-api-key")
            {
                CustomTransport = _transport,
                Environment = ConvaiEnvironment.Beta,
                BetaBaseUrl = "https://beta.example.convai/"
            });

            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"response\":{\"character_id\":\"char-123\",\"memory_settings\":{\"enabled\":false}}}",
                request.Url);

            await _client.Characters.GetMemoryEnabledAsync("char-123");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character details request.");

            Assert.That(request.Url.ToString(), Is.EqualTo("https://beta.example.convai/character/get"));
        }

        [Test]
        public async Task Characters_GetDetailsAsync_CharacterV2AdapterUsesCanonicalGetWithoutAuthoringClient()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("legacy-key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Preview("character-v2-key")
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"user_id\":\"user-1\",\"character_id\":\"character-42\"," +
                "\"character_name\":\"Test\",\"voice_type\":\"test\",\"language_code\":\"en\"," +
                "\"description\":\"\",\"model_type\":\"\",\"is_narrative_driven\":false," +
                "\"timestamp\":\"2026-01-01T00:00:00Z\",\"memory_settings\":{}," +
                "\"character_traits\":{},\"metadata_filter\":{}}",
                request.Url);

            CharacterDetails details = await _client.Characters.GetDetailsAsync("character-42");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected Character v2 request.");
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(request.Url.Query, Is.EqualTo("?character_id=character-42"));
            Assert.That(request.Headers["CONVAI-API-KEY"], Is.EqualTo("character-v2-key"));
            Assert.That(details.CharacterID, Is.EqualTo("character-42"));
        }

        [Test]
        public async Task Characters_UpdateAsync_MergesCharacterIdIntoPayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);

            await _client.Characters.UpdateAsync(
                "char-123",
                new Dictionary<string, object>
                {
                    ["displayName"] = "Guide"
                });

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character update request.");
            JObject payload = JObject.Parse(request.Body ?? "{}");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/update"));
            Assert.That(payload["charID"]?.Value<string>(), Is.EqualTo("char-123"));
            Assert.That(payload["displayName"]?.Value<string>(), Is.EqualTo("Guide"));
        }

        [Test]
        public async Task Characters_UpdateAsync_CharacterIdParameterWinsOverPayloadCharId()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);

            await _client.Characters.UpdateAsync(
                "char-authoritative",
                new Dictionary<string, object>
                {
                    ["charID"] = "char-wrong",
                    ["displayName"] = "Guide"
                });

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character update request.");
            JObject payload = JObject.Parse(request.Body ?? "{}");

            Assert.That(payload["charID"]?.Value<string>(), Is.EqualTo("char-authoritative"));
        }

        [Test]
        public async Task Characters_SetMemoryEnabledAsync_SendsFocusedUpdatePayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);

            await _client.Characters.SetMemoryEnabledAsync("char-123", enabled: true);

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character update request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/update"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["charID"]?.Value<string>(), Is.EqualTo("char-123"));
            Assert.That(payload["memorySettings"]?["enabled"]?.Value<bool>(), Is.True);
        }

        private sealed class CapturingTransport : IConvaiHttpTransport
        {
            [CanBeNull] public ConvaiHttpRequest LastRequest { get; private set; }
            [CanBeNull] public Func<ConvaiHttpRequest, ConvaiHttpResponse> ResponseFactory { get; set; }

            public Task<ConvaiHttpResponse> SendAsync(
                ConvaiHttpRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                ConvaiHttpResponse response = ResponseFactory?.Invoke(request)
                                              ?? ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);
                return Task.FromResult(response);
            }

            public Task<byte[]> DownloadBytesAsync(Uri url, CancellationToken cancellationToken = default) =>
                Task.FromResult(Array.Empty<byte>());

            public void Dispose()
            {
            }
        }
    }
}
