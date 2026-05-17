using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Serilog;

namespace Hostix.Runtime.Services
{
    public interface IRuntimeEngine
    {
        IEnumerable<Service> GetActiveServices();
        Task<bool> StartServiceAsync(ServiceType type);
        Task<bool> StopServiceAsync(ServiceType type);
        void StartHeartbeat();
        Task InitializeAsync();
        event Action<Service>? ServiceStateChanged;
    }

    public class RuntimeEngine : IRuntimeEngine
    {
        private readonly IProcessManager _processManager;
        private readonly IServiceOrchestrator _orchestrator;
        private readonly List<Service> _services = new();
        private readonly object _lock = new();

        public event Action<Service>? ServiceStateChanged;

        public RuntimeEngine(IProcessManager processManager, IServiceOrchestrator orchestrator)
        {
            _processManager = processManager;
            _orchestrator = orchestrator;
            InitializeServiceRegistry();
        }

        private void InitializeServiceRegistry()
        {
            lock (_lock)
            {
                _services.Add(new Service { Type = ServiceType.Nginx,    Name = "Nginx Web Server",  Port = 80,   ShowOnDashboard = true  });
                _services.Add(new Service { Type = ServiceType.Apache,   Name = "Apache Web Server", Port = 8080, ShowOnDashboard = true  });
                _services.Add(new Service { Type = ServiceType.MariaDB,  Name = "MariaDB Database",  Port = 3306, ShowOnDashboard = false });
                _services.Add(new Service { Type = ServiceType.MySQL,    Name = "MySQL Database",    Port = 3307, ShowOnDashboard = true  });
                _services.Add(new Service { Type = ServiceType.Postgres, Name = "PostgreSQL",        Port = 5432, ShowOnDashboard = false });
                _services.Add(new Service { Type = ServiceType.Redis,    Name = "Redis Cache",       Port = 6379, ShowOnDashboard = false });
                _services.Add(new Service { Type = ServiceType.Mailpit,  Name = "Mailpit SMTP",      Port = 1025, ShowOnDashboard = true  });
            }
        }

        /// <summary>
        /// Called once on app startup:
        /// 1. Detects any externally running instances of managed services.
        /// 2. Stops them so Hostix has clean ownership.
        /// 3. All services start in Stopped state — user must manually start.
        /// </summary>
        public async Task InitializeAsync()
        {
            Log.Information("Hostix RuntimeEngine: scanning for existing service instances...");

            var stoppedTypes = await _orchestrator.DetectAndStopExternalInstancesAsync();

            if (stoppedTypes.Any())
            {
                Log.Information("Cleaned up {Count} external service instance(s). All services are now Stopped.", stoppedTypes.Count);
                foreach (var type in stoppedTypes)
                {
                    var service = _services.FirstOrDefault(s => s.Type == type);
                    if (service != null)
                    {
                        service.Status = ServiceStatus.Stopped;
                        service.ProcessId = null;
                    }
                }
            }
            else
            {
                Log.Information("No conflicting service instances found. Ready.");
            }
        }

        public IEnumerable<Service> GetActiveServices()
        {
            lock (_lock) return _services.ToList();
        }

        public async Task<bool> StartServiceAsync(ServiceType type)
        {
            Service? service;
            lock (_lock) service = _services.FirstOrDefault(s => s.Type == type);
            if (service == null || service.Status == ServiceStatus.Running) return false;

            try
            {
                service.Status = ServiceStatus.Starting;
                ServiceStateChanged?.Invoke(service);
                Log.Information("Starting {ServiceName}...", service.Name);

                var success = await _orchestrator.StartAsync(type);

                if (success)
                {
                    service.Status    = ServiceStatus.Running;
                    service.ProcessId = GetRealPid(type);
                }
                else
                {
                    // Distinguish: missing binary (→ Stopped) vs real launch failure (→ Error)
                    service.Status = _orchestrator.GetBinaryPath(type) == null
                        ? ServiceStatus.Stopped   // not installed — silent, no alarming Error
                        : ServiceStatus.Error;    // binary exists but process failed
                }

                Log.Information("{ServiceName} → {Status}", service.Name, service.Status);
                ServiceStateChanged?.Invoke(service);
                return success;
            }
            catch (Exception ex)
            {
                service.Status = ServiceStatus.Error;
                Log.Error(ex, "Failed to start {ServiceName}", service.Name);
                ServiceStateChanged?.Invoke(service);
                return false;
            }
        }

        public async Task<bool> StopServiceAsync(ServiceType type)
        {
            Service? service;
            lock (_lock) service = _services.FirstOrDefault(s => s.Type == type);
            if (service == null || service.Status == ServiceStatus.Stopped) return false;

            try
            {
                service.Status = ServiceStatus.Stopping;
                ServiceStateChanged?.Invoke(service);

                var success = await _orchestrator.StopAsync(type);

                service.Status = ServiceStatus.Stopped;
                service.ProcessId = null;
                Log.Information("{ServiceName} stopped.", service.Name);
                ServiceStateChanged?.Invoke(service);
                return success;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping {ServiceName}", service.Name);
                service.Status = ServiceStatus.Error;
                ServiceStateChanged?.Invoke(service);
                return false;
            }
        }

        public void StartHeartbeat()
        {
            // ServicesOrchestrator runs its own health loop (5s interval).
            // RuntimeEngine heartbeat is disabled to prevent it from overriding
            // the bridge-synced state back to Error for simulated/uninstalled services.
            Log.Information("RuntimeEngine: delegating health monitoring to ServicesOrchestrator.");
        }

        private void OnHeartbeat(object? state)
        {
            // Heartbeat disabled — ServicesOrchestrator manages health.
        }

        private int? GetRealPid(ServiceType type)
        {
            try
            {
                return type switch
                {
                    ServiceType.Nginx    => Process.GetProcessesByName("nginx").FirstOrDefault()?.Id,
                    ServiceType.Apache   => Process.GetProcessesByName("httpd").FirstOrDefault()?.Id,
                    ServiceType.MariaDB  => (Process.GetProcessesByName("mariadbd").FirstOrDefault()
                                          ?? Process.GetProcessesByName("mysqld").FirstOrDefault())?.Id,
                    ServiceType.MySQL    => Process.GetProcessesByName("mysqld").FirstOrDefault()?.Id,
                    ServiceType.Postgres => Process.GetProcessesByName("postgres").FirstOrDefault()?.Id,
                    ServiceType.Redis    => Process.GetProcessesByName("redis-server").FirstOrDefault()?.Id,
                    ServiceType.Mailpit  => Process.GetProcessesByName("mailpit").FirstOrDefault()?.Id,
                    _                    => null
                };
            }
            catch { return null; }
        }
    }
}
