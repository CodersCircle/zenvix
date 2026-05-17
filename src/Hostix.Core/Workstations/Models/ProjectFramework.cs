using System;

namespace Hostix.Core.Workstations.Models
{
    public class ProjectFramework
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // e.g., "Laravel", "Next.js", "WordPress"
        public string IconCode { get; set; } = "\xE71B"; // Default icon
    }
}
