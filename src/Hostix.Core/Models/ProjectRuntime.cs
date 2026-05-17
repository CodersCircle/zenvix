using System;

namespace Hostix.Core.Models
{
    public enum PhpVersion { Php74, Php80, Php81, Php82, Php83 }
    public enum NodeVersion { Node18, Node20, Node22 }
    public enum ProjectFramework { Unknown, Laravel, WordPress, Vue, React, NextJs, Static, Node }

    public class ProjectRuntime
    {
        public PhpVersion? PhpVersion { get; set; }
        public NodeVersion? NodeVersion { get; set; }
        public string PhpFpmPort { get; set; } = "9000";
        public string DevServerPort { get; set; } = "5173";
        public bool HasQueue { get; set; }
        public bool HasScheduler { get; set; }
        public int? QueueWorkerPid { get; set; }
        public int? ViteDevServerPid { get; set; }
    }

    public class SslCertificate
    {
        public string Domain { get; set; } = string.Empty;
        public string CertPath { get; set; } = string.Empty;
        public string KeyPath { get; set; } = string.Empty;
        public bool IsTrusted { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
