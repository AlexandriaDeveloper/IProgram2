#nullable enable
using System;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Configuration
{
    public class SqlServerOptions
    {
        public const string SectionName = "SqlServer";

        public int MaxRetryCount { get; set; } = 5;
        public int MaxRetryDelaySeconds { get; set; } = 10;
        public int CommandTimeoutSeconds { get; set; } = 30;

        public static SqlServerOptions FromConfiguration(IConfiguration? configuration)
        {
            var options = new SqlServerOptions();
            if (configuration == null)
            {
                return options;
            }

            var section = configuration.GetSection(SectionName);
            if (section != null && section.Exists())
            {
                if (int.TryParse(section[nameof(MaxRetryCount)], out var retryCount) && retryCount > 0)
                {
                    options.MaxRetryCount = Math.Clamp(retryCount, 1, 10);
                }

                if (int.TryParse(section[nameof(MaxRetryDelaySeconds)], out var delaySeconds) && delaySeconds > 0)
                {
                    options.MaxRetryDelaySeconds = Math.Clamp(delaySeconds, 1, 60);
                }

                if (int.TryParse(section[nameof(CommandTimeoutSeconds)], out var timeoutSeconds) && timeoutSeconds > 0)
                {
                    options.CommandTimeoutSeconds = Math.Clamp(timeoutSeconds, 5, 120);
                }
            }

            return options;
        }
    }
}
