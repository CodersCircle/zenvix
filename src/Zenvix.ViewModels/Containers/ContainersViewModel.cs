using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zenvix.Core.Services;
using Zenvix.Modules.Containers;
using Zenvix.ViewModels.Services;

namespace Zenvix.ViewModels.Containers
{
    public partial class ContainersViewModel : ObservableObject
    {
        private readonly IDockerService _docker;
        private readonly IDialogService _dialogs;
        private readonly IDispatcherService _dispatcher;
        private readonly Timer _refreshTimer;
        private int _refreshing;

        public ObservableCollection<ContainerInfo> Containers { get; } = new();
        public ObservableCollection<string> ActivityLog { get; } = new();
        public System.Collections.Generic.IReadOnlyList<ContainerTemplate> Templates => _docker.Templates;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDockerReady), nameof(IsDockerMissing), nameof(IsDockerStopped))]
        private DockerState _dockerState = DockerState.NotRunning;

        [ObservableProperty] private string _dockerDetail = "Checking Docker ...";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _isChecking = true;
        [ObservableProperty] private ContainerInfo? _logsFor;
        [ObservableProperty] private string _logsText = string.Empty;

        // Custom container form
        [ObservableProperty] private bool _isCustomFormOpen;
        [ObservableProperty] private string _customName = string.Empty;
        [ObservableProperty] private string _customImage = string.Empty;
        [ObservableProperty] private string _customPorts = "8080:80";
        [ObservableProperty] private string _customEnv = string.Empty;

        public bool IsDockerReady => DockerState == DockerState.Running;
        public bool IsDockerMissing => DockerState == DockerState.NotInstalled;
        public bool IsDockerStopped => DockerState == DockerState.NotRunning;

        public ContainersViewModel(IDockerService docker, IDialogService dialogs, IDispatcherService dispatcher)
        {
            _docker = docker;
            _dialogs = dialogs;
            _dispatcher = dispatcher;
            // Light polling so state stays in sync with Docker Desktop / CLI changes.
            _refreshTimer = new Timer(_ => _ = RefreshCoreAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Poll Docker only while the Container page is visible.</summary>
        public void SetActive(bool active) =>
            _refreshTimer.Change(active ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, active ? TimeSpan.FromSeconds(6) : Timeout.InfiniteTimeSpan);

        private void Log(string line) => _dispatcher.BeginInvoke(() =>
        {
            ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (ActivityLog.Count > 300) ActivityLog.RemoveAt(0);
        });

        private IProgress<string> NewLog() => new Progress<string>(Log);

        [RelayCommand]
        private Task Refresh() => RefreshCoreAsync();

        private async Task RefreshCoreAsync()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            try
            {
                var (state, detail) = await _docker.GetStateAsync();
                var list = state == DockerState.Running ? await _docker.ListAsync() : new();
                _dispatcher.BeginInvoke(() =>
                {
                    DockerState = state;
                    DockerDetail = detail;
                    IsChecking = false;
                    // Replace in place to keep the list stable.
                    Containers.Clear();
                    foreach (var c in list) Containers.Add(c);
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Containers] refresh failed");
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        }

        [RelayCommand]
        private void StartDocker()
        {
            if (_docker.TryStartDockerDesktop())
            {
                DockerDetail = "Starting Docker Desktop ... this can take up to a minute.";
                Log("Starting Docker Desktop.");
            }
            else
            {
                DockerDetail = "Docker Desktop was not found. Start your Docker engine manually.";
            }
        }

        [RelayCommand]
        private void InstallDocker() => OpenUrl("https://www.docker.com/products/docker-desktop/");

        [RelayCommand]
        private Task StartContainer(ContainerInfo? c) => Act(c, "Starting", _docker.StartAsync);

        [RelayCommand]
        private Task StopContainer(ContainerInfo? c) => Act(c, "Stopping", _docker.StopAsync);

        [RelayCommand]
        private Task RestartContainer(ContainerInfo? c) => Act(c, "Restarting", _docker.RestartAsync);

        [RelayCommand]
        private Task RemoveContainer(ContainerInfo? c)
        {
            if (c == null) return Task.CompletedTask;
            if (!_dialogs.Confirm("Remove container",
                    $"Remove container \"{c.Name}\"? It will be stopped and deleted. Named volumes (your data) are kept."))
                return Task.CompletedTask;
            return Act(c, "Removing", _docker.RemoveAsync);
        }

        private async Task Act(ContainerInfo? c, string verb, Func<string, CancellationToken, Task<(bool Ok, string Output)>> action)
        {
            if (c == null || IsBusy) return;
            IsBusy = true;
            Log($"{verb} {c.Name} ...");
            try
            {
                var (ok, output) = await action(c.Id, CancellationToken.None);
                Log(ok ? $"✔ {c.Name}: done." : $"✖ {c.Name}: {output}");
            }
            finally
            {
                IsBusy = false;
                await RefreshCoreAsync();
            }
        }

        [RelayCommand]
        private async Task ShowLogs(ContainerInfo? c)
        {
            if (c == null) return;
            LogsFor = c;
            LogsText = "Loading logs ...";
            LogsText = await _docker.LogsAsync(c.Id);
            if (string.IsNullOrWhiteSpace(LogsText)) LogsText = "(no output yet)";
        }

        [RelayCommand]
        private void CloseLogs() { LogsFor = null; LogsText = string.Empty; }

        [RelayCommand]
        private void OpenInBrowser(ContainerInfo? c)
        {
            if (c?.FirstHostPort is int port) OpenUrl($"http://localhost:{port}");
        }

        [RelayCommand]
        private async Task CreateFromTemplate(ContainerTemplate? t)
        {
            if (t == null || IsBusy) return;
            IsBusy = true;
            try
            {
                var name = $"zenvix-{t.Key}";
                var n = 2;
                while (Containers.Any(c => c.Name == name)) name = $"zenvix-{t.Key}-{n++}";
                await _docker.RunTemplateAsync(t, name, NewLog());
            }
            finally
            {
                IsBusy = false;
                await RefreshCoreAsync();
            }
        }

        [RelayCommand] private void ToggleCustomForm() => IsCustomFormOpen = !IsCustomFormOpen;

        [RelayCommand]
        private async Task RunCustom()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var (ok, _) = await _docker.RunCustomAsync(CustomName, CustomImage, CustomPorts, CustomEnv, NewLog());
                if (ok) { IsCustomFormOpen = false; CustomName = CustomImage = CustomEnv = string.Empty; }
            }
            finally
            {
                IsBusy = false;
                await RefreshCoreAsync();
            }
        }

        private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
