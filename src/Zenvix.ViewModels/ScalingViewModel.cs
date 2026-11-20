using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zenvix.Modules.Scaling;
using Zenvix.ViewModels.Services;

namespace Zenvix.ViewModels
{
    public partial class ScalingProjectItem : ObservableObject
    {
        public ScalingPool Pool { get; }
        public ScalingProjectItem(ScalingPool pool) => Pool = pool;
        public string Name => Pool.Website.Name;
        public string Domain => Pool.Website.Domain;
        public string KindLabel => Pool.Kind == PoolKind.Php ? "PHP-FPM" : "Node.js";
        [ObservableProperty] private string _state = "Off";
        [ObservableProperty] private bool _isRunning;
    }

    public partial class InstanceItem : ObservableObject
    {
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _id = "";
        [ObservableProperty] private int _port;
        [ObservableProperty] private int _requests;
        [ObservableProperty] private int _capacity;
        [ObservableProperty] private double _utilization;
        [ObservableProperty] private string _cpu = "";
        [ObservableProperty] private string _ram = "";
        [ObservableProperty] private string _status = "";
        [ObservableProperty] private bool _isHealthy;
    }

    /// <summary>Auto Scaling: per-project local load balancer, instances, traffic simulation and events.</summary>
    public partial class ScalingViewModel : ObservableObject
    {
        private readonly IScalingManager _scaling;
        private readonly IDispatcherService _dispatcher;
        private readonly Timer _timer;
        private int _eventsShown;
        private bool _active;

        public ObservableCollection<ScalingProjectItem> Projects { get; } = new();
        public ObservableCollection<InstanceItem> Instances { get; } = new();
        public ObservableCollection<string> Events { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        private ScalingProjectItem? _selected;
        public bool HasSelection => Selected != null;
        public bool HasProjects => Projects.Count > 0;

        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _balancerUrl = "";
        [ObservableProperty] private string _instanceSummary = "0 / 0";
        [ObservableProperty] private string _requestsPerSecond = "0";
        [ObservableProperty] private string _avgCpu = "0%";
        [ObservableProperty] private string _totalRam = "0 MB";
        [ObservableProperty] private string _utilization = "0%";
        [ObservableProperty] private string _trafficInfo = "";

        // configuration
        [ObservableProperty] private int _minInstances = 1;
        [ObservableProperty] private int _maxInstances = 5;
        [ObservableProperty] private int _capacity = 60;
        [ObservableProperty] private bool _autoScale = true;
        [ObservableProperty] private int _trafficRps;

        public ScalingViewModel(IScalingManager scaling, IDispatcherService dispatcher)
        {
            _scaling = scaling;
            _dispatcher = dispatcher;
            Projects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProjects));
            _timer = new Timer(_ => _dispatcher.BeginInvoke(Tick), null, 1000, 1000);
        }

        /// <summary>Called by navigation; the list of projects is refreshed when the page opens.</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (!active) return;
            _scaling.Refresh();
            foreach (var pool in _scaling.Pools)
                if (Projects.All(p => p.Pool != pool)) Projects.Add(new ScalingProjectItem(pool));
            foreach (var gone in Projects.Where(p => !_scaling.Pools.Contains(p.Pool)).ToList()) Projects.Remove(gone);
            Selected ??= Projects.FirstOrDefault();
            Tick();
        }

        partial void OnSelectedChanged(ScalingProjectItem? value)
        {
            Instances.Clear();
            Events.Clear();
            _eventsShown = 0;
            if (value == null) return;
            var s = value.Pool.Settings;
            MinInstances = s.MinInstances;
            MaxInstances = s.MaxInstances;
            Capacity = s.CapacityPerInstance;
            AutoScale = s.AutoScale;
            TrafficRps = value.Pool.SimulatedRps;
            Tick();
        }

        partial void OnTrafficRpsChanged(int value)
        {
            if (Selected != null && Selected.Pool.IsRunning) _scaling.SetTraffic(Selected.Pool, value);
        }

        private void Tick()
        {
            foreach (var p in Projects)
            {
                p.IsRunning = p.Pool.IsRunning;
                p.State = p.Pool.IsRunning ? $"{p.Pool.Instances.Count} running" : "Off";
            }
            if (!_active || Selected == null) return;

            var pool = Selected.Pool;
            IsRunning = pool.IsRunning;
            BalancerUrl = pool.BalancerUrl;
            var list = pool.Instances.ToList();
            var cap = Math.Max(1, pool.Settings.CapacityPerInstance);
            var perMinute = list.Sum(i => i.Requests60);
            InstanceSummary = $"{list.Count} / {pool.Settings.MaxInstances}";
            RequestsPerSecond = (perMinute / 60.0).ToString("0.0");
            AvgCpu = list.Count == 0 ? "0%" : $"{list.Average(i => i.CpuPercent):0}%";
            TotalRam = $"{list.Sum(i => i.RamMb):0} MB";
            Utilization = list.Count == 0 ? "0%" : $"{Math.Min(100, perMinute * 100.0 / (cap * list.Count)):0}%";
            TrafficInfo = pool.SimulatedRps > 0 || pool.SimulatedSent > 0
                ? $"Sent {pool.SimulatedSent} · failed {pool.SimulatedFailed}" : "";

            // instances, updated in place so the cards don't flicker
            while (Instances.Count > list.Count) Instances.RemoveAt(Instances.Count - 1);
            for (var k = 0; k < list.Count; k++)
            {
                if (k >= Instances.Count) Instances.Add(new InstanceItem());
                var src = list[k];
                var dst = Instances[k];
                dst.Name = src.Name;
                dst.Id = src.Id;
                dst.Port = src.Port;
                dst.Requests = src.Requests60;
                dst.Capacity = cap;
                dst.Utilization = Math.Min(100, src.Requests60 * 100.0 / cap);
                dst.Cpu = $"{src.CpuPercent:0.0}%";
                dst.Ram = $"{src.RamMb:0} MB";
                dst.Status = src.Status;
                dst.IsHealthy = src.Status == "running";
            }

            string[] events;
            lock (pool.Events) events = pool.Events.Skip(Math.Min(_eventsShown, pool.Events.Count)).ToArray();
            if (_eventsShown > pool.Events.Count) { Events.Clear(); _eventsShown = 0; }
            foreach (var e in events) Events.Insert(0, e);
            _eventsShown += events.Length;
            while (Events.Count > 300) Events.RemoveAt(Events.Count - 1);
        }

        [RelayCommand]
        private async Task ToggleBalancer()
        {
            if (Selected == null || IsBusy) return;
            IsBusy = true;
            try
            {
                if (Selected.Pool.IsRunning) await _scaling.StopAsync(Selected.Pool);
                else
                {
                    _scaling.ApplySettings(Selected.Pool, MinInstances, MaxInstances, Capacity, AutoScale);
                    await _scaling.StartAsync(Selected.Pool);
                }
                TrafficRps = Selected.Pool.SimulatedRps;
            }
            finally { IsBusy = false; Tick(); }
        }

        [RelayCommand]
        private async Task ScaleUp()
        {
            if (Selected?.Pool.IsRunning != true) return;
            await _scaling.ScaleToAsync(Selected.Pool, Selected.Pool.Instances.Count + 1, "manual");
        }

        [RelayCommand]
        private async Task ScaleDown()
        {
            if (Selected?.Pool.IsRunning != true) return;
            await _scaling.ScaleToAsync(Selected.Pool, Math.Max(1, Selected.Pool.Instances.Count - 1), "manual");
        }

        [RelayCommand]
        private void ApplyConfig()
        {
            if (Selected == null) return;
            _scaling.ApplySettings(Selected.Pool, MinInstances, MaxInstances, Capacity, AutoScale);
            MinInstances = Selected.Pool.Settings.MinInstances;
            MaxInstances = Selected.Pool.Settings.MaxInstances;
        }

        [RelayCommand] private void IncreaseLoad() => TrafficRps = Math.Min(200, TrafficRps + 5);
        [RelayCommand] private void DecreaseLoad() => TrafficRps = Math.Max(0, TrafficRps - 5);
        [RelayCommand] private void StopTraffic() => TrafficRps = 0;

        [RelayCommand]
        private void OpenBalancer()
        {
            if (Selected?.Pool.IsRunning == true)
                try { Process.Start(new ProcessStartInfo(Selected.Pool.BalancerUrl) { UseShellExecute = true }); } catch { }
        }
    }
}
