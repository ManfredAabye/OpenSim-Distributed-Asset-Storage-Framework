using System;
using System.Security.Cryptography;

namespace OpenSim.DataS3.Common
{
    /// <summary>
    /// Builds deterministic object keys for asset payloads.
    /// </summary>
    public static class AssetObjectKeyBuilder
    {
        /// <summary>
        /// Computes lower-case SHA256 hex for a payload.
        /// </summary>
        /// <param name="payload">Payload bytes.</param>
        /// <returns>64-char lower-case SHA256 hex string.</returns>
        public static string ComputeSha256Hex(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(payload);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Builds storage key using the agreed prefix strategy.
        /// </summary>
        /// <param name="assetType">OpenSim asset type.</param>
        /// <param name="sha256Hex">SHA256 hash as lower-case hex.</param>
        /// <returns>Object key like type-0/ab/cd/hash.</returns>
        public static string BuildStorageKey(int assetType, string sha256Hex)
        {
            if (string.IsNullOrWhiteSpace(sha256Hex) || sha256Hex.Length < 4)
                throw new ArgumentException("SHA256 value must contain at least 4 hex characters.", nameof(sha256Hex));

            string hash = sha256Hex.ToLowerInvariant();
            return $"type-{assetType}/{hash.Substring(0, 2)}/{hash.Substring(2, 2)}/{hash}";
        }
    }
}
