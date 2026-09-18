using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Security
{
    public static class JwtKeyValidator
    {
        public const int MinimumKeyLength = 64; // 64 chars = 512 bits required for HMAC-SHA512

        private static readonly string[] KnownInsecureSubstrings = new[]
        {
            "YOUR_JWT",
            "REPLACE_IN",
            "DevelopmentOnlySecret",
            "CHANGEME",
            "PLACEHOLDER",
            "SECRET_KEY",
            "DEFAULT_KEY"
        };

        public static string GetValidatedSigningKey(IConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var key = configuration["Token:Key"];

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    "CRITICAL SECURITY ERROR: JWT signing key ('Token:Key') is missing or empty. " +
                    "In Development, configure it via User Secrets (dotnet user-secrets set \"Token:Key\" \"<secret>\") or Environment Variables. " +
                    "In Production, configure it via Environment Variables or Azure App Settings.");
            }

            var trimmed = key.Trim();

            foreach (var placeholder in KnownInsecureSubstrings)
            {
                if (trimmed.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException(
                        "CRITICAL SECURITY ERROR: JWT signing key ('Token:Key') contains an insecure placeholder. " +
                        "A genuine, cryptographically secure secret must be configured via User Secrets or Environment Variables.");
                }
            }

            if (trimmed.Length < MinimumKeyLength)
            {
                throw new InvalidOperationException(
                    $"CRITICAL SECURITY ERROR: JWT signing key ('Token:Key') length ({trimmed.Length}) is shorter than the minimum security requirement of {MinimumKeyLength} characters (512 bits) for HMAC-SHA512.");
            }

            return trimmed;
        }
    }
}
