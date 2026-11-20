using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Zenvix.Core.Models;
using Zenvix.Modules.Services;
using Zenvix.Runtime.Services;
using Zenvix.ViewModels.Services;

namespace Zenvix.ViewModels
{
    public partial class DatabasesViewModel : ObservableObject
    {
        private readonly IDatabaseOrchestrationService _dbService;
        private readonly IDispatcherService _dispatcher;
        private readonly ILegacyDatabaseImporter _importer;

        /// <summary>"XAMPP" when another stack's MySQL with databases is installed.</summary>
        public string? LegacySourceName { get; }
        public bool CanImportLegacy => LegacySourceName != null;
        public string ImportButtonText => $"Import from {LegacySourceName}";
        public ObservableCollection<string> ImportLog { get; } = new();
        [ObservableProperty] private bool _isImporting;
        [ObservableProperty] private string _importStatus = "";

        public ObservableCollection<DatabaseInstance> Instances => _dbService.Instances;

        [ObservableProperty] private DatabaseInstance? _selectedInstance;
        [ObservableProperty] private bool _isAddingInstance;
        [ObservableProperty] private string _newInstanceName   = string.Empty;
        [ObservableProperty] private int    _newInstancePort   = 3308;
        [ObservableProperty] private DbEngineType _newInstanceEngine = DbEngineType.MySQL;

        public DbEngineType[] AvailableEngines => new[]
        {
            // Local
            DbEngineType.MariaDB, DbEngineType.MySQL, DbEngineType.PostgreSQL,
            DbEngineType.SQLite,  DbEngineType.MongoDB, DbEngineType.Redis,
            DbEngineType.Meilisearch,
            // Cloud / BaaS
            DbEngineType.Supabase, DbEngineType.Firebase,
            DbEngineType.PlanetScale, DbEngineType.Neon,
        };

        public DatabasesViewModel(IDatabaseOrchestrationService dbService, IDispatcherService dispatcher, ILegacyDatabaseImporter importer)
        {
            _importer = importer;
            LegacySourceName = importer.Detect()?.Name;
            _dbService  = dbService;
            _dispatcher = dispatcher;

            _dbService.InstanceStateChanged += instance =>
                _dispatcher.Invoke(() => OnPropertyChanged(nameof(Instances)));

            _ = System.Threading.Tasks.Task.Run(_dbService.InitializeAsync);
        }

        /// <summary>Copies the other stack's databases into Zenvix MySQL (kept when that stack is uninstalled).</summary>
        [RelayCommand]
        private async Task ImportLegacy()
        {
            if (IsImporting) return;
            IsImporting = true;
            ImportLog.Clear();
            ImportStatus = $"Copying {LegacySourceName} databases into Zenvix MySQL...";
            var progress = new System.Progress<string>(line => _dispatcher.BeginInvoke(() =>
            {
                ImportLog.Add(line);
                ImportStatus = line;
            }));
            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => _importer.ImportAsync(progress));
                ImportStatus = (result.Success ? "✔ " : "✖ ") + result.Message;
            }
            catch (System.Exception ex) { ImportStatus = "✖ " + ex.Message; }
            finally { IsImporting = false; }
        }

        [RelayCommand]
        private async Task ToggleInstance(DatabaseInstance? instance)
        {
            if (instance == null) return;

            if (instance.Status == DbInstanceStatus.Running || instance.Status == DbInstanceStatus.Starting)
                await _dbService.StopInstanceAsync(instance.Id);
            else
                await _dbService.StartInstanceAsync(instance.Id);
        }

        [RelayCommand]
        private async Task RestartInstance(DatabaseInstance? instance)
        {
            if (instance == null) return;
            await _dbService.RestartInstanceAsync(instance.Id);
        }

        [RelayCommand]
        private void OpenPanel(DatabaseInstance? instance)
        {
            if (instance == null) return;
            _dbService.OpenPanel(instance.Id);
        }

        [RelayCommand]
        private void OpenDataFolder(DatabaseInstance? instance)
        {
            if (instance == null) return;
            _dbService.OpenDataFolder(instance.Id);
        }

        [RelayCommand]
        private void OpenConfig(DatabaseInstance? instance)
        {
            if (instance == null) return;
            _dbService.OpenConfig(instance.Id);
        }

        [RelayCommand]
        private void OpenLogs(DatabaseInstance? instance)
        {
            if (instance == null) return;
            _dbService.OpenLogs(instance.Id);
        }

        [RelayCommand]
        private void RemoveInstance(DatabaseInstance? instance)
        {
            if (instance == null) return;
            _dbService.RemoveInstance(instance.Id);
        }

        [RelayCommand]
        private void ShowAddInstance() => IsAddingInstance = true;

        [RelayCommand]
        private void CancelAddInstance()
        {
            IsAddingInstance   = false;
            NewInstanceName    = string.Empty;
            NewInstancePort    = 3308;
            NewInstanceEngine  = DbEngineType.MySQL;
        }

        [RelayCommand]
        private void ConfirmAddInstance()
        {
            if (string.IsNullOrWhiteSpace(NewInstanceName)) return;
            _dbService.CreateInstance(NewInstanceName, NewInstanceEngine, NewInstancePort);
            CancelAddInstance();
        }
    }
}
