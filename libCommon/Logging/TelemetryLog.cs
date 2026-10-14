using Serilog;
using Serilog.Core;

namespace libCommon.Logging
{
    /// <summary>
    /// Telemetry for the test analyses (the memory governor's MEM status lines): written to the log file only when
    /// <see cref="Enabled"/> - which the test harness sets through CLONEZILLA_TELEMETRY=1, inherited by the exe it
    /// spawns - and never to the console. A user running the exe sees none of it. Events carry the <see cref="Property"/>
    /// property; the console sink excludes them.
    /// </summary>
    public static class TelemetryLog
    {
        public const string EnvironmentVariable = "CLONEZILLA_TELEMETRY";
        public const string Property = "Telemetry";

        /// <summary>Set once at startup from <see cref="EnvironmentVariable"/>; off by default.</summary>
        public static bool Enabled { get; set; }

        /// <summary>The logger to write telemetry through: silent unless <see cref="Enabled"/>. Taken per call, so it
        /// follows <see cref="Log.Logger"/> however late that is configured.</summary>
        public static ILogger Logger => Enabled ? Log.ForContext(Property, true) : Serilog.Core.Logger.None;
    }
}
