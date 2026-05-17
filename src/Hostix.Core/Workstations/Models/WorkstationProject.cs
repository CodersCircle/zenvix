using System;
using System.Collections.Generic;

namespace Hostix.Core.Workstations.Models
{
    public class WorkstationProject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string LocalDomain { get; set; } = string.Empty; // e.g., myproject.test
        public bool IsRunning { get; set; }
        public bool IsSslEnabled { get; set; }
        public ProjectFramework Framework { get; set; } = new();
        public string AssignedRuntimeVersion { get; set; } = string.Empty; // e.g., "PHP 8.2" or "Node 20"
        public int PrimaryPort { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    }
}
