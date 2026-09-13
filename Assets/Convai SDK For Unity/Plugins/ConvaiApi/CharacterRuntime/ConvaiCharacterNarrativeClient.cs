#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi.Contracts.Model;
using Newtonsoft.Json;

namespace Convai.CharacterApi.Runtime
{
    /// <summary>Minimal read-only Character API shipped with the default runtime.</summary>
    public sealed class ConvaiCharacterNarrativeClient : IDisposable
    {
        private readonly Uri _baseUri;
        private readonly string _apiKey;
        private readonly IConvaiApiTransport _transport;
        private readonly bool _ownsTransport;
        private readonly TimeSpan _timeout;

        public ConvaiCharacterNarrativeClient(
            Uri baseUri,
            string apiKey,
            TimeSpan? timeout = null,
            IConvaiApiTransport? transport = null)
        {
            if (baseUri == null || !baseUri.IsAbsoluteUri)
                throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key is required.", nameof(apiKey));
            _baseUri = baseUri;
            _apiKey = apiKey;
            _timeout = timeout ?? TimeSpan.FromSeconds(30);
            _transport = transport ?? ConvaiApiTransportFactory.Create(_timeout);
            _ownsTransport = transport == null;
        }

        public Task<IReadOnlyList<SectionResponse>> ListSectionsAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            GetAsync<SectionResponse>("character/narrative/sections/list", characterId, cancellationToken);

        public Task<IReadOnlyList<TriggerResponse>> ListTriggersAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            GetAsync<TriggerResponse>("character/narrative/triggers/list", characterId, cancellationToken);

        private async Task<IReadOnlyList<T>> GetAsync<T>(
            string path,
            string characterId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID is required.", nameof(characterId));
            var builder = new UriBuilder(new Uri(_baseUri, path));
            builder.Query = "character_id=" + Uri.EscapeDataString(characterId);
            var request = new ConvaiApiRequest(
                builder.Uri,
                ConvaiApiHttpMethod.Get,
                new Dictionary<string, string> { ["CONVAI-API-KEY"] = _apiKey },
                timeout: _timeout);
            ConvaiApiResponse response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess) throw ConvaiApiException.FromResponse(response);
            List<T>? result = JsonConvert.DeserializeObject<List<T>>(response.BodyText);
            if (result == null)
                throw new ConvaiApiException("Narrative response was null.", ConvaiApiErrorCategory.Serialization);
            return result;
        }

        public void Dispose()
        {
            if (_ownsTransport) _transport.Dispose();
        }
    }
}
