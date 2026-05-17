using System;
using System.Collections.Generic;
using Hostix.Core.Models;
using Hostix.Runtime.Services;
using Hostix.ViewModels.Services;
using Serilog;

namespace Hostix.ViewModels.Services
{
    /// <summary>
    /// Global runtime state synchronization hub.
    ///
    /// Flow (single source of truth → all pages):
    ///   ServicesOrchestrator
    ///     → RuntimeStateBridge
    ///       → IRuntimeStateManager  (Dashboard Active Services)
    ///       → IDatabaseOrchestrationService  (Databases page)
    /// </summary>
    public interface IRuntimeStateBridge
    {
        void Start();
        /// <summary>Resolve the ServicesOrchestrator instance ID for a ServiceType (used by Dashboard toggle).</summary>
        Guid? ResolveInstanceId(ServiceType type);
    }

    public class RuntimeStateBridge : IRuntimeStateBridge
    {
        private readonly IServicesOrchestrator           _orchestrator;
        private readonly IRuntimeStateManager            _stateManager;
        private readonly IDatabaseOrchestrationService   _dbService;
        private readonly IDispatcherService              _dispatcher;

        // ServicesOrchestrator type → RuntimeStateManager ServiceType
        private static readonly Dictionary<RuntimeServiceType, ServiceType> _toService = new()
        {
            [RuntimeServiceType.Nginx]      = ServiceType.Nginx,
            [RuntimeServiceType.Apache]     = ServiceType.Apache,
            [RuntimeServiceType.PhpFpm]     = ServiceType.PhpFpm,
            [RuntimeServiceType.MariaDB]    = ServiceType.MariaDB,
            [RuntimeServiceType.MySQL]      = ServiceType.MySQL,
            [RuntimeServiceType.PostgreSQL] = ServiceType.Postgres,
            [RuntimeServiceType.Redis]      = ServiceType.Redis,
            [RuntimeServiceType.Mailpit]    = ServiceType.Mailpit,
        };

        // ServicesOrchestrator type → DatabaseOrchestrationService DbEngineType
        private static readonly Dictionary<RuntimeServiceType, DbEngineType> _toDb = new()
        {
            [RuntimeServiceType.MariaDB]    = DbEngineType.MariaDB,
            [RuntimeServiceType.MySQL]      = DbEngineType.MySQL,
            [RuntimeServiceType.PostgreSQL] = DbEngineType.PostgreSQL,
            [RuntimeServiceType.Redis]      = DbEngineType.Redis,
            [RuntimeServiceType.MongoDB]    = DbEngineType.MongoDB,
        };

        private static readonly Dictionary<DbEngineType, RuntimeServiceType> _fromDb = new()
        {
            [DbEngineType.MariaDB]    = RuntimeServiceType.MariaDB,
            [DbEngineType.MySQL]      = RuntimeServiceType.MySQL,
            [DbEngineType.PostgreSQL] = RuntimeServiceType.PostgreSQL,
            [DbEngineType.Redis]      = RuntimeServiceType.Redis,
            [DbEngineType.MongoDB]    = RuntimeServiceType.MongoDB,
        };

        // Reverse for Dashboard → ServicesOrchestrator routing
        private static readonly Dictionary<ServiceType, RuntimeServiceType> _fromService = new()
        {
            [ServiceType.Nginx]    = RuntimeServiceType.Nginx,
            [ServiceType.Apache]   = RuntimeServiceType.Apache,
            [ServiceType.PhpFpm]   = RuntimeServiceType.PhpFpm,
            [ServiceType.MariaDB]  = RuntimeServiceType.MariaDB,
            [ServiceType.MySQL]    = RuntimeServiceType.MySQL,
            [ServiceType.Postgres] = RuntimeServiceType.PostgreSQL,
            [ServiceType.Redis]    = RuntimeServiceType.Redis,
            [ServiceType.Mailpit]  = RuntimeServiceType.Mailpit,
        };

        public RuntimeStateBridge(
            IServicesOrchestrator          orchestrator,
            IRuntimeStateManager           stateManager,
            IDatabaseOrchestrationService  dbService,
            IDispatcherService             dispatcher)
        {
            _orchestrator = orchestrator;
            _stateManager = stateManager;
            _dbService    = dbService;
            _dispatcher   = dispatcher;
        }

        public void Start()
        {
            _orchestrator.InstanceStateChanged += OnInstanceStateChanged;
            _dbService.InstanceStateChanged    += OnDbInstanceStateChanged;
            Log.Information("RuntimeStateBridge: global sync active — Dashboard + DB section wired.");
        }

        private void OnInstanceStateChanged(RuntimeServiceInstance instance)
        {
            _dispatcher.Invoke(() =>
            {
                // ─── 1. Sync → RuntimeStateManager (Dashboard) ───────────────────
                if (_toService.TryGetValue(instance.Type, out var serviceType))
                {
                    var svc = System.Linq.Enumerable.FirstOrDefault(
                        _stateManager.ActiveServices, s => s.Type == serviceType);

                    if (svc != null)
                    {
                        var prev = svc.Status;
                        svc.Status         = instance.Status;
                        svc.ProcessId      = instance.ProcessId;
                        svc.Version        = instance.Version;
                        svc.Notes          = instance.Notes;
                        svc.DiagnosticLogs = instance.DiagnosticLogs;
                        _stateManager.UpdateService(svc);

                        if (prev != instance.Status)
                        {
                            // Only log meaningful transitions to Runtime Activity
                            // Skip Starting→Stopped (binary not installed, silent fallback)
                            var meaningful = instance.Status == ServiceStatus.Running
                                          || instance.Status == ServiceStatus.Error
                                          || (instance.Status == ServiceStatus.Stopped && prev == ServiceStatus.Stopping);

                            if (meaningful)
                            {
                                var msg = $"{instance.Name} → {instance.Status}";
                                if (instance.Status == ServiceStatus.Error && !string.IsNullOrEmpty(instance.Notes))
                                    msg += $" ({instance.Notes})";
                                
                                _stateManager.AddEvent(msg);

                                // If we have full diagnostic logs on error, push them as separate events
                                if (instance.Status == ServiceStatus.Error && !string.IsNullOrEmpty(instance.DiagnosticLogs))
                                {
                                    var diagLines = instance.DiagnosticLogs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                    foreach (var line in diagLines.Take(10)) // Show first 10 diagnostic lines
                                    {
                                        _stateManager.AddEvent($"  [LOG] {line}");
                                    }
                                }
                            }

                            Log.Information("Bridge → Dashboard: {Name} {A} → {B}", instance.Name, prev, instance.Status);
                        }
                    }
                }

                // ─── 2. Sync → DatabaseOrchestrationService (Databases page) ─────
                if (_toDb.TryGetValue(instance.Type, out var dbEngine))
                {
                    var dbStatus = instance.Status switch
                    {
                        ServiceStatus.Running  => DbInstanceStatus.Running,
                        ServiceStatus.Starting => DbInstanceStatus.Starting,
                        ServiceStatus.Stopping => DbInstanceStatus.Stopping,
                        ServiceStatus.Error    => DbInstanceStatus.Error,
                        _                      => DbInstanceStatus.Stopped,
                    };

                    _dbService.SyncInstanceStatus(dbEngine, dbStatus, instance.ProcessId);
                    Log.Information("Bridge → DB section: {Engine} → {Status}", dbEngine, dbStatus);
                }
            });
        }

        private void OnDbInstanceStateChanged(DatabaseInstance dbInstance)
        {
            _dispatcher.Invoke(() =>
            {
                if (_fromDb.TryGetValue(dbInstance.Engine, out var runtimeType))
                {
                    if (_toService.TryGetValue(runtimeType, out var serviceType))
                    {
                        var svc = System.Linq.Enumerable.FirstOrDefault(
                            _stateManager.ActiveServices, s => s.Type == serviceType);

                        if (svc != null)
                        {
                            var prev = svc.Status;
                            svc.Status    = MapDbStatusToService(dbInstance.Status);
                            svc.ProcessId = dbInstance.ProcessId;
                            svc.Version   = dbInstance.Version;
                            _stateManager.UpdateService(svc);

                            if (prev != svc.Status)
                            {
                                Log.Information("Bridge → Dashboard (from DB): {Name} {A} → {B}", dbInstance.Name, prev, svc.Status);
                            }
                        }
                    }
                }
            });
        }

        private ServiceStatus MapDbStatusToService(DbInstanceStatus status) => status switch
        {
            DbInstanceStatus.Running  => ServiceStatus.Running,
            DbInstanceStatus.Starting => ServiceStatus.Starting,
            DbInstanceStatus.Stopping => ServiceStatus.Stopping,
            DbInstanceStatus.Error    => ServiceStatus.Error,
            _                         => ServiceStatus.Stopped,
        };

        public Guid? ResolveInstanceId(ServiceType type)
        {
            if (!_fromService.TryGetValue(type, out var rType)) return null;
            return System.Linq.Enumerable.FirstOrDefault(
                _orchestrator.Instances, i => i.Type == rType)?.Id;
        }
    }
}
