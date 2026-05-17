using System;

namespace Hostix.Core.Models
{
    public enum EnvironmentMode
    {
        Development,
        Testing,
        Staging,
        ProductionSimulation
    }

    public enum WebsiteStatus
    {
        Stopped,
        Starting,
        Running,
        Error
    }

    public enum ProjectType
    {
        Laravel,
        WordPress,
        PHP,
        NodeJS,
        Static,
        Vite,
        React,
        Vue
    }

    public class Website : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        private WebsiteStatus _status = WebsiteStatus.Stopped;
        private string? _notes;
        private string? _sslDiagnosticMessage;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string PhpVersion { get; set; } = "8.3"; // Default
        
        public WebsiteStatus Status 
        { 
            get => _status; 
            set { if (_status != value) { _status = value; OnPropertyChanged(); } } 
        }

        public ProjectType Type { get; set; } = ProjectType.PHP;
        public bool SslEnabled { get; set; } = false;
        public string? PublicDir { get; set; } // e.g. "public" for Laravel
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastOpened { get; set; } = DateTime.Now;
        
        public string? Notes 
        { 
            get => _notes; 
            set { if (_notes != value) { _notes = value; OnPropertyChanged(); } } 
        }

        public string? SslDiagnosticMessage 
        { 
            get => _sslDiagnosticMessage; 
            set { if (_sslDiagnosticMessage != value) { _sslDiagnosticMessage = value; OnPropertyChanged(); } } 
        }

        // Derived UI helpers
        public bool IsDocsSite => Domain.StartsWith("docs.", StringComparison.OrdinalIgnoreCase) || Name.Contains("(Docs)");

        public string Icon
        {
            get
            {
                if (string.IsNullOrEmpty(Domain)) return "W";

                var parts = Domain.Split('.');
                if (parts.Length < 2) return Domain.Substring(0, 1).ToUpper();

                // Common subdomains to ignore
                var subdomainsToIgnore = new[] { "admin", "api", "dev", "dashboard", "panel", "mail", "cdn", "www" };
                
                string mainPart = parts[0];
                if (parts.Length >= 3 && subdomainsToIgnore.Contains(parts[0].ToLower()))
                {
                    mainPart = parts[1];
                }

                return mainPart.Substring(0, 1).ToUpper();
            }
        }

        public string DisplayUrl => SslEnabled ? $"https://{Domain}" : $"http://{Domain}";
    }
}
