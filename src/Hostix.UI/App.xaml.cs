using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Hostix.Core.Services;
using Hostix.Runtime.Services;
using Hostix.ViewModels;
using Hostix.ViewModels.Services;
using Hostix.Modules.Services;
using Hostix.UI.Services;

namespace Hostix.UI
{
    public partial class App : System.Windows.Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            ServiceProvider = serviceCollection.BuildServiceProvider();

            // Initialize Services
            var stateManager = ServiceProvider.GetRequiredService<IRuntimeStateManager>();
            stateManager.AddEvent("Hostix Professional Workstation Started.");

            // Launch Main Window
            var mainWindow = new MainWindow();
            mainWindow.DataContext = ServiceProvider.GetRequiredService<MainViewModel>();
            mainWindow.Show();
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
            services.AddSingleton<IClipboardService, WpfClipboardService>();
            services.AddSingleton<IRuntimeLocator, RuntimeLocator>();
            services.AddSingleton<IRuntimeInstaller, RuntimeInstaller>();
            services.AddSingleton<IDatabaseCredentialsManager, DatabaseCredentialsManager>();
            services.AddSingleton<IRuntimeConfigGenerator, RuntimeConfigGenerator>();
            services.AddSingleton<IWebsiteOrchestrator, WebsiteOrchestrator>();

            // ViewModels
            services.AddSingleton<DatabasesViewModel>();
            services.AddSingleton<ServicesViewModel>();
            services.AddSingleton<WebsitesViewModel>();
            services.AddSingleton<MainViewModel>();
        }
    }
}
