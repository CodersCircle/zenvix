using System;

namespace Hostix.Core.Models
{
    public enum ServiceCategory
    {
        WebServer,
        Runtime,
        Database,
        Developer,
        Worker
    }

    public enum RuntimeServiceType
    {
        // Web Servers
        Nginx, Apache,
        // Runtimes
        PhpFpm, NodeRuntime, ViteRuntime,
        // Databases
        MariaDB, MySQL, PostgreSQL, MongoDB, Redis,
        // Developer
        Mailpit, QueueWorker, Scheduler, SSL,
    }

    public static class RuntimeServiceMeta
    {
        public static readonly System.Collections.Generic.Dictionary<RuntimeServiceType,
            (string Label, string Icon, ServiceCategory Category, int DefaultPort, string Version, string[] ProcessNames, string BinaryName)> Data = new()
        {
            [RuntimeServiceType.Nginx]       = ("Nginx",         "Nx", ServiceCategory.WebServer,  80,    "1.27",  new[]{"nginx"},        "nginx.exe"),
            [RuntimeServiceType.Apache]      = ("Apache",        "Ap", ServiceCategory.WebServer,  8080,  "2.4",   new[]{"httpd"},        "httpd.exe"),
            [RuntimeServiceType.PhpFpm]      = ("PHP-FPM",       "PH", ServiceCategory.Runtime,    9000,  "8.3",   new[]{"php-cgi"},      "php-cgi.exe"),
            [RuntimeServiceType.NodeRuntime] = ("Node.js",       "No", ServiceCategory.Runtime,    3000,  "20.14", new[]{"node"},         "node.exe"),
            [RuntimeServiceType.ViteRuntime] = ("Vite",          "Vi", ServiceCategory.Runtime,    5173,  "5.4",   new[]{"node"},         "node.exe"),
            [RuntimeServiceType.MariaDB]     = ("MariaDB",       "Ma", ServiceCategory.Database,   3306,  "11.4",  new[]{"mysqld","mariadbd"}, "mysqld.exe"),
            [RuntimeServiceType.MySQL]       = ("MySQL",         "My", ServiceCategory.Database,   3307,  "8.4",   new[]{"mysqld"},       "mysqld.exe"),
            [RuntimeServiceType.PostgreSQL]  = ("PostgreSQL",    "Pg", ServiceCategory.Database,   5432,  "16.3",  new[]{"postgres"},     "pg_ctl.exe"),
            [RuntimeServiceType.MongoDB]     = ("MongoDB",       "Mo", ServiceCategory.Database,   27017, "7.0",   new[]{"mongod"},       "mongod.exe"),
            [RuntimeServiceType.Redis]       = ("Redis",         "Re", ServiceCategory.Database,   6379,  "7.2",   new[]{"redis-server"}, "redis-server.exe"),
            [RuntimeServiceType.Mailpit]     = ("Mailpit SMTP",  "Ma", ServiceCategory.Developer,  1025,  "1.21",  new[]{"mailpit"},      "mailpit.exe"),
            [RuntimeServiceType.QueueWorker] = ("Queue Worker",  "Q",  ServiceCategory.Worker,     0,     "",      new[]{"php"},          "php.exe"),
            [RuntimeServiceType.Scheduler]   = ("Scheduler",     "Sc", ServiceCategory.Worker,     0,     "",      new[]{"php"},          "php.exe"),
            [RuntimeServiceType.SSL]         = ("SSL Service",   "SS", ServiceCategory.Developer,  443,   "",      System.Array.Empty<string>(), ""),
        };
    }

    public class RuntimeServiceInstance : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        private ServiceStatus _status = ServiceStatus.Stopped;
        private string _ramUsage = "0 MB";
        private string _cpuUsage = "0%";
        private string _uptimeDisplay = "--";
        private string? _notes;

        public Guid Id { get; set; } = Guid.NewGuid();
        public RuntimeServiceType Type { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public int Port { get; set; }
        
        public ServiceStatus Status 
        { 
            get => _status; 
            set { if (_status != value) { _status = value; OnPropertyChanged(); } } 
        }

        public int? ProcessId { get; set; }
        public string ConfigPath { get; set; } = string.Empty;
        public string LogPath { get; set; } = string.Empty;
        public string DataPath { get; set; } = string.Empty;
        public bool AutoStart { get; set; } = false;
        public DateTime? LastStarted { get; set; }
        
        public string CpuUsage 
        { 
            get => _cpuUsage; 
            set { if (_cpuUsage != value) { _cpuUsage = value; OnPropertyChanged(); } } 
        }

        public string RamUsage 
        { 
            get => _ramUsage; 
            set { if (_ramUsage != value) { _ramUsage = value; OnPropertyChanged(); } } 
        }

        public string UptimeDisplay 
        { 
            get => _uptimeDisplay; 
            set { if (_uptimeDisplay != value) { _uptimeDisplay = value; OnPropertyChanged(); } } 
        }

        public string? Notes 
        { 
            get => _notes; 
            set { if (_notes != value) { _notes = value; OnPropertyChanged(); } } 
        }
        
        public string? DiagnosticLogs { get; set; }
 
        /// <summary>True for services that have a web admin panel (MySQL, MariaDB, Postgres, Redis, Mailpit).</summary>
        public bool HasAdminPanel => Type == RuntimeServiceType.MySQL
                                  || Type == RuntimeServiceType.MariaDB
                                  || Type == RuntimeServiceType.PostgreSQL
                                  || Type == RuntimeServiceType.Redis
                                  || Type == RuntimeServiceType.Mailpit;

        /// <summary>True for services that have a web admin panel (MySQL, MariaDB, Postgres, Redis, Mailpit).</summary>
        public bool ShowPanelButton => HasAdminPanel;

        // Derived
        public string Icon => RuntimeServiceMeta.Data.TryGetValue(Type, out var m) ? m.Icon : "?";
        public string CategoryLabel => RuntimeServiceMeta.Data.TryGetValue(Type, out var m) ? m.Category.ToString() : "";
        public ServiceCategory Category => RuntimeServiceMeta.Data.TryGetValue(Type, out var m) ? m.Category : ServiceCategory.Developer;
    }
}
