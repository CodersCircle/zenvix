using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Runtime.Services;
using Hostix.ViewModels.Services;

namespace Hostix.ViewModels
{
    public partial class DatabasesViewModel : ObservableObject
    {
        private readonly IDatabaseOrchestrationService _dbService;
        private readonly IDispatcherService _dispatcher;

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

        public DatabasesViewModel(IDatabaseOrchestrationService dbService, IDispatcherService dispatcher)
        {
            _dbService  = dbService;
            _dispatcher = dispatcher;

            _dbService.InstanceStateChanged += instance =>
                _dispatcher.Invoke(() => OnPropertyChanged(nameof(Instances)));

            _ = System.Threading.Tasks.Task.Run(_dbService.InitializeAsync);
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
