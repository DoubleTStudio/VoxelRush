using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoxelRush.Core.Hosting
{
    /// <summary>Optional client command-line overrides used by desktop/headless test clients.</summary>
    public sealed class ClientLaunchOptions
    {
        public const string ConnectArgument = "-connect";
        public const string SmokeTestSecondsArgument = "-smokeTestSeconds";
        public const int MaxSmokeTestSeconds = 600;

        public ClientLaunchOptions(ServerEndpoint? connect, int? smokeTestSeconds)
        {
            if (smokeTestSeconds is int seconds && (seconds < 1 || seconds > MaxSmokeTestSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(smokeTestSeconds));
            }

            if (smokeTestSeconds.HasValue && !connect.HasValue)
            {
                throw new ArgumentException("A smoke test needs an endpoint to connect to.", nameof(smokeTestSeconds));
            }

            Connect = connect;
            SmokeTestSeconds = smokeTestSeconds;
        }

        /// <summary>When set, the client connects to this server on startup.</summary>
        public ServerEndpoint? Connect { get; }

        /// <summary>When set, the client exits after staying connected this long (or fails if it never connects).</summary>
        public int? SmokeTestSeconds { get; }

        public static ClientLaunchParseResult Parse(IReadOnlyList<string> args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            var errors = new List<string>();
            ServerEndpoint? connect = null;
            int? smokeTestSeconds = null;

            for (int i = 0; i < args.Count; i++)
            {
                bool isConnect = string.Equals(args[i], ConnectArgument, StringComparison.OrdinalIgnoreCase);
                bool isSmoke = string.Equals(args[i], SmokeTestSecondsArgument, StringComparison.OrdinalIgnoreCase);
                if (!isConnect && !isSmoke)
                {
                    continue;
                }

                if (i + 1 >= args.Count)
                {
                    errors.Add($"{args[i]} requires a value.");
                    continue;
                }

                string value = args[++i];
                if (isConnect)
                {
                    if (ServerEndpoint.TryParse(value, out ServerEndpoint endpoint, out string error))
                    {
                        connect = endpoint;
                    }
                    else
                    {
                        errors.Add($"{ConnectArgument}: {error}.");
                    }
                }
                else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds >= 1 && seconds <= MaxSmokeTestSeconds)
                {
                    smokeTestSeconds = seconds;
                }
                else
                {
                    errors.Add($"{SmokeTestSecondsArgument} must be an integer in 1..{MaxSmokeTestSeconds} (got '{value}').");
                }
            }

            if (errors.Count == 0 && smokeTestSeconds.HasValue && !connect.HasValue)
            {
                errors.Add($"{SmokeTestSecondsArgument} requires {ConnectArgument}.");
            }

            return errors.Count == 0
                ? new ClientLaunchParseResult(new ClientLaunchOptions(connect, smokeTestSeconds), Array.Empty<string>())
                : new ClientLaunchParseResult(null, errors);
        }
    }

    public sealed class ClientLaunchParseResult
    {
        internal ClientLaunchParseResult(ClientLaunchOptions? options, IReadOnlyList<string> errors)
        {
            Options = options;
            Errors = errors;
        }

        public ClientLaunchOptions? Options { get; }

        public IReadOnlyList<string> Errors { get; }

        public bool IsSuccess => Options != null;
    }
}
