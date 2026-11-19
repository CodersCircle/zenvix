using System;
using System.Collections.ObjectModel;
using System.Linq;
using Zenvix.Core.Models;

namespace Zenvix.ViewModels.Services
{
    public interface IRuntimeStateManager
    {
        /// <summary>All services — used by Services page and Databases page.</summary>
        ObservableCollection<Service> ActiveServices { get; }
        /// <summary>Only services marked ShowOnDashboard=true (Nginx, Apache, MySQL, Mailpit).</summary>
        ObservableCollection<Service> DashboardServices { get; }
        ObservableCollection<Website> RunningWebsites { get; }
        ObservableCollection<string> RuntimeEvents { get; }

        string CpuUsage { get; set; }
        string RamUsage { get; set; }
        string InfrastructureState { get; set; }
        string SystemHealthStatus { get; set; }

        void AddEvent(string message);
        void UpdateService(Service service);
    }

    public class RuntimeStateManager : IRuntimeStateManager, System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        private string _cpuUsage = "0%";
        private string _ramUsage = "0 GB";
        private readonly IDispatcherService _dispatcher;

        public ObservableCollection<Service> ActiveServices   { get; } = new();
        public ObservableCollection<Service> DashboardServices { get; } = new();
        public ObservableCollection<Website> RunningWebsites  { get; } = new();
        public ObservableCollection<string>  RuntimeEvents    { get; } = new();

        // Notify so Dashboard and top bar update live.
        public string CpuUsage { get => _cpuUsage; set { if (_cpuUsage != value) { _cpuUsage = value; Notify(); } } }
        public string RamUsage { get => _ramUsage; set { if (_ramUsage != value) { _ramUsage = value; Notify(); } } }
        public string InfrastructureState { get; set; } = "Idle";
        public string SystemHealthStatus  { get; set; } = "Healthy";

        public RuntimeStateManager(IDispatcherService dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void AddEvent(string message)
        {
            _dispatcher.Invoke(() => {
                RuntimeEvents.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
                if (RuntimeEvents.Count > 50) RuntimeEvents.RemoveAt(50);
            });
        }

        public void UpdateService(Service service)
        {
            _dispatcher.Invoke(() => {
                // ── ActiveServices ─────────────────────────────────────────────
                var existing = ActiveServices.FirstOrDefault(s => s.Type == service.Type);
                if (existing != null)
                {
                    // Update IN PLACE — preserves INotifyPropertyChanged bindings.
                    existing.Status    = service.Status;
                    existing.ProcessId = service.ProcessId;

                    // ── DashboardServices shares the SAME object reference ─────
                    // We do NOT update dash.Status separately — the in-place mutation
                    // of `existing` already fires INotifyPropertyChanged, which WPF
                    // picks up on both bindings automatically.
                    // Just ensure the reference is in DashboardServices if needed.
                    if (service.ShowOnDashboard || existing.ShowOnDashboard)
                    {
                        if (!DashboardServices.Contains(existing))
                            DashboardServices.Add(existing);
                    }
                }
                else
                {
                    ActiveServices.Add(service);
                    if (service.ShowOnDashboard)
                        DashboardServices.Add(service); // same reference
                }
            });
        }
    }
}
