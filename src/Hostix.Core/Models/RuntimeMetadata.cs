using Hostix.Core.Models;

namespace Hostix.Core.Models
{
    public class RuntimeMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = "Unknown";
        public string ExecutablePath { get; set; } = string.Empty;
        public string? ConfigPath { get; set; }
        public string InstallSource { get; set; } = "Unknown"; // Hostix, Laragon, System, etc.
        public RuntimeServiceType Type { get; set; }
        public string? BinDir => System.IO.Path.GetDirectoryName(ExecutablePath);
        public string? RootDir => System.IO.Path.GetDirectoryName(BinDir);
    }
}
