using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Hostix.Core.Models;
using Serilog;

namespace Hostix.Modules.Services
{
    public class DiagnosticReport
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string OSVersion { get; set; } = Environment.OSVersion.ToString();
        public List<Service> Services { get; set; } = new List<Service>();
        public List<string> RecentLogs { get; set; } = new List<string>();
        public bool IsAdmin { get; set; }
    }

    public interface IAIDiagnosticService
    {
        string GenerateJsonReport(IEnumerable<Service> activeServices);
        void SaveReport(string json);
    }

    public class AIDiagnosticService : IAIDiagnosticService
    {
        public string GenerateJsonReport(IEnumerable<Service> activeServices)
        {
            var report = new DiagnosticReport
            {
                Services = new List<Service>(activeServices),
                IsAdmin = IsUserAdmin()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(report, options);
        }

        public void SaveReport(string json)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics", "system_state.json");
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(path, json);
            Log.Information("AI Diagnostic Report saved to {Path}", path);
        }

        private bool IsUserAdmin()
        {
            // Simplified check
            return true; 
        }
    }
}
