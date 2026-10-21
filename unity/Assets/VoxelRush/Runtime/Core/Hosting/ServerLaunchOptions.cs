using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoxelRush.Core.Hosting
{
    /// <summary>Validated dedicated server launch configuration parsed from command-line arguments.</summary>
    public sealed class ServerLaunchOptions
    {
        public const ushort DefaultPort = 7777;

        public const string PortArgument = "-port";
        public const string MaxPlayersArgument = "-maxPlayers";
        public const string SmokeTestTicksArgument = "-smokeTestTicks";
        public const string WorldDirArgument = "-worldDir";
        public const string RaceAutoStartArgument = "-raceAutoStart";
        private const int MaxWorldDirLength = 512;

        // Upper bound keeps a misconfigured smoke run from hanging CI for hours.
        public const int MaxSmokeTestTicks = SimulationConstants.ServerTickRate * 600;

        public ServerLaunchOptions(ushort port, int maxPlayers, int? smokeTestTicks, string? worldDir = null, int? raceAutoStart = null)
        {
            if (port == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be non-zero.");
            }

            if (maxPlayers < 1 || maxPlayers > SimulationConstants.MaxPlayersPerServer)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            }

            if (smokeTestTicks is int ticks && (ticks < 1 || ticks > MaxSmokeTestTicks))
            {
                throw new ArgumentOutOfRangeException(nameof(smokeTestTicks));
            }

            Port = port;
            MaxPlayers = maxPlayers;
            SmokeTestTicks = smokeTestTicks;
            if (raceAutoStart is int racers && (racers < 1 || racers > maxPlayers))
            {
                throw new ArgumentOutOfRangeException(nameof(raceAutoStart));
            }

            WorldDir = worldDir;
            RaceAutoStart = raceAutoStart;
        }

        public static ServerLaunchOptions Default { get; } =
            new ServerLaunchOptions(DefaultPort, SimulationConstants.MaxPlayersPerServer, null);

        public ushort Port { get; }

        public int MaxPlayers { get; }

        /// <summary>When set, the server exits successfully after this many ticks.</summary>
        public int? SmokeTestTicks { get; }

        /// <summary>Directory for the world journal and snapshots; null uses the default under $HOME.</summary>
        public string? WorldDir { get; }

        /// <summary>When set, a race countdown starts as soon as this many players are waiting (test/dev lobbies).</summary>
        public int? RaceAutoStart { get; }

        /// <summary>
        /// Parses known arguments; unknown arguments are ignored because the Unity player
        /// receives its own flags (-batchmode, -logFile, ...) on the same command line.
        /// </summary>
        public static ServerLaunchParseResult Parse(IReadOnlyList<string> args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            var errors = new List<string>();
            ushort port = DefaultPort;
            int maxPlayers = SimulationConstants.MaxPlayersPerServer;
            int? smokeTestTicks = null;
            string? worldDir = null;
            int? raceAutoStart = null;

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (!IsKnown(arg))
                {
                    continue;
                }

                if (i + 1 >= args.Count)
                {
                    errors.Add($"{arg} requires a value.");
                    continue;
                }

                string value = args[++i];
                if (Matches(arg, PortArgument))
                {
                    if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port == 0)
                    {
                        errors.Add($"{PortArgument} must be an integer in 1..65535 (got '{value}').");
                        port = DefaultPort;
                    }
                }
                else if (Matches(arg, MaxPlayersArgument))
                {
                    if (!TryParseBounded(value, 1, SimulationConstants.MaxPlayersPerServer, out maxPlayers))
                    {
                        errors.Add($"{MaxPlayersArgument} must be an integer in 1..{SimulationConstants.MaxPlayersPerServer} (got '{value}').");
                        maxPlayers = SimulationConstants.MaxPlayersPerServer;
                    }
                }
                else if (Matches(arg, WorldDirArgument))
                {
                    if (value.Length == 0 || value.Length > MaxWorldDirLength || value.IndexOf('\0') >= 0)
                    {
                        errors.Add($"{WorldDirArgument} must be a path of 1..{MaxWorldDirLength} characters.");
                    }
                    else
                    {
                        worldDir = value;
                    }
                }
                else if (Matches(arg, RaceAutoStartArgument))
                {
                    if (TryParseBounded(value, 1, SimulationConstants.MaxPlayersPerServer, out int racers))
                    {
                        raceAutoStart = racers;
                    }
                    else
                    {
                        errors.Add($"{RaceAutoStartArgument} must be an integer in 1..{SimulationConstants.MaxPlayersPerServer} (got '{value}').");
                    }
                }
                else if (Matches(arg, SmokeTestTicksArgument))
                {
                    if (TryParseBounded(value, 1, MaxSmokeTestTicks, out int ticks))
                    {
                        smokeTestTicks = ticks;
                    }
                    else
                    {
                        errors.Add($"{SmokeTestTicksArgument} must be an integer in 1..{MaxSmokeTestTicks} (got '{value}').");
                    }
                }
            }

            if (raceAutoStart > maxPlayers)
            {
                errors.Add($"{RaceAutoStartArgument} can't exceed {MaxPlayersArgument} ({maxPlayers}).");
            }

            return errors.Count == 0
                ? ServerLaunchParseResult.Success(new ServerLaunchOptions(port, maxPlayers, smokeTestTicks, worldDir, raceAutoStart))
                : ServerLaunchParseResult.Failure(errors);
        }

        private static bool IsKnown(string arg) =>
            Matches(arg, PortArgument) || Matches(arg, MaxPlayersArgument) || Matches(arg, SmokeTestTicksArgument) || Matches(arg, WorldDirArgument) || Matches(arg, RaceAutoStartArgument);

        private static bool Matches(string arg, string name) =>
            string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

        private static bool TryParseBounded(string value, int min, int max, out int result) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= min
            && result <= max;
    }

    public sealed class ServerLaunchParseResult
    {
        private ServerLaunchParseResult(ServerLaunchOptions? options, IReadOnlyList<string> errors)
        {
            Options = options;
            Errors = errors;
        }

        public ServerLaunchOptions? Options { get; }

        public IReadOnlyList<string> Errors { get; }

        public bool IsSuccess => Options != null;

        internal static ServerLaunchParseResult Success(ServerLaunchOptions options) =>
            new ServerLaunchParseResult(options, Array.Empty<string>());

        internal static ServerLaunchParseResult Failure(IReadOnlyList<string> errors) =>
            new ServerLaunchParseResult(null, errors);
    }
}
