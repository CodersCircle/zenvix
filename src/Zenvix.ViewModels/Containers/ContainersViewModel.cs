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
        private readonly IContainerEngine _engine;
        private readonly IDialogService _dialogs;
        private readonly IClipboardService _clipboard;
        private readonly IDispatcherService _dispatcher;
        private readonly Timer _timer;

        public ObservableCollection<ContainerInstance> Containers { get; } = new();
        public ObservableCollection<string> ActivityLog { get; } = new();
        public System.Collections.Generic.IReadOnlyList<ContainerTemplate> Templates => _engine.Templates;

        [ObservableProperty] private bool _isBusy;

        // Create form
        [ObservableProperty] private bool _isCreateOpen;
        [ObservableProperty] private ContainerTemplate? _newTemplate;
        [ObservableProperty] private string _newName = "";
        [ObservableProperty] private string _newPort = "";
        [ObservableProperty] private string _newPassword = "";
        [ObservableProperty] private string _newMemoryMb = "0";

        // Logs panel
        [ObservableProperty] private ContainerInstance? _logsFor;
        [ObservableProperty] private string _logsText = "";

        public bool HasContainers => Containers.Count > 0;

        public ContainersViewModel(IContainerEngine engine, IDialogService dialogs, IClipboardService clipboard, IDispatcherService dispatcher)
        {
            _engine = engine;
            _dialogs = dialogs;
            _clipboard = clipboard;
            _dispatcher = dispatcher;
            _engine.ContainersChanged += () => _dispatcher.BeginInvoke(Sync);
            Containers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasContainers));
            _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            _ = _engine.InitializeAsync();
        }

        /// <summary>Poll usage/logs only while the Container page is visible.</summary>
        public void SetActive(bool active) =>
            _timer.Change(active ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, active ? TimeSpan.FromSeconds(3) : Timeout.InfiniteTimeSpan);

        private void Tick()
        {
            _engine.RefreshUsage();
            var c = LogsFor;
            if (c != null)
            {
                var text = _engine.ReadLog(c);
                _dispatcher.BeginInvoke(() => { if (LogsFor == c) LogsText = string.IsNullOrEmpty(text) ? "(no output yet)" : text; });
            }
        }

        private void Sync()
        {
            var current = _engine.Containers;
            foreach (var gone in Containers.Where(c => !current.Contains(c)).ToList()) Containers.Remove(gone);
            foreach (var c in current.Where(c => !Containers.Contains(c))) Containers.Add(c);
        }

        private IProgress<string> NewLog() => new Progress<string>(line => _dispatcher.BeginInvoke(() =>
        {
            ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (ActivityLog.Count > 300) ActivityLog.RemoveAt(0);
        }));

        // ───────────── create ─────────────

        [RelayCommand]
        private void OpenCreate(ContainerTemplate? template)
        {
            NewTemplate = template ?? Templates.First();
            var n = 1;
            var baseName = NewTemplate.Kind.ToString().ToLowerInvariant();
            var name = baseName;
            while (Containers.Any(c => c.Name == name)) name = $"{baseName}-{++n}";
            NewName = name;
            NewPort = "";
            NewPassword = NewTemplate.UsesPassword ? "secret" : "";
            NewMemoryMb = "0";
            IsCreateOpen = true;
        }

        [RelayCommand] private void CancelCreate() => IsCreateOpen = false;

        [RelayCommand]
        private async Task Create()
        {
            if (NewTemplate == null || IsBusy) return;
            int? port = int.TryParse(NewPort, out var p) && p > 0 ? p : null;
            int.TryParse(NewMemoryMb, out var mem);
            IsBusy = true;
            IsCreateOpen = false;
            var log = NewLog();
            try { await _engine.CreateAsync(NewTemplate.Kind, NewName, port, NewPassword, mem, log); }
            catch (Exception ex) { log.Report($"✖ {ex.Message}"); }
            finally { IsBusy = false; }
        }

        // ───────────── lifecycle ─────────────

        [RelayCommand] private Task Start(ContainerInstance? c) => c == null ? Task.CompletedTask : _engine.StartAsync(c, NewLog());
        [RelayCommand] private Task Stop(ContainerInstance? c) => c == null ? Task.CompletedTask : _engine.StopAsync(c, NewLog());

        [RelayCommand]
        private async Task Restart(ContainerInstance? c)
        {
            if (c == null) return;
            await _engine.StopAsync(c, NewLog());
            await _engine.StartAsync(c, NewLog());
        }

        [RelayCommand]
        private async Task Remove(ContainerInstance? c)
        {
            if (c == null) return;
            if (!_dialogs.Confirm("Remove container", $"Remove container \"{c.Name}\"? It will be stopped.")) return;
            var deleteData = _dialogs.Confirm("Delete data too?",
                $"Also delete all data of \"{c.Name}\" (databases, files) from\n{c.Folder}?\n\nChoose No to keep the data folder.");
            if (LogsFor == c) CloseLogs();
            await _engine.RemoveAsync(c, deleteData);
            NewLog().Report($"Removed {c.Name}{(deleteData ? " and its data" : " (data kept)")}.");
        }

        // ───────────── tools ─────────────

        [RelayCommand]
        private void ShowLogs(ContainerInstance? c)
        {
            if (c == null) return;
            LogsFor = c;
            LogsText = _engine.ReadLog(c);
            if (string.IsNullOrEmpty(LogsText)) LogsText = "(no output yet)";
        }

        [RelayCommand] private void CloseLogs() { LogsFor = null; LogsText = ""; }

        [RelayCommand]
        private void CopyConnection(ContainerInstance? c)
        {
            if (c == null) return;
            _clipboard.SetText(c.ConnectionString);
            NewLog().Report($"Copied connection string for {c.Name}.");
        }

        [RelayCommand]
        private void CopyEnv(ContainerInstance? c)
        {
            if (c == null) return;
            _clipboard.SetText(c.EnvSnippet);
            NewLog().Report($"Copied .env settings for {c.Name}.");
        }

        [RelayCommand]
        private void OpenFolder(ContainerInstance? c)
        {
            if (c != null) Process.Start("explorer.exe", $"\"{c.Folder}\"");
        }

        [RelayCommand]
        private void OpenInbox(ContainerInstance? c)
        {
            if (c?.Spec.ExtraPort is int port)
                Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true });
        }
    }
}
