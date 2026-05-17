using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace Hostix.Modules.Services
{
    public class RuntimeVersion
    {
        public string Version { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Identifier { get; set; } = string.Empty; // e.g. "php82", "node20"
    }

    public interface IRuntimeVersionManager
    {
        IEnumerable<RuntimeVersion> GetAvailablePhpVersions();
        IEnumerable<RuntimeVersion> GetAvailableNodeVersions();
        void RegisterVersion(string type, string version, string path);
    }

    public class RuntimeVersionManager : IRuntimeVersionManager
    {
        private readonly string _baseBinPath;

        public RuntimeVersionManager()
        {
            _baseBinPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
        }

        public IEnumerable<RuntimeVersion> GetAvailablePhpVersions()
        {
            var phpDir = Path.Combine(_baseBinPath, "php");
            if (!Directory.Exists(phpDir)) return Enumerable.Empty<RuntimeVersion>();

            return Directory.GetDirectories(phpDir).Select(dir => new RuntimeVersion
            {
                Version = Path.GetFileName(dir),
                Path = dir,
                Identifier = $"php-{Path.GetFileName(dir)}"
            });
        }

        public IEnumerable<RuntimeVersion> GetAvailableNodeVersions()
        {
            var nodeDir = Path.Combine(_baseBinPath, "node");
            if (!Directory.Exists(nodeDir)) return Enumerable.Empty<RuntimeVersion>();

            return Directory.GetDirectories(nodeDir).Select(dir => new RuntimeVersion
            {
                Version = Path.GetFileName(dir),
                Path = dir,
                Identifier = $"node-{Path.GetFileName(dir)}"
            });
        }

        public void RegisterVersion(string type, string version, string path)
        {
            Log.Information("Registering {Type} version {Version} at {Path}", type, version, path);
            // In a real app, this might save to the SQLite database
        }
    }
}
