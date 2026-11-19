using Serilog;
using System;
using System.IO;

namespace Zenvix.Shared
{
    public static class LoggingConfiguration
    {
        public static void Setup()
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "zenvix.log");
            
            // Debug logging is very chatty (file I/O on every port/runtime check); opt in with ZENVIX_DEBUG=1.
            var debug = Environment.GetEnvironmentVariable("ZENVIX_DEBUG") == "1";
            var config = new LoggerConfiguration();
            config = debug ? config.MinimumLevel.Debug() : config.MinimumLevel.Information();

            Log.Logger = config
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("Zenvix logging initialized at {LogPath}", logPath);
        }
    }
}
