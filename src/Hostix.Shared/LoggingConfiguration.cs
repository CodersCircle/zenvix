using Serilog;
using System;
using System.IO;

namespace Hostix.Shared
{
    public static class LoggingConfiguration
    {
        public static void Setup()
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "hostix.log");
            
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("Hostix logging initialized at {LogPath}", logPath);
        }
    }
}
