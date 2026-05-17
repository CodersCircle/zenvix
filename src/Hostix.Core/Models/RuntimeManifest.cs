using System.Collections.Generic;
using Hostix.Core.Models;

namespace Hostix.Core.Models
{
    public class RuntimeManifest
    {
        public Dictionary<RuntimeServiceType, List<RuntimeVersionInfo>> Runtimes { get; set; } = new();
    }

    public class RuntimeVersionInfo
    {
        public string Version { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public string RelativeBinaryPath { get; set; } = string.Empty;
    }
}
