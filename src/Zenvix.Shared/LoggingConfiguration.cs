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
            
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("Zenvix logging initialized at {LogPath}", logPath);
        }
    }
}
