using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.NextcloudMemories.Streaming
{
    /// <summary>
    /// Issues and verifies HMAC tokens for the anonymous streaming proxy.
    /// </summary>
    public class StreamTokenService
    {
        /// <summary>
        /// Creates a token for a file id.
        /// </summary>
        /// <param name="fileId">The Nextcloud file id.</param>
        /// <returns>The hex encoded token.</returns>
        public string CreateToken(long fileId)
        {
            var secret = EnsureSecret();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var payload = Encoding.UTF8.GetBytes(fileId.ToString(CultureInfo.InvariantCulture));
            return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
        }

        /// <summary>
        /// Verifies a token in constant time.
        /// </summary>
        /// <param name="fileId">The Nextcloud file id.</param>
        /// <param name="token">The token from the query string.</param>
        /// <returns><c>true</c> when the token is valid.</returns>
        public bool Validate(long fileId, string? token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            var expected = CreateToken(fileId);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(token));
        }

        /// <summary>
        /// Returns the signing secret, generating and persisting one on first use.
        /// </summary>
        /// <returns>The secret.</returns>
        public static string EnsureSecret()
        {
            var plugin = Plugin.Instance
                         ?? throw new InvalidOperationException("Plugin is not initialised.");

            if (!string.IsNullOrWhiteSpace(plugin.Configuration.StreamSecret))
            {
                return plugin.Configuration.StreamSecret;
            }

            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            plugin.Configuration.StreamSecret = secret;
            plugin.SaveConfiguration();
            return secret;
        }
    }
}
