#nullable enable
using System;

namespace Convai.CharacterApi
{
    public enum CharacterApiCredentialKind
    {
        ApiKey,
        PersonalAccessToken
    }

    public sealed class ConvaiCharacterApiOptions
    {
        private static readonly Uri PreviewUri = new("https://api2-stg.convai.com/");

        private ConvaiCharacterApiOptions(
            Uri baseUri,
            string credential,
            CharacterApiCredentialKind credentialKind,
            bool allowCompatibility,
            TimeSpan timeout)
        {
            if (!baseUri.IsAbsoluteUri) throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));
            if (string.IsNullOrWhiteSpace(credential)) throw new ArgumentException("Credential is required.", nameof(credential));
            BaseUri = NormalizeBaseUri(baseUri);
            Credential = credential;
            CredentialKind = credentialKind;
            AllowCompatibilityOperations = allowCompatibility;
            Timeout = timeout;
        }

        public Uri BaseUri { get; }
        internal string Credential { get; }
        public CharacterApiCredentialKind CredentialKind { get; }
        public bool AllowCompatibilityOperations { get; }
        public TimeSpan Timeout { get; }

        public static ConvaiCharacterApiOptions Preview(
            string credential,
            CharacterApiCredentialKind credentialKind = CharacterApiCredentialKind.ApiKey,
            bool allowCompatibilityOperations = false,
            TimeSpan? timeout = null) =>
            new(PreviewUri, credential, credentialKind, allowCompatibilityOperations,
                timeout ?? TimeSpan.FromSeconds(30));

        public static ConvaiCharacterApiOptions Custom(
            Uri baseUri,
            string credential,
            CharacterApiCredentialKind credentialKind = CharacterApiCredentialKind.ApiKey,
            bool allowCompatibilityOperations = false,
            TimeSpan? timeout = null) =>
            new(baseUri, credential, credentialKind, allowCompatibilityOperations,
                timeout ?? TimeSpan.FromSeconds(30));

        private static Uri NormalizeBaseUri(Uri baseUri)
        {
            var builder = new UriBuilder(baseUri);
            if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
                builder.Path += "/";
            return builder.Uri;
        }
    }
}
