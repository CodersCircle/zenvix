using System;
using System.ComponentModel;

namespace Hostix.Core.Models
{
    public enum ServiceType
    {
        Nginx,
        Apache,
        PhpFpm,
        MariaDB,
        MySQL,
        Postgres,
        Redis,
        Mailpit
    }

    public enum ServiceStatus
    {
        Stopped,
        Running,
        Starting,
        Stopping,
        Error
    }

    public class Service : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, string name)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public ServiceType Type { get; set; }
        public string Name { get; set; } = string.Empty;

        private ServiceStatus _status = ServiceStatus.Stopped;
        public ServiceStatus Status
        {
            get => _status;
            set
            {
                Set(ref _status, value, nameof(Status));
                // Panel button depends on Status — notify so binding updates reactively
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPanelButton)));
            }
        }

        private int? _processId;
        public int? ProcessId
        {
            get => _processId;
            set => Set(ref _processId, value, nameof(ProcessId));
        }

        public int Port { get; set; }
        public int RestartAttempts { get; set; }
        public string ConfigPath { get; set; } = string.Empty;

        private string _version = string.Empty;
        public string Version
        {
            get => _version;
            set => Set(ref _version, value, nameof(Version));
        }

        /// <summary>If true this service appears in the Dashboard Active Services panel.</summary>
        public bool ShowOnDashboard { get; set; } = false;
        /// <summary>True for services that have a web admin panel (MySQL, MariaDB, Postgres, Redis, Mailpit).</summary>
        public bool HasAdminPanel => Type == ServiceType.MySQL
                                  || Type == ServiceType.MariaDB
                                  || Type == ServiceType.Postgres
                                  || Type == ServiceType.Redis
                                  || Type == ServiceType.Mailpit;
        /// <summary>True for services that have a web admin panel (MySQL, MariaDB, Postgres, Redis, Mailpit).</summary>
        public bool ShowPanelButton => HasAdminPanel;

        private string? _notes;
        public string? Notes
        {
            get => _notes;
            set => Set(ref _notes, value, nameof(Notes));
        }

        private string? _diagnosticLogs;
        public string? DiagnosticLogs
        {
            get => _diagnosticLogs;
            set => Set(ref _diagnosticLogs, value, nameof(DiagnosticLogs));
        }
    }
}
