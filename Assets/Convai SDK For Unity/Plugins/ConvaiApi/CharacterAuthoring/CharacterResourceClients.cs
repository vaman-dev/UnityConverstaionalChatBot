#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.CharacterApi.Contracts;
using Convai.CharacterApi.Contracts.Model;

namespace Convai.CharacterApi
{
    public sealed class CharacterResourceClient : CharacterApiResourceClient
    {
        internal CharacterResourceClient(ConvaiCharacterApiClient client) : base(client, "character")
        {
        }

        public Task<CharacterResponse> GetAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<CharacterResponse>(
                CharacterApiOperations.CharacterGet,
                query: Query("character_id", characterId),
                cancellationToken: cancellationToken);

        public Task<PageCharacterListItem> ListAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<PageCharacterListItem>(
                CharacterApiOperations.CharacterList,
                query: new Dictionary<string, object?> { ["limit"] = limit, ["offset"] = offset },
                cancellationToken: cancellationToken);

        public Task<CharacterCreateResponse> CreateAsync(
            CharacterCreate request,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<CharacterCreateResponse>(
                CharacterApiOperations.CharacterCreate,
                body: request,
                cancellationToken: cancellationToken);

        public Task<CharacterResponse> UpdateAsync(
            CharacterUpdate request,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<CharacterResponse>(
                CharacterApiOperations.CharacterUpdate,
                body: request,
                cancellationToken: cancellationToken);

        public Task<Message> DeleteAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<Message>(
                CharacterApiOperations.CharacterDeleteCanonical,
                routeValues: Query("character_id", characterId),
                cancellationToken: cancellationToken);

        private static IReadOnlyDictionary<string, object?> Query(string name, object value) =>
            new Dictionary<string, object?> { [name] = value };
    }

    public sealed class NarrativeResourceClient : CharacterApiResourceClient
    {
        internal NarrativeResourceClient(ConvaiCharacterApiClient client) : base(client, "narrative")
        {
        }

        public Task<List<SectionResponse>> ListSectionsAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<List<SectionResponse>>(
                CharacterApiOperations.ListSectionsCharacterNarrativeSectionsListGet,
                query: Query(characterId),
                cancellationToken: cancellationToken);

        public Task<List<TriggerResponse>> ListTriggersAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<List<TriggerResponse>>(
                CharacterApiOperations.ListTriggersCharacterNarrativeTriggersListGet,
                query: Query(characterId),
                cancellationToken: cancellationToken);

        private static IReadOnlyDictionary<string, object?> Query(string characterId) =>
            new Dictionary<string, object?> { ["character_id"] = characterId };
    }

    public sealed class AnimationResourceClient : CharacterApiResourceClient
    {
        internal AnimationResourceClient(ConvaiCharacterApiClient client) : base(client, "animations")
        {
        }

        public Task<PageAnimationListItem> ListAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<PageAnimationListItem>(
                CharacterApiOperations.ListAnimationsAnimationsListGet,
                query: new Dictionary<string, object?> { ["limit"] = limit, ["offset"] = offset },
                cancellationToken: cancellationToken);

        public Task<AnimationResponse> GetAsync(
            string animationId,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync<AnimationResponse>(
                CharacterApiOperations.GetAnimationAnimationsGetGet,
                query: new Dictionary<string, object?> { ["animation_id"] = animationId },
                cancellationToken: cancellationToken);
    }
}
