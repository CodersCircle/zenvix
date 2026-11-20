using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Zenvix.Core.Services;
using Zenvix.Runtime.Services;
using Zenvix.ViewModels;
using Zenvix.ViewModels.Services;
using Zenvix.Modules.Services;
using Zenvix.UI.Services;

namespace Zenvix.UI
{
    public partial class App : System.Windows.Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            Zenvix.Shared.LoggingConfiguration.Setup();
            RegisterGlobalExceptionHandlers();

            base.OnStartup(e);

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            ServiceProvider = serviceCollection.BuildServiceProvider();

            // Initialize Services
            var stateManager = ServiceProvider.GetRequiredService<IRuntimeStateManager>();
            stateManager.AddEvent("Zenvix started.");

            // Launch Main Window
            var mainWindow = new MainWindow();
            mainWindow.DataContext = ServiceProvider.GetRequiredService<MainViewModel>();
            mainWindow.Show();

            // Quietly copy databases from XAMPP / WAMP / Laragon / a local MySQL into Zenvix MySQL, once per
            // database, so projects keep their data even if that stack is uninstalled. Log only, no UI.
            var importer = ServiceProvider.GetRequiredService<ILegacyDatabaseImporter>();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(20));
                    await importer.AutoImportAsync();
                }
                catch (Exception ex) { Serilog.Log.Warning(ex, "[Import] background import failed"); }
            });
        }

        private bool _errorDialogOpen;
        private string? _lastErrorMessage;
        private DateTime _lastErrorShown;

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                Serilog.Log.Error(args.Exception, "Unhandled UI exception");
                args.Handled = true;

                // One error can repeat for every item in a list – show a single dialog, never a pile.
                var message = args.Exception.InnerException?.Message ?? args.Exception.Message;
                if (_errorDialogOpen || (message == _lastErrorMessage && DateTime.Now - _lastErrorShown < TimeSpan.FromSeconds(30)))
                    return;

                _errorDialogOpen = true;
                _lastErrorMessage = message;
                _lastErrorShown = DateTime.Now;
                try
                {
                    System.Windows.MessageBox.Show(
                        $"Something went wrong:\n\n{message}\n\nDetails were written to logs\\zenvix.log.",
                        "Zenvix", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { _errorDialogOpen = false; }
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Serilog.Log.Fatal(args.ExceptionObject as Exception, "Fatal unhandled exception");
                Serilog.Log.CloseAndFlush();
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Serilog.Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Serilog.Log.Information("Zenvix shutting down – stopping managed services.");
            try { ServiceProvider?.GetService<Zenvix.Modules.Deployment.IShareTunnelService>()?.StopAll(); } catch { }
            try { ServiceProvider?.GetService<Zenvix.Modules.Containers.IContainerEngine>()?.StopAllAsync().Wait(TimeSpan.FromSeconds(20)); } catch { }
            try
            {
                // Don't leave orphaned nginx/mysqld/php-cgi holding ports after the window closes.
                var orchestrator = ServiceProvider?.GetService<IServicesOrchestrator>();
                if (orchestrator != null)
                {
                    var running = orchestrator.Instances
                        .Where(i => i.ProcessId.HasValue || i.Status == Zenvix.Core.Models.ServiceStatus.Running)
                        .Select(i => orchestrator.StopAsync(i.Id))
                        .ToArray();
                    System.Threading.Tasks.Task.WaitAll(running, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to stop services on exit"); }
            Serilog.Log.CloseAndFlush();
            base.OnExit(e);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Infrastructure & Runtime
            services.AddSingleton<IProcessManager, ProcessManager>();
            services.AddSingleton<IServiceOrchestrator, ServiceOrchestrator>();
            services.AddSingleton<IRuntimeEngine, RuntimeEngine>();
            services.AddSingleton<ICommunicationServer, NamedPipeServer>();
            services.AddSingleton<IEmbeddedToolsOrchestrator, EmbeddedToolsOrchestrator>();
            services.AddSingleton<IDatabaseOrchestrationService, DatabaseOrchestrationService>();
            services.AddSingleton<IPhpMyAdminService, PhpMyAdminService>();
            services.AddSingleton<ILocalStackConflictResolver, LocalStackConflictResolver>();
            services.AddSingleton<IServicesOrchestrator, ServicesOrchestrator>();

            // Modules
            services.AddSingleton<IDomainManager, HostsManager>();
            services.AddSingleton<ISSLManager, SSLManager>();
            services.AddSingleton<IEnvironmentManager, EnvironmentManager>();
            services.AddSingleton<IProjectScanner, ProjectScanner>();
            services.AddSingleton<ITemplateGenerator, TemplateGenerator>();
            services.AddSingleton<IAIDiagnosticService, AIDiagnosticService>();
            services.AddSingleton<IRuntimeVersionManager, RuntimeVersionManager>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IDispatcherService, DispatcherService>();
            services.AddSingleton<IDialogService, WpfDialogService>();
            services.AddSingleton<IRuntimeStateManager, RuntimeStateManager>();
            services.AddSingleton<IRuntimeStateBridge, RuntimeStateBridge>();
            services.AddSingleton<IAppUpdaterService, AppUpdaterService>();
            services.AddSingleton<IClipboardService, WpfClipboardService>();
            services.AddSingleton<IRuntimeLocator, RuntimeLocator>();
            services.AddSingleton<IRuntimeInstaller, RuntimeInstaller>();
            services.AddSingleton<IDatabaseCredentialsManager, DatabaseCredentialsManager>();
            services.AddSingleton<IRuntimeConfigGenerator, RuntimeConfigGenerator>();
            services.AddSingleton<IWebsiteOrchestrator, WebsiteOrchestrator>();
            services.AddSingleton<Zenvix.Modules.Services.ILegacyDatabaseImporter, Zenvix.Modules.Services.LegacyDatabaseImporter>();

            // Deployment & containers
            services.AddSingleton<Zenvix.Modules.Deployment.IDeploymentStore, Zenvix.Modules.Deployment.DeploymentStore>();
            services.AddSingleton<Zenvix.Modules.Deployment.ISshDeployService, Zenvix.Modules.Deployment.SshDeployService>();
            services.AddSingleton<Zenvix.Modules.Deployment.IGitDeployService, Zenvix.Modules.Deployment.GitDeployService>();
            services.AddSingleton<Zenvix.Modules.Deployment.IShareTunnelService, Zenvix.Modules.Deployment.ShareTunnelService>();
            services.AddSingleton<Zenvix.Modules.Containers.IContainerEngine, Zenvix.Modules.Containers.ContainerEngine>();
            services.AddSingleton<Zenvix.Modules.Toolchain.IToolchainManager, Zenvix.Modules.Toolchain.ToolchainManager>();
            services.AddSingleton<Zenvix.Modules.Toolchain.IProjectScaffolder, Zenvix.Modules.Toolchain.ProjectScaffolder>();
            services.AddSingleton<Zenvix.Modules.Toolchain.IUpdateService, Zenvix.Modules.Toolchain.UpdateService>();
            services.AddSingleton<UpdatesViewModel>();

            // ViewModels
            services.AddSingleton<Zenvix.ViewModels.Deployment.DeploymentViewModel>();
            services.AddSingleton<Zenvix.ViewModels.Containers.ContainersViewModel>();
            services.AddSingleton<LogsViewModel>();
            services.AddSingleton<DatabasesViewModel>();
            services.AddSingleton<ServicesViewModel>();
            services.AddSingleton<WebsitesViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>();
        }
    }
}
