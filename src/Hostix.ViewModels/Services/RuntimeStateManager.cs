using System;
using System.Collections.ObjectModel;
using System.Linq;
using Hostix.Core.Models;

namespace Hostix.ViewModels.Services
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

    public class RuntimeStateManager : IRuntimeStateManager
    {
        private readonly IDispatcherService _dispatcher;

        public ObservableCollection<Service> ActiveServices   { get; } = new();
        public ObservableCollection<Service> DashboardServices { get; } = new();
        public ObservableCollection<Website> RunningWebsites  { get; } = new();
        public ObservableCollection<string>  RuntimeEvents    { get; } = new();

        public string CpuUsage           { get; set; } = "0%";
        public string RamUsage           { get; set; } = "0 GB";
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
