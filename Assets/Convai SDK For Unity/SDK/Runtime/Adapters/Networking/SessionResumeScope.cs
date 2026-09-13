using System;
using System.Security.Cryptography;
using System.Text;
using Convai.Infrastructure.Networking.Models;

namespace Convai.Runtime.Adapters.Networking
{
    /// <summary>Creates non-identifying keys that isolate resumable sessions by user and topology.</summary>
    internal static class SessionResumeScope
    {
        internal static string CreateIdentityScope(string endUserId) =>
            Hash("identity|" + Normalize(endUserId));

        internal static string CreatePersistenceKey(
            string characterId,
            string endUserId,
            RoomJoinOptions joinOptions)
        {
            var material = new StringBuilder()
                .Append("v2|character=").Append(Normalize(characterId))
                .Append("|identity=").Append(CreateIdentityScope(endUserId))
                .Append("|mode=").Append(joinOptions?.IsJoinRequest == true ? "join" : "create")
                .Append("|room=").Append(Normalize(joinOptions?.RoomName))
                .Append("|room_session=").Append(Normalize(joinOptions?.ResolvedRoomSessionId))
                .Append("|shared=").Append(Normalize(joinOptions?.ResolvedSharedSessionKey));

            if (joinOptions?.ResolvedCharacterRoster != null)
            {
                material.Append("|roster=");
                foreach (var character in joinOptions.ResolvedCharacterRoster)
                    material.Append(Normalize(character?.CharacterId)).Append(',');
            }

            return "convai-session-v2:" + Hash(material.ToString());
        }

        private static string Normalize(string value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string Hash(string value)
        {
            using SHA256 sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var output = new StringBuilder(digest.Length * 2);
            foreach (byte item in digest)
                output.Append(item.ToString("x2"));
            return output.ToString();
        }
    }
}
