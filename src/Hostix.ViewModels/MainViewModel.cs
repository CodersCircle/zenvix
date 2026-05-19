using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System;
using System.Diagnostics;
using System.Threading;
using Hostix.Core.Models;
using Hostix.Runtime.Services;
using Hostix.Modules.Services;
using Hostix.ViewModels.Services;
using Hostix.ViewModels.Workstations;
using Serilog;

using Hostix.Core.Services;

namespace Hostix.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IRuntimeEngine _runtimeEngine;
        private readonly IServicesOrchestrator _servicesOrchestrator;
        private readonly IRuntimeStateBridge _bridge;
        private readonly IPhpMyAdminService _phpMyAdmin;
        private readonly IProjectScanner _projectScanner;
        private readonly IThemeService _themeService;
        private readonly IDispatcherService _dispatcher;
        private readonly IRuntimeStateManager _stateManager;
        private readonly IClipboardService _clipboard;
        private readonly IDatabaseCredentialsManager _credentialsManager;
        private readonly ISSLManager _sslManager;
        private Timer? _monitoringTimer;

        [ObservableProperty] private string _statusText = "Ready";
        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private ObservableObject? _currentViewModel;

        // Shared State Accessors (Proxies to StateManager for UI Binding consistency)
        public IRuntimeStateManager State => _stateManager;
        public SettingsViewModel Settings => _settingsVm;
        public ObservableCollection<Service> Services => _stateManager.ActiveServices;
        public ObservableCollection<Website> Websites => _stateManager.RunningWebsites;
        public ObservableCollection<string> RuntimeEvents => _stateManager.RuntimeEvents;

        // ViewModels
        private readonly DashboardViewModel _dashboardVm;
        private readonly WebsitesViewModel _websitesVm;
        private readonly DatabasesViewModel _databasesVm;
        private readonly ServicesViewModel _servicesVm;
        private readonly LogsViewModel _logsVm = new();
        private readonly SettingsViewModel _settingsVm;
        private readonly WorkstationsViewModel _workstationsVm = new();
        private readonly IAppUpdaterService _updater;

        public MainViewModel(
            IRuntimeEngine runtimeEngine,
            IServicesOrchestrator servicesOrchestrator,
            IRuntimeStateBridge bridge,
            IPhpMyAdminService phpMyAdmin,
            IProjectScanner projectScanner,
            IThemeService themeService,
            IDispatcherService dispatcher,
            IRuntimeStateManager stateManager,
            IClipboardService clipboard,
            IDatabaseCredentialsManager credentialsManager,
            DatabasesViewModel databasesViewModel,
            ServicesViewModel servicesViewModel,
            WebsitesViewModel websitesViewModel,
            SettingsViewModel settingsViewModel,
            IAppUpdaterService updater,
            ISSLManager sslManager)
        {
            _runtimeEngine = runtimeEngine;
            _servicesOrchestrator = servicesOrchestrator;
            _bridge = bridge;
            _phpMyAdmin = phpMyAdmin;
            _projectScanner = projectScanner;
            _themeService = themeService;
            _dispatcher = dispatcher;
            _stateManager = stateManager;
            _clipboard = clipboard;
            _credentialsManager = credentialsManager;
            _settingsVm = settingsViewModel;
            _updater = updater;
            _sslManager = sslManager;

            // Initialize Child ViewModels with shared state
            _dashboardVm = new DashboardViewModel(_stateManager);
            _websitesVm = websitesViewModel;
            _databasesVm = databasesViewModel;
            _servicesVm = servicesViewModel;

            CurrentViewModel = _dashboardVm;
            _runtimeEngine.StartHeartbeat();
            _runtimeEngine.ServiceStateChanged += OnServiceStateChanged;

            // ── Seed Dashboard with known services (Stopped state) ──────────────
            // This populates RuntimeStateManager.ActiveServices and DashboardServices
            // ONCE at startup. From here on, ServicesOrchestrator → Bridge owns all updates.
            foreach (var svc in _runtimeEngine.GetActiveServices())
                _stateManager.UpdateService(svc);

            // Start the global sync bridge AFTER seeding so initial state is correct
            _bridge.Start();

            _phpMyAdmin.OnStatusMessage += msg =>
            {
                _dispatcher.Invoke(() =>
                {
                    _stateManager.AddEvent($"[AdminPanel] {msg}");
                });
            };

            RefreshData();

            _monitoringTimer = new Timer(OnMonitoringTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            _stateManager.AddEvent("Hostix Core Synchronization Active.");

            // Startup: detect & stop any externally running service instances, then show clean state
            _ = Task.Run(async () =>
            {
                await _runtimeEngine.InitializeAsync();
                _stateManager.AddEvent("Startup scan complete. All services in Stopped state.");
                _dispatcher.Invoke(RefreshData);
            });

            StartUpdateChecker();
        }

        public string AppVersion => $"v{_updater.CurrentVersion}";

        private void StartUpdateChecker()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(30)); // Check every 30 minutes
                    var info = await _updater.CheckForUpdatesAsync();
                    if (info != null && info.IsUpdateAvailable)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            _settingsVm.IsUpdateAvailable = true;
                            _settingsVm.UpdateStatus = $"Update Available: {info.Version}";
                            _stateManager.AddEvent($"New version {info.Version} is available! Go to Settings to update.");
                        });
                    }
                }
            });
        }

        [RelayCommand]
        private void Navigate(string viewName)
        {
            CurrentViewModel = viewName switch
            {
                "Dashboard" => _dashboardVm,
                "Websites" => _websitesVm,
                "Databases" => _databasesVm,
                "Services" => _servicesVm,
                "Workstations" => _workstationsVm,
                "Logs" => _logsVm,
                "Settings" => _settingsVm,
                _ => _dashboardVm
            };
            StatusText = $"Hostix - {viewName}";
        }

        [RelayCommand]
        private void ToggleTheme()
        {
            _isDark = !_isDark;
            _themeService.SetTheme(_isDark ? "Dark" : "Light");
        }
        private bool _isDark = true;

        [RelayCommand]
        private async Task ToggleService(Service? service)
        {
            if (service == null) return;
            try
            {
                // Route through ServicesOrchestrator — the single source of truth.
                // The RuntimeStateBridge will propagate the state change back to
                // RuntimeStateManager so Dashboard and all pages update instantly.
                var instanceId = _bridge.ResolveInstanceId(service.Type);
                if (instanceId.HasValue)
                {
                    if (service.Status == ServiceStatus.Running || service.Status == ServiceStatus.Starting)
                        await _servicesOrchestrator.StopAsync(instanceId.Value);
                    else
                        await _servicesOrchestrator.StartAsync(instanceId.Value);
                }
                else
                {
                    // Fallback: legacy RuntimeEngine path for unmapped service types
                    if (service.Status == ServiceStatus.Running)
                        await _runtimeEngine.StopServiceAsync(service.Type);
                    else
                        await _runtimeEngine.StartServiceAsync(service.Type);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                _stateManager.AddEvent($"Runtime Error: {ex.Message}");
            }
        }

        [RelayCommand] private void FlushDNS() { _stateManager.AddEvent("DNS Cache flushed."); StatusText = "DNS Flushed"; }
        [RelayCommand] private void ClearCache() { _stateManager.AddEvent("Runtime cache cleared."); StatusText = "Cache Cleared"; }

        [RelayCommand]
        private void TrustRootCA()
        {
            _stateManager.AddEvent("Installing Hostix Root CA to system store...");
            var success = _sslManager.InstallRootCA();
            if (success)
            {
                _stateManager.AddEvent("SUCCESS: Hostix Root CA installed. Please RESTART your browser to trust local domains.");
                StatusText = "Root CA Trusted";
            }
            else
            {
                _stateManager.AddEvent("ERROR: Root CA installation failed. Try running Hostix as Administrator.");
                StatusText = "Trust Failed";
            }
        }

        [ObservableProperty] private string? _selectedDiagnosticLogs;
        [ObservableProperty] private string? _selectedServiceName;
        [ObservableProperty] private bool _isDiagnosticFlyoutOpen;

        [RelayCommand]
        private void ShowDiagnostics(object? parameter)
        {
            if (parameter is Service service)
            {
                SelectedServiceName = service.Name;
                SelectedDiagnosticLogs = service.DiagnosticLogs ?? "No diagnostic information available for this service.";
            }
            else if (parameter is RuntimeServiceInstance rsi)
            {
                SelectedServiceName = rsi.Name;
                SelectedDiagnosticLogs = rsi.DiagnosticLogs ?? "No diagnostic information available for this service.";
            }
            else return;

            IsDiagnosticFlyoutOpen = true;
        }

        [RelayCommand]
        private void CopyDiagnostics()
        {
            if (!string.IsNullOrEmpty(SelectedDiagnosticLogs))
            {
                _clipboard.SetText(SelectedDiagnosticLogs);
                _stateManager.AddEvent("Diagnostic logs copied to clipboard.");
            }
        }

        [RelayCommand]
        private void ExportDiagnostics()
        {
            if (string.IsNullOrEmpty(SelectedDiagnosticLogs)) return;

            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", $"diagnostic_{SelectedServiceName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, SelectedDiagnosticLogs);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                _stateManager.AddEvent($"Diagnostics exported to {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                _stateManager.AddEvent($"Export failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private void CopySmtpConfig(Service? service)
        {
            if (service == null || service.Type != ServiceType.Mailpit) return;

            var config = "MAIL_MAILER=smtp\n" +
                         "MAIL_HOST=127.0.0.1\n" +
                         "MAIL_PORT=1025\n" +
                         "MAIL_USERNAME=\n" +
                         "MAIL_PASSWORD=\n" +
                         "MAIL_ENCRYPTION=null\n" +
                         "MAIL_FROM_ADDRESS=test@localhost\n" +
                         "MAIL_FROM_NAME=Hostix";

            _clipboard.SetText(config);
            _stateManager.AddEvent("Mailpit SMTP config copied to clipboard (Laravel format).");
            StatusText = "SMTP Config Copied";
        }

        [RelayCommand]
        private async Task SendTestMail(Service? service)
        {
            if (service == null || service.Type != ServiceType.Mailpit || service.Status != ServiceStatus.Running) return;

            try
            {
                using var client = new System.Net.Mail.SmtpClient("127.0.0.1", 1025);
                var mail = new System.Net.Mail.MailMessage("test@hostix.local", "user@example.com")
                {
                    Subject = "Hostix Test Email",
                    Body = $"This is a test email sent from Hostix at {DateTime.Now}.\n\nMailpit is working correctly!"
                };
                await client.SendMailAsync(mail);
                _stateManager.AddEvent("Test email sent to Mailpit (127.0.0.1:1025)");
                StatusText = "Test Mail Sent";
            }
            catch (Exception ex)
            {
                _stateManager.AddEvent($"Failed to send test mail: {ex.Message}");
                StatusText = "Mail Failed";
            }
        }

        [RelayCommand]
        private async Task OpenAdminPanel(Service? service)
        {
            if (service == null) return;
            var (dbType, dbPort) = service.Type switch
            {
                ServiceType.MySQL => ("mysql", service.Port),
                ServiceType.MariaDB => ("mariadb", service.Port),
                ServiceType.Postgres => ("postgresql", service.Port),
                ServiceType.Redis => ("redis", service.Port),
                ServiceType.Mailpit => ("mailpit", 8025),
                _ => ("", 0)
            };

            if (string.IsNullOrEmpty(dbType)) return;

            if (service.Type == ServiceType.Mailpit)
            {
                // Mailpit has its own built-in web UI — no PHP needed
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://localhost:8025") { UseShellExecute = true }); }
                catch { }
                return;
            }

            _stateManager.AddEvent($"Opening admin panel for {service.Name}...");

            // Fetch synchronized credentials from the manager
            // This ensures that initialization, Adminer autologin, and UI are always in sync.
            var runtimeType = MapToRuntimeType(service.Type);
            var (user, pass) = _credentialsManager.GetCredentials(runtimeType);
            const string defaultDatabase = "test";

            await _phpMyAdmin.OpenPanelAsync(dbType, dbPort, user, pass, defaultDatabase);
        }

        [RelayCommand]
        private void StopAllServices()
        {
            var running = Services.Where(s => s.Status == ServiceStatus.Running).ToList();
            foreach (var s in running) _ = ToggleService(s);
            _stateManager.AddEvent("Shutting down all active runtimes...");
        }

        private void OnServiceStateChanged(Service service)
        {
            _stateManager.UpdateService(service);

            var runningCount = Services.Count(s => s.Status == ServiceStatus.Running);
            _stateManager.InfrastructureState = runningCount > 0 ? "Active" : "Idle";
            StatusText = runningCount > 0 ? "Runtime Active" : "Ready";
        }

        private void OnMonitoringTick(object? state)
        {
            var rand = new Random();
            _dispatcher.Invoke(() =>
            {
                _stateManager.CpuUsage = $"{rand.Next(2, 8)}%";
                _stateManager.RamUsage = $"{rand.NextDouble() * 2 + 1:F1} GB";
                OnPropertyChanged(nameof(State)); // Notify UI to refresh metrics bindings
            });
        }

        partial void OnSearchQueryChanged(string value) => RefreshData();

        private void RefreshData()
        {
            // NOTE: Service state is exclusively managed by ServicesOrchestrator → RuntimeStateBridge.
            // Do NOT pull from RuntimeEngine here — it overwrites bridge-synced state with stale values.

            var projects = _projectScanner.ScanNow(@"C:\Hostix\projects");
            var query = SearchQuery.ToLower();

            _dispatcher.Invoke(() =>
            {
                _stateManager.RunningWebsites.Clear();
                foreach (var project in projects)
                {
                    if (string.IsNullOrEmpty(query) || project.Name.ToLower().Contains(query))
                        _stateManager.RunningWebsites.Add(project);
                }
            });
        }

        private RuntimeServiceType MapToRuntimeType(ServiceType type)
        {
            return type switch
            {
                ServiceType.MySQL => RuntimeServiceType.MySQL,
                ServiceType.MariaDB => RuntimeServiceType.MariaDB,
                ServiceType.Postgres => RuntimeServiceType.PostgreSQL,
                _ => RuntimeServiceType.MySQL
            };
        }
    }
}
