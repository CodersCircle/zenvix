using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zenvix.Core.Models;
using Zenvix.Modules.Toolchain;
using Zenvix.Runtime.Services;
using Zenvix.ViewModels.Services;

namespace Zenvix.ViewModels
{
    public partial class UpdateRow : ObservableObject
    {
        public UpdateItem Item { get; }

        public UpdateRow(UpdateItem item)
        {
            Item = item;
            // Pre-select the newest version; for project packages stay on the installed major line.
            _selectedVersion = item.Versions.FirstOrDefault(v => item.Kind == UpdateKind.Tool
                                   || ToolchainManager.ParseVersion(v).Major == ToolchainManager.ParseVersion(item.Installed ?? "0").Major)
                               ?? item.Versions.FirstOrDefault();
        }

        public string Name => Item.Name;
        public string Description => Item.Description;
        public bool IsTool => Item.Kind == UpdateKind.Tool;
        public IReadOnlyList<string> Versions => Item.Versions;
        public bool HasVersions => Item.Versions.Count > 0;
        /// <summary>"5.11.4 / 4.3.2" – newest of the current and previous release line.</summary>
        public string VersionsLabel => Item.Versions.Count == 0 ? "—" : string.Join(" / ", Item.Versions);

        // ── Zenvix copy ──
        public string Installed => Item.Installed ?? "Not installed";
        public string Latest => Item.Latest ?? "—";
        public bool HasUpdate => Item.UpdateAvailable;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(CanAct))]
        private string? _selectedVersion;

        public string ActionLabel
        {
            get
            {
                if (!IsTool) return Item.IsMajorUpgrade && SelectedVersion == Item.Latest ? "Major (guide)" : Item.UpdateAvailable ? "Update" : "Up to date";
                if (!Item.IsInstalled) return "Install";
                if (SelectedVersion == Item.Installed) return "Installed";
                return ToolchainManager.IsNewer(SelectedVersion ?? "", Item.Installed!) ? "Update" : "Switch";
            }
        }

        public bool CanAct => !IsBusy && (IsTool
            ? SelectedVersion != null && SelectedVersion != Item.Installed
            : Item.UpdateAvailable && !Item.IsMajorUpgrade);

        // ── The user's own install (outside Zenvix) ──
        public bool HasSystem => Item.System != null;
        public string SystemVersion => Item.System?.Version ?? (HasSystem ? "unknown" : "Not installed");
        public string SystemPath => Item.System?.Path ?? "";
        public bool SystemUpdateAvailable => Item.SystemUpdateAvailable;
        public bool CanUpdateSystem => !IsBusy && SystemUpdateAvailable;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanUpdateSystem))]
        private bool _isBusy;

        [ObservableProperty] private string _status = "";
    }

    /// <summary>One PHP extension switch in Settings.</summary>
    public partial class PhpExtensionRow : ObservableObject
    {
        private readonly Action<PhpExtensionRow> _changed;
        public PhpExtensionRow(string name, bool on, Action<PhpExtensionRow> changed)
        {
            Name = name;
            _isEnabled = on;
            _changed = changed;
            Description = PhpExtensions.Descriptions.TryGetValue(name, out var d) ? d : name;
        }
        public string Name { get; }
        public string Description { get; }
        [ObservableProperty] private bool _isEnabled;
        partial void OnIsEnabledChanged(bool value) => _changed(this);
    }

    /// <summary>Settings → Services &amp; Updates.</summary>
    public partial class UpdatesViewModel : ObservableObject
    {
        private readonly IUpdateService _updates;
        private readonly IWebsiteOrchestrator _websites;
        private readonly IDispatcherService _dispatcher;
        private readonly IRuntimeStateManager _state;
        private readonly IServicesOrchestrator _services;

        public ObservableCollection<UpdateRow> Tools { get; } = new();
        public ObservableCollection<UpdateRow> ProjectPackages { get; } = new();
        public ObservableCollection<string> Log { get; } = new();
        public ObservableCollection<PhpExtensionRow> PhpExtensionList { get; } = new();

        [ObservableProperty] private string _phpVersionLabel = "";
        [ObservableProperty] private string _extensionsStatus = "";

        [ObservableProperty] private bool _isChecking;
        [ObservableProperty] private int _availableCount;
        [ObservableProperty] private string _lastChecked = "Not checked yet";

        public bool HasProjectPackages => ProjectPackages.Count > 0;

        public UpdatesViewModel(IUpdateService updates, IWebsiteOrchestrator websites, IDispatcherService dispatcher, IRuntimeStateManager state,
                                IServicesOrchestrator services)
        {
            _services = services;
            _updates = updates;
            _websites = websites;
            _dispatcher = dispatcher;
            _state = state;
            ProjectPackages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProjectPackages));
            _updates.UpdatesFound += items => _dispatcher.BeginInvoke(() => Show(items));
            // Startup + every 12 hours.
            _updates.StartBackgroundChecks(() => _websites.Websites.ToList(), TimeSpan.FromHours(12));
            _ = LoadExtensionsAsync();
        }

        /// <summary>Lists the extensions of the active PHP with their on/off state.</summary>
        private async Task LoadExtensionsAsync()
        {
            try
            {
                var meta = await _services.GetRuntimeMetadataAsync(RuntimeServiceType.PhpFpm);
                if (meta?.BinDir == null) return;
                var available = PhpExtensions.Available(System.IO.Path.Combine(meta.BinDir, "ext"));
                var enabled = PhpExtensions.Enabled();
                _dispatcher.BeginInvoke(() =>
                {
                    PhpVersionLabel = $"PHP {meta.Version}";
                    PhpExtensionList.Clear();
                    foreach (var n in available)
                        PhpExtensionList.Add(new PhpExtensionRow(n, enabled.Contains(n), OnExtensionToggled));
                });
            }
            catch (Exception ex) { AddLog($"✖ Could not read PHP extensions: {ex.Message}"); }
        }

        private async void OnExtensionToggled(PhpExtensionRow row)
        {
            try
            {
                PhpExtensions.SetEnabled(row.Name, row.IsEnabled);
                ExtensionsStatus = $"{(row.IsEnabled ? "Enabling" : "Disabling")} {row.Name}, restarting PHP...";
                await RestartPhpAsync();
                ExtensionsStatus = $"✔ {row.Name} {(row.IsEnabled ? "enabled" : "disabled")}.";
            }
            catch (Exception ex) { ExtensionsStatus = $"✖ {ex.Message}"; }
        }

        /// <summary>Restarts running PHP so a new php.ini / PHP version takes effect.</summary>
        private async Task RestartPhpAsync()
        {
            var running = _services.Instances.Where(i => i.Type == RuntimeServiceType.PhpFpm && i.Status == ServiceStatus.Running).ToList();
            foreach (var php in running) await _services.RestartAsync(php.Id);
        }

        private void Show(IReadOnlyList<UpdateItem> items)
        {
            Tools.Clear();
            ProjectPackages.Clear();
            foreach (var i in items)
                (i.Kind == UpdateKind.Tool ? Tools : ProjectPackages).Add(new UpdateRow(i));

            var available = items.Where(i => i.UpdateAvailable || i.SystemUpdateAvailable).ToList();
            if (available.Count > AvailableCount)
            {
                var names = string.Join(", ", available.Select(i => $"{i.Name} {i.Latest}").Take(4));
                _state.AddEvent($"🔔 {available.Count} update(s) available: {names}. Open Settings → Services & Updates.");
            }
            AvailableCount = available.Count;
            LastChecked = $"Checked {_updates.LastChecked:HH:mm}";
        }

        [RelayCommand]
        private async Task CheckNow()
        {
            if (IsChecking) return;
            IsChecking = true;
            try { await _updates.CheckAsync(_websites.Websites.ToList()); }
            catch (Exception ex) { AddLog($"✖ {ex.Message}"); }
            finally { IsChecking = false; }
        }

        /// <summary>Install / update / switch the Zenvix-managed copy (or update a project package).</summary>
        [RelayCommand]
        private Task Apply(UpdateRow? row) => Run(row, (r, log) =>
            r.IsTool && r.SelectedVersion != null
                ? _updates.InstallVersionAsync(r.Item, r.SelectedVersion, log)
                : _updates.ApplyAsync(r.Item, log));

        /// <summary>Update the user's own installation (outside Zenvix).</summary>
        [RelayCommand]
        private Task UpdateSystem(UpdateRow? row) => Run(row, (r, log) => _updates.UpdateSystemAsync(r.Item, log));

        private async Task Run(UpdateRow? row, Func<UpdateRow, IProgress<string>, Task<bool>> action)
        {
            if (row == null || row.IsBusy) return;
            row.IsBusy = true;
            row.Status = "Working...";
            var progress = new Progress<string>(line => { row.Status = line.Length > 80 ? line[..80] + "…" : line; AddLog(line); });
            var ok = await action(row, progress);
            if (ok && row.Item.Tool == ToolKind.Php)
            {
                AddLog("Restarting PHP with the selected version...");
                await RestartPhpAsync();
                await LoadExtensionsAsync();
            }
            row.Status = ok ? "✔ Done" : row.Status.StartsWith("✖") ? row.Status : "✖ Not completed – see details above";
            row.IsBusy = false;
            if (ok) await CheckNow();
        }

        private void AddLog(string line) => _dispatcher.BeginInvoke(() =>
        {
            Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (Log.Count > 300) Log.RemoveAt(0);
        });
    }
}
