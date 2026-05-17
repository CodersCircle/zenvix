using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Hostix.Runtime.Services;
using Hostix.ViewModels.Services;
using Serilog;

namespace Hostix.ViewModels
{
    public partial class ServicesViewModel : ObservableObject
    {
        private readonly IServicesOrchestrator _orchestrator;
        private readonly IDispatcherService _dispatcher;
        private readonly IClipboardService _clipboard;
        private readonly IPhpMyAdminService _phpMyAdmin;

        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private string? _errorMessage;
        [ObservableProperty] private RuntimeServiceInstance? _selectedInstance;
        [ObservableProperty] private string _filterQuery = string.Empty;

        private readonly object _collectionLock = new();

        // The master UI collection owned by this ViewModel (WPF requirement: mutate only on UI thread)
        public ObservableCollection<RuntimeServiceInstance> Instances { get; } = new();
        public object InstancesLock => _orchestrator.InstancesLock;

        // Thread-safe categorised collections owned by the UI thread
        public ObservableCollection<RuntimeServiceInstance> WebServers { get; } = new();
        public ObservableCollection<RuntimeServiceInstance> Runtimes { get; } = new();
        public ObservableCollection<RuntimeServiceInstance> Databases { get; } = new();
        public ObservableCollection<RuntimeServiceInstance> DevServices { get; } = new();

        public ServicesViewModel(
            IServicesOrchestrator orchestrator, 
            IDispatcherService dispatcher,
            IClipboardService clipboard,
            IPhpMyAdminService phpMyAdmin)
        {
            _orchestrator = orchestrator;
            _dispatcher   = dispatcher;
            _clipboard    = clipboard;
            _phpMyAdmin   = phpMyAdmin;

            try
            {
                // Sync on instance state changes (Start/Stop/Metrics)
                _orchestrator.InstanceStateChanged += inst =>
                    _dispatcher.BeginInvoke(() => {
                        try
                        {
                            // If it's a new instance or category change, full sync
                            var existing = Instances.FirstOrDefault(i => i.Id == inst.Id);
                            if (existing == null)
                            {
                                SyncCollections();
                            }
                            else
                            {
                                // Force UI refresh for the specific item's calculated properties
                                OnPropertyChanged(nameof(WebServers));
                                OnPropertyChanged(nameof(Runtimes));
                                OnPropertyChanged(nameof(Databases));
                                OnPropertyChanged(nameof(DevServices));
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[ServicesVM] Error during property change notification.");
                        }
                    });

                // Sync on collection structure changes (Add/Remove/Seed)
                _orchestrator.RegistryUpdated += () => SyncCollections();

                // Start initialization in background
                _ = Task.Run(async () => 
                {
                    try
                    {
                        await _orchestrator.InitializeAsync();
                        _dispatcher.BeginInvoke(() => 
                        {
                            SyncCollections();
                            IsLoading = false;
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Fatal(ex, "[ServicesVM] CRITICAL: Failed to initialize services orchestrator.");
                        _dispatcher.Invoke(() => {
                            ErrorMessage = $"Failed to initialize services: {ex.Message}";
                            IsLoading = false;
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[ServicesVM] Hard crash prevented in constructor.");
                ErrorMessage = "Fatal initialization error.";
                IsLoading = false;
            }
        }

        /// <summary>
        /// Atomicly synchronises the local UI collections with the background orchestrator state.
        /// This ensures the UI thread always owns the collections it is binding to.
        /// </summary>
        private void SyncCollections()
        {
            try
            {
                List<RuntimeServiceInstance> snapshot;
                
                // 1. Capture snapshot immediately on the current thread, 
                // holding the lock only for this brief moment.
                lock (_orchestrator.InstancesLock)
                {
                    snapshot = _orchestrator.Instances.ToList();
                }

                // 2. Dispatch to UI thread ASYNCHRONOUSLY to avoid deadlocking the caller
                _dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // Smart-update categorised collections to avoid UI churn
                        UpdateCollectionSafely(Instances,   snapshot);
                        UpdateCollectionSafely(WebServers,  snapshot.Where(i => i.Category == ServiceCategory.WebServer).ToList());
                        UpdateCollectionSafely(Runtimes,    snapshot.Where(i => i.Category == ServiceCategory.Runtime).ToList());
                        UpdateCollectionSafely(Databases,   snapshot.Where(i => i.Category == ServiceCategory.Database).ToList());
                        UpdateCollectionSafely(DevServices, snapshot.Where(i => i.Category == ServiceCategory.Developer || i.Category == ServiceCategory.Worker).ToList());

                        OnPropertyChanged(nameof(WebServers));
                        OnPropertyChanged(nameof(Runtimes));
                        OnPropertyChanged(nameof(Databases));
                        OnPropertyChanged(nameof(DevServices));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[ServicesVM] Error during UI collection update.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ServicesVM] Failed to capture collection snapshot defensively.");
            }
        }

        /// <summary>
        /// Updates an ObservableCollection by adding/removing items to match the source list.
        /// This avoids Clear() + AddAll() which causes massive UI churn and loses selection state.
        /// </summary>
        private void UpdateCollectionSafely(ObservableCollection<RuntimeServiceInstance> collection, List<RuntimeServiceInstance> newList)
        {
            try
            {
                // 1. Remove items that no longer exist
                var toRemove = collection.Where(item => newList.All(n => n.Id != item.Id)).ToList();
                foreach (var item in toRemove) collection.Remove(item);

                // 2. Add items that are new
                foreach (var item in newList)
                {
                    if (collection.All(c => c.Id != item.Id))
                    {
                        collection.Add(item);
                    }
                }

                // 3. Ensure order matches (optional, but good for UX stability)
                for (int i = 0; i < newList.Count; i++)
                {
                    var targetId = newList[i].Id;
                    int currentIndex = -1;
                    for(int j=0; j < collection.Count; j++) if(collection[j].Id == targetId) { currentIndex = j; break; }

                    if (currentIndex >= 0 && currentIndex != i)
                    {
                        collection.Move(currentIndex, i);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ServicesVM] Exception during safe collection update. Falling back to Clear.");
                collection.Clear();
                foreach (var item in newList) collection.Add(item);
            }
        }

        [RelayCommand]
        private void CopySmtpConfig(RuntimeServiceInstance? inst)
        {
            if (inst == null || inst.Type != RuntimeServiceType.Mailpit) return;

            var config = "MAIL_MAILER=smtp\n" +
                         "MAIL_HOST=127.0.0.1\n" +
                         "MAIL_PORT=1025\n" +
                         "MAIL_USERNAME=\n" +
                         "MAIL_PASSWORD=\n" +
                         "MAIL_ENCRYPTION=null\n" +
                         "MAIL_FROM_ADDRESS=test@localhost\n" +
                         "MAIL_FROM_NAME=Hostix";

            _clipboard.SetText(config);
        }

        [RelayCommand]
        private async Task SendTestMail(RuntimeServiceInstance? inst)
        {
            if (inst == null || inst.Type != RuntimeServiceType.Mailpit || inst.Status != ServiceStatus.Running) return;

            try
            {
                using var client = new System.Net.Mail.SmtpClient("127.0.0.1", 1025);
                var mail = new System.Net.Mail.MailMessage("test@hostix.local", "user@example.com")
                {
                    Subject = "Hostix Infrastructure Test",
                    Body = $"Test email sent from Infrastructure Page at {DateTime.Now}."
                };
                await client.SendMailAsync(mail);
            }
            catch { }
        }

        [RelayCommand]
        private async Task OpenAdminPanel(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            if (inst.Type == RuntimeServiceType.Mailpit)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://localhost:8025") { UseShellExecute = true }); }
                catch { }
                return;
            }

            // Standard Adminer/PMA logic for others
            // Note: ServicesView doesn't currently support full DB autologin injection here yet
            // but we'll open the panel for the user.
            var dbType = inst.Type switch
            {
                RuntimeServiceType.MySQL => "mysql",
                RuntimeServiceType.MariaDB => "mariadb",
                RuntimeServiceType.PostgreSQL => "postgresql",
                RuntimeServiceType.Redis => "redis",
                _ => ""
            };

            if (!string.IsNullOrEmpty(dbType))
            {
                await _phpMyAdmin.OpenPanelAsync(dbType, inst.Port, "root", "", "test");
            }
        }

        [RelayCommand]
        private async Task ToggleService(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            if (inst.Status == ServiceStatus.Running || inst.Status == ServiceStatus.Starting)
                await _orchestrator.StopAsync(inst.Id);
            else
                await _orchestrator.StartAsync(inst.Id);
        }

        [RelayCommand]
        private async Task RestartService(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            await _orchestrator.RestartAsync(inst.Id);
        }

        [RelayCommand]
        private void OpenConfig(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            _orchestrator.OpenConfig(inst.Id);
        }

        [RelayCommand]
        private void OpenLogs(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            _orchestrator.OpenLogs(inst.Id);
        }

        [RelayCommand]
        private void OpenDataFolder(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            _orchestrator.OpenDataFolder(inst.Id);
        }

        [RelayCommand]
        private void RemoveService(RuntimeServiceInstance? inst)
        {
            if (inst == null) return;
            _orchestrator.RemoveService(inst.Id);
        }
    }
}
