using System;
using System.Collections.Generic;
using Auth.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Auth.UnitTests
{
    public class JwtKeyValidatorTests
    {
        private IConfiguration CreateConfigWithKey(string? key)
        {
            var dict = new Dictionary<string, string?>();
            if (key != null)
            {
                dict["Token:Key"] = key;
            }
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        [Fact]
        public void GetValidatedSigningKey_Throws_WhenKeyIsMissing()
        {
            var config = CreateConfigWithKey(null);

            var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyValidator.GetValidatedSigningKey(config));
            Assert.Contains("missing or empty", ex.Message);
        }

        [Fact]
        public void GetValidatedSigningKey_Throws_WhenKeyIsEmptyOrWhitespace()
        {
            var config = CreateConfigWithKey("   ");

            var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyValidator.GetValidatedSigningKey(config));
            Assert.Contains("missing or empty", ex.Message);
        }

        [Theory]
        [InlineData("YOUR_JWT_SECRET_KEY_MIN_64_CHARS_LONG_HERE_REPLACE_IN_PRODUCTION")]
        [InlineData("DevelopmentOnlySecretKeyForLocalDebuggingMustBeAtLeast64BytesLongForSha512SecurityRequirement")]
        [InlineData("CHANGEME_some_long_string_that_is_at_least_sixty_four_characters_long_1234567890")]
        [InlineData("PLACEHOLDER_KEY_AT_LEAST_64_CHARS_LONG_FOR_TESTING_PURPOSES_1234567890")]
        public void GetValidatedSigningKey_Throws_WhenKeyContainsKnownPlaceholder(string placeholder)
        {
            var config = CreateConfigWithKey(placeholder);

            var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyValidator.GetValidatedSigningKey(config));
            Assert.Contains("insecure placeholder", ex.Message);
        }

        [Fact]
        public void GetValidatedSigningKey_Throws_WhenKeyIsShorterThan64Chars()
        {
            // 32 characters - inadequate for HMAC-SHA512
            var config = CreateConfigWithKey("ShortKeyThatIsOnly32CharsLong123");

            var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyValidator.GetValidatedSigningKey(config));
            Assert.Contains("shorter than the minimum security requirement", ex.Message);
        }

        [Fact]
        public void GetValidatedSigningKey_DoesNotLeakKeyInExceptionMessage()
        {
            var sensitiveKey = "ShortSecret123";
            var config = CreateConfigWithKey(sensitiveKey);

            var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyValidator.GetValidatedSigningKey(config));
            Assert.DoesNotContain(sensitiveKey, ex.Message);
        }

        [Fact]
        public void GetValidatedSigningKey_ReturnsTrimmedKey_WhenValid()
        {
            var validKey = "a1b2c3d4e5f67890123456789012345678901234567890123456789012345678901234567890"; // 76 chars
            var config = CreateConfigWithKey("  " + validKey + "  ");

            var result = JwtKeyValidator.GetValidatedSigningKey(config);

            Assert.Equal(validKey, result);
        }
    }
}
