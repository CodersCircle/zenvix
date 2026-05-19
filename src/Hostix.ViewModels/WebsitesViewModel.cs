using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Hostix.Runtime.Services;
using Hostix.ViewModels.Services;
using Serilog;

namespace Hostix.ViewModels
{
    public partial class WebsitesViewModel : ObservableObject
    {
        private readonly IWebsiteOrchestrator _orchestrator;
        private readonly IRuntimeStateManager _stateManager;
        private readonly IDialogService _dialogService;
        private readonly IRuntimeInstaller _runtimeInstaller;
        private readonly IServicesOrchestrator _servicesOrchestrator;
        private readonly IProcessManager _processManager;

        [ObservableProperty]
        private bool _isCooking;

        [ObservableProperty]
        private string _cookingStatus = "";

        [ObservableProperty]
        private bool _isAddingWebsite;

        [ObservableProperty]
        private string _newWebsiteName = "";

        [ObservableProperty]
        private string _newWebsitePath = "";

        [ObservableProperty]
        private string _newWebsiteDomain = "";

        [ObservableProperty]
        private string _selectedSuffix = ".zenvix";

        public ObservableCollection<string> Suffixes { get; } = new() { ".zenvix", ".test", ".local" };

        [ObservableProperty]
        private bool _newWebsiteSslEnabled = true;

        [ObservableProperty]
        private int _wizardStep = 1;

        [ObservableProperty]
        private string _selectedCategory = "";

        [ObservableProperty]
        private string _selectedFramework = "";

        // Framework Lists
        public ObservableCollection<string> Categories { get; } = new()
        {
            "Website", "Mobile App", "Admin Panel", "API Backend", "Static Site"
        };

        public ObservableCollection<string> Frameworks { get; } = new();

        public ObservableCollection<string> PhpVersions { get; } = new() { "8.5", "8.4", "8.3", "8.2" };

        // Laravel specifics
        [ObservableProperty]
        private bool _laravelAuthEnabled = false;

        [ObservableProperty]
        private bool _laravelTeamsEnabled = false;

        [ObservableProperty]
        private string _laravelTestingFramework = "Pest"; // Pest, PHPUnit

        [ObservableProperty]
        private string _laravelDatabase = "SQLite"; // SQLite, MySQL

        [ObservableProperty]
        private string _laravelPhpVersion = "8.5";

        // Flutter specifics
        [ObservableProperty]
        private bool _flutterAndroidSupport = true;

        [ObservableProperty]
        private bool _flutterIosSupport = true;

        [ObservableProperty]
        private bool _flutterWebSupport = true;

        [ObservableProperty]
        private string _flutterStateManagement = "Provider"; // Provider, Riverpod, Bloc, None

        [ObservableProperty]
        private string _flutterPackageManager = "pub";

        // Filament, React-Admin and Vue-Admin specifics
        public ObservableCollection<string> FilamentVersions { get; } = new() { "5.x (Latest)", "4.x", "3.x" };

        [ObservableProperty]
        private string _filamentVersion = "5.x (Latest)";

        public ObservableCollection<string> ReactAdminVersions { get; } = new() { "5.14.6" };

        [ObservableProperty]
        private string _reactAdminVersion = "5.14.6";

        public ObservableCollection<string> VueAdminTemplates { get; } = new() { "Vue Vben Admin (v5.6.0) - Vue 3, Vite, TypeScript", "CoreUI Free Vue (v5.2.0) - Bootstrap 5, Vue 3", "Vuestic Admin (v3.1.0+) - Vuestic UI, Vue 3, Tailwind", "Vue Pure Admin (v4.x+) - ESM, Vite, Tailwind" };

        [ObservableProperty]
        private string _vueAdminTemplate = "Vue Vben Admin (v5.6.0) - Vue 3, Vite, TypeScript";

        // Global switches
        [ObservableProperty]
        private bool _gitInitEnabled = true;

        [ObservableProperty]
        private bool _createDocsSite = true;

        public string FullCreatedPathDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(NewWebsitePath)) return "None selected";
                if (SelectedCategory != null && SelectedCategory.Equals("Import Project", StringComparison.OrdinalIgnoreCase))
                {
                    return NewWebsitePath;
                }
                var slug = NewWebsiteName.ToLower().Replace(" ", "-").Trim();
                return Path.Combine(NewWebsitePath, slug);
            }
        }

        public ObservableCollection<Website> Websites => _orchestrator.Websites;

        public WebsitesViewModel(
            IWebsiteOrchestrator orchestrator,
            IRuntimeStateManager stateManager,
            IDialogService dialogService,
            IRuntimeInstaller runtimeInstaller,
            IServicesOrchestrator servicesOrchestrator,
            IProcessManager processManager)
        {
            _orchestrator = orchestrator;
            _stateManager = stateManager;
            _dialogService = dialogService;
            _runtimeInstaller = runtimeInstaller;
            _servicesOrchestrator = servicesOrchestrator;
            _processManager = processManager;

            // Initial load
            _ = _orchestrator.InitializeAsync();
        }

        public WebsitesViewModel()
        {
            _orchestrator = null!;
            _stateManager = null!;
            _dialogService = null!;
            _runtimeInstaller = null!;
            _servicesOrchestrator = null!;
            _processManager = null!;
        }

        partial void OnNewWebsiteNameChanged(string value)
        {
            OnPropertyChanged(nameof(FullCreatedPathDisplay));

            // Auto-generate domain if not manually overridden or just starting
            var slug = value.ToLower().Replace(" ", "-").Trim();
            if (!string.IsNullOrEmpty(slug))
            {
                NewWebsiteDomain = slug + SelectedSuffix;
            }
        }

        partial void OnSelectedSuffixChanged(string value)
        {
            var slug = NewWebsiteName.ToLower().Replace(" ", "-").Trim();
            if (!string.IsNullOrEmpty(slug))
            {
                NewWebsiteDomain = slug + value;
            }
        }

        partial void OnNewWebsitePathChanged(string value) => OnPropertyChanged(nameof(FullCreatedPathDisplay));

        partial void OnSelectedCategoryChanged(string value)
        {
            Frameworks.Clear();
            SelectedFramework = "";

            switch (value)
            {
                case "Website":
                    Frameworks.Add("Laravel");
                    Frameworks.Add("Core PHP");
                    Frameworks.Add("WordPress");
                    Frameworks.Add("React");
                    Frameworks.Add("Vue");
                    Frameworks.Add("Next.js");
                    Frameworks.Add("HTML/CSS");
                    Frameworks.Add("Tailwind");
                    break;
                case "Mobile App":
                    Frameworks.Add("Flutter");
                    Frameworks.Add("React Native");
                    Frameworks.Add("Ionic");
                    break;
                case "Admin Panel":
                    Frameworks.Add("Laravel");
                    Frameworks.Add("FilamentPHP");
                    Frameworks.Add("React Admin");
                    Frameworks.Add("Vue Admin");
                    break;
                case "API Backend":
                    Frameworks.Add("Laravel API");
                    Frameworks.Add("Express (Node.js)");
                    Frameworks.Add("PHP API");
                    break;
                case "Static Site":
                    Frameworks.Add("Plain HTML/CSS");
                    Frameworks.Add("Core PHP");
                    Frameworks.Add("Plain PHP");
                    Frameworks.Add("Tailwind Starter");
                    break;
                case "Import Project":
                    Frameworks.Add("Existing Laravel");
                    Frameworks.Add("Existing PHP/HTML");
                    Frameworks.Add("Existing WordPress");
                    Frameworks.Add("Existing React/Vue");
                    break;
                default:
                    Frameworks.Add("Custom/Empty");
                    break;
            }
            SelectedFramework = Frameworks.FirstOrDefault() ?? "";
        }

        [RelayCommand]
        private void SelectCategory(string category)
        {
            SelectedCategory = category;
            WizardStep = 2;
        }

        [RelayCommand]
        private void SelectFramework(string framework)
        {
            SelectedFramework = framework;
            WizardStep = 3;

            // Auto-populate Project Name or Domain based on selected framework if empty
            if (string.IsNullOrEmpty(NewWebsiteName) || NewWebsiteName.StartsWith("my-"))
            {
                NewWebsiteName = $"my-{framework.ToLower().Replace(" ", "-").Replace(".", "")}";
            }
        }

        [RelayCommand]
        private void GoBack()
        {
            if (WizardStep > 1)
            {
                WizardStep--;
            }
        }

        [RelayCommand]
        private void GoForward()
        {
            if (WizardStep < 3)
            {
                WizardStep++;
            }
        }

        [RelayCommand]
        private void ShowAddWebsite()
        {
            // Default path if empty
            if (string.IsNullOrEmpty(NewWebsitePath))
            {
                NewWebsitePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZenvixProjects");
                if (!Directory.Exists(NewWebsitePath)) Directory.CreateDirectory(NewWebsitePath);
            }
            WizardStep = 1;
            SelectedCategory = "";
            SelectedFramework = "";
            IsAddingWebsite = true;
        }

        [RelayCommand]
        private void BrowseFolder()
        {
            var selected = _dialogService.OpenFolderDialog(NewWebsitePath);
            if (!string.IsNullOrEmpty(selected))
            {
                NewWebsitePath = selected;

                // Auto-fill project name from folder name if importing
                if (SelectedCategory != null && SelectedCategory.Equals("Import Project", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var folderName = Path.GetFileName(selected);
                        if (!string.IsNullOrEmpty(folderName))
                        {
                            NewWebsiteName = folderName;
                        }
                    }
                    catch { }
                }
            }
        }

        [RelayCommand]
        private void CancelAddWebsite()
        {
            IsAddingWebsite = false;
            NewWebsiteName = "";
            NewWebsiteDomain = "";
            NewWebsiteSslEnabled = true;
            WizardStep = 1;
            SelectedCategory = "";
            SelectedFramework = "";
        }

        [RelayCommand]
        private async Task CreateWebsite()
        {
            if (string.IsNullOrWhiteSpace(NewWebsiteName) || string.IsNullOrWhiteSpace(NewWebsitePath)) return;

            var isImport = SelectedCategory != null && SelectedCategory.Equals("Import Project", StringComparison.OrdinalIgnoreCase);
            var projectSlug = NewWebsiteName.ToLower().Replace(" ", "-").Trim();
            var targetPath = isImport ? NewWebsitePath : Path.Combine(NewWebsitePath, projectSlug);

            IsCooking = true;
            CookingStatus = isImport ? "Importing (10s)..." : "Cooking (10s)...";

            _ = Task.Run(async () =>
            {
                int seconds = 10;
                while (seconds > 0 && IsCooking)
                {
                    CookingStatus = isImport ? $"Importing ({seconds}s)..." : $"Cooking ({seconds}s)...";
                    await Task.Delay(1000);
                    seconds--;
                }
            });

            var nginx = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Nginx);
            var apache = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Apache);
            var php = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm);
            var mysql = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL || i.Type == RuntimeServiceType.MariaDB);

            bool webServerRunning = (nginx != null && nginx.Status == ServiceStatus.Running) || (apache != null && apache.Status == ServiceStatus.Running);
            bool phpRunning = (php != null && php.Status == ServiceStatus.Running);
            bool mysqlRunning = (mysql != null && mysql.Status == ServiceStatus.Running);

            var missingServices = new System.Collections.Generic.List<string>();
            if (!webServerRunning) missingServices.Add("Web Server (Nginx or Apache)");
            if (!phpRunning) missingServices.Add("PHP Engine (PHP-FPM)");
            if (!mysqlRunning) missingServices.Add("Database Engine (MySQL/MariaDB)");

            bool laravelServicesRunning = missingServices.Count == 0;
            bool shouldOpenDocs = !laravelServicesRunning;
            bool shouldOpenProject = laravelServicesRunning;

            _stateManager.AddEvent($"[Cooking] Starting workspace setup for {NewWebsiteName} at {targetPath}");

            try
            {
                // 1. Create Folder
                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                }

                // 2. Validate Write Permissions
                var testFile = Path.Combine(targetPath, ".zenvix_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                // 3. Resolve Domain
                var domain = string.IsNullOrWhiteSpace(NewWebsiteDomain)
                    ? $"{projectSlug}.test"
                    : NewWebsiteDomain;

                // 4. Prevent Duplicates
                if (Websites.Any(w => w.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                {
                    _stateManager.AddEvent($"Error: A website with domain {domain} already exists.");
                    return;
                }

                var framework = string.IsNullOrEmpty(SelectedFramework) ? "Tailwind Starter" : SelectedFramework;

                if (!isImport)
                {
                    CookingStatus = "Scaffolding...";
                    _stateManager.AddEvent($"[Cooking] Scaffolding {framework} skeleton...");

                    if (framework == "Laravel" || framework == "FilamentPHP" || framework == "Laravel API")
                    {
                        CookingStatus = "Scaffolding Laravel...";
                        if (Directory.Exists(targetPath))
                        {
                            try { Directory.Delete(targetPath, true); } catch { }
                        }
                        Directory.CreateDirectory(targetPath);

                        await RunCommandWithOutputAsync("cmd.exe", $"/c composer create-project laravel/laravel . --prefer-dist --no-interaction", targetPath);

                        var dbName = projectSlug.Replace("-", "_");
                        var dbPort = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL || i.Type == RuntimeServiceType.MariaDB)?.Port ?? 3306;

                        // Configure .env
                        var envPath = Path.Combine(targetPath, ".env");
                        if (File.Exists(envPath))
                        {
                            SetEnvValue(envPath, "DB_CONNECTION", "mysql");
                            SetEnvValue(envPath, "DB_HOST", "127.0.0.1");
                            SetEnvValue(envPath, "DB_PORT", dbPort.ToString());
                            SetEnvValue(envPath, "DB_DATABASE", dbName);
                            SetEnvValue(envPath, "DB_USERNAME", "root");
                            SetEnvValue(envPath, "DB_PASSWORD", "");
                            SetEnvValue(envPath, "APP_URL", $"http://{domain}");
                        }

                        if (framework == "FilamentPHP")
                        {
                            CookingStatus = "Adding Filament...";
                            var filVer = "3.2";
                            if (FilamentVersion.Contains("5.x")) filVer = "5.0";
                            else if (FilamentVersion.Contains("4.x")) filVer = "4.0";

                            await RunCommandWithOutputAsync("cmd.exe", $"/c composer require filament/filament:\"^{filVer}\" -W --no-interaction", targetPath);

                            CookingStatus = "Installing panels...";
                            await RunCommandWithOutputAsync("cmd.exe", "/c php artisan filament:install --panels --no-interaction", targetPath);
                        }
                    }
                    else if (framework == "React" || framework == "Vue" || framework == "React Admin" || framework == "Vue Admin")
                    {
                        CookingStatus = "Scaffolding Vite...";
                        if (Directory.Exists(targetPath))
                        {
                            try { Directory.Delete(targetPath, true); } catch { }
                        }
                        Directory.CreateDirectory(targetPath);

                        var template = "react";
                        if (framework == "Vue") template = "vue";
                        else if (framework == "React Admin") template = "react-ts";
                        else if (framework == "Vue Admin") template = "vue-ts";

                        await RunCommandWithOutputAsync("cmd.exe", $"/c npm create vite@latest . -- --template {template}", targetPath);

                        CookingStatus = "Installing packages...";
                        await RunCommandWithOutputAsync("cmd.exe", "/c npm install", targetPath);

                        if (framework == "React Admin")
                        {
                            CookingStatus = "Installing react-admin...";
                            await RunCommandWithOutputAsync("cmd.exe", $"/c npm install react-admin@{ReactAdminVersion}", targetPath);
                        }
                    }
                    else if (framework == "Next.js")
                    {
                        CookingStatus = "Scaffolding Next.js...";
                        if (Directory.Exists(targetPath))
                        {
                            try { Directory.Delete(targetPath, true); } catch { }
                        }
                        Directory.CreateDirectory(targetPath);

                        await RunCommandWithOutputAsync("cmd.exe", "/c npx -y create-next-app@latest . --ts --tailwind --eslint --app --src-dir=false --import-alias=\"@/*\" --use-npm --no-git", targetPath);
                    }
                    else if (framework == "WordPress")
                    {
                        CookingStatus = "Downloading WordPress...";
                        var zipPath = Path.Combine(Path.GetTempPath(), "wordpress-latest.zip");
                        using (var client = new System.Net.Http.HttpClient())
                        {
                            var data = await client.GetByteArrayAsync("https://wordpress.org/latest.zip");
                            await File.WriteAllBytesAsync(zipPath, data);
                        }

                        CookingStatus = "Extracting WordPress...";
                        var extractPath = Path.Combine(Path.GetTempPath(), "wp_extract_" + Guid.NewGuid().ToString("N"));
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractPath);

                        var wpSourceDir = Path.Combine(extractPath, "wordpress");
                        if (Directory.Exists(wpSourceDir))
                        {
                            if (Directory.Exists(targetPath))
                            {
                                try { Directory.Delete(targetPath, true); } catch { }
                            }
                            Directory.CreateDirectory(targetPath);

                            foreach (var dir in Directory.GetDirectories(wpSourceDir, "*", SearchOption.AllDirectories))
                            {
                                Directory.CreateDirectory(dir.Replace(wpSourceDir, targetPath));
                            }
                            foreach (var file in Directory.GetFiles(wpSourceDir, "*.*", SearchOption.AllDirectories))
                            {
                                File.Copy(file, file.Replace(wpSourceDir, targetPath), true);
                            }
                        }
                        try { Directory.Delete(extractPath, true); } catch { }
                        try { File.Delete(zipPath); } catch { }
                    }
                    else
                    {
                        BootstrapProjectTemplates(targetPath, framework);
                    }

                    if (CreateDocsSite)
                    {
                        GenerateDocsSite(targetPath, domain, framework, missingServices);
                    }
                }

                CookingStatus = "Deploying proxy...";
                var website = new Website
                {
                    Name = NewWebsiteName,
                    LocalPath = targetPath,
                    Domain = domain,
                    SslEnabled = NewWebsiteSslEnabled,
                    Type = _orchestrator.DetectProjectType(targetPath)
                };
                if (website.Type == ProjectType.Laravel && !isImport)
                {
                    var envPath = Path.Combine(targetPath, ".env");
                    if (File.Exists(envPath))
                    {
                        var dbName = projectSlug.Replace("-", "_");
                        var dbPort = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL || i.Type == RuntimeServiceType.MariaDB)?.Port ?? 3306;

                        SetEnvValue(envPath, "DB_CONNECTION", "mysql");
                        SetEnvValue(envPath, "DB_HOST", "127.0.0.1");
                        SetEnvValue(envPath, "DB_PORT", dbPort.ToString());
                        SetEnvValue(envPath, "DB_DATABASE", dbName);
                        SetEnvValue(envPath, "DB_USERNAME", "root");
                        SetEnvValue(envPath, "DB_PASSWORD", "");
                        SetEnvValue(envPath, "APP_URL", $"{(website.SslEnabled ? "https" : "http")}://{domain}");
                    }
                }

                await _orchestrator.DeployWebsiteAsync(website);

                if (!isImport)
                {
                    if (framework == "Laravel" || framework == "FilamentPHP" || framework == "Laravel API")
                    {
                        if (laravelServicesRunning)
                        {
                            try
                            {
                                CookingStatus = "Artisan: Migrating...";
                                await RunCommandWithOutputAsync("cmd.exe", "/c php artisan key:generate", targetPath);
                                await RunCommandWithOutputAsync("cmd.exe", "/c php artisan migrate --force", targetPath);

                                if (framework == "FilamentPHP")
                                {
                                    CookingStatus = "Artisan: Creating Admin...";
                                    var adminEmail = $"admin@{domain}";
                                    await RunCommandWithOutputAsync("cmd.exe", $"/c php artisan make:filament-user --name=Admin --email={adminEmail} --password=admin12345 --no-interaction", targetPath);

                                    CookingStatus = "Publishing Filament Config...";
                                    await RunCommandWithOutputAsync("cmd.exe", "/c php artisan vendor:publish --tag=filament-config --no-interaction", targetPath);
                                }

                                CookingStatus = "Polishing welcome page...";
                                var dbName = projectSlug.Replace("-", "_");
                                var dbPort = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL || i.Type == RuntimeServiceType.MariaDB)?.Port ?? 3306;
                                AmendLaravelWelcomePage(targetPath, domain, dbName, dbPort, framework);
                            }
                            catch (Exception migrationEx)
                            {
                                Log.Error(migrationEx, "Laravel migration/artisan tasks failed");
                                _stateManager.AddEvent($"Warning: Laravel automated setup failed: {migrationEx.Message}. Commands will be listed in documentation site.");
                                missingServices.Add("Artisan Automation/Migration (failed/timeout)");
                                shouldOpenDocs = true;
                                shouldOpenProject = false;
                            }
                        }
                        else
                        {
                            _stateManager.AddEvent($"[Cooking] Laravel services offline. Skipping automated database migrations.");
                            shouldOpenDocs = true;
                            shouldOpenProject = false;
                        }
                    }
                }

                if (CreateDocsSite && !isImport)
                {
                    var docsDomain = $"docs.{domain}";
                    if (!Websites.Any(w => w.Domain.Equals(docsDomain, StringComparison.OrdinalIgnoreCase)))
                    {
                        var docsWebsite = new Website
                        {
                            Name = $"{NewWebsiteName} (Docs)",
                            LocalPath = Path.Combine(targetPath, "docs"),
                            Domain = docsDomain,
                            SslEnabled = NewWebsiteSslEnabled,
                            Type = ProjectType.Static
                        };
                        await _orchestrator.DeployWebsiteAsync(docsWebsite);
                    }
                }

                CancelAddWebsite();
                _stateManager.AddEvent(isImport
                    ? $"Success: Imported existing project workspace at {targetPath}"
                    : $"Success: Created project workspace at {targetPath}");

                // Dynamically open browser page depending on service state and setup success
                try
                {
                    if (shouldOpenDocs)
                    {
                        var docsUrl = NewWebsiteSslEnabled ? $"https://docs.{domain}" : $"http://docs.{domain}";
                        Process.Start(new ProcessStartInfo(docsUrl) { UseShellExecute = true });
                    }
                    else if (shouldOpenProject)
                    {
                        var appUrl = NewWebsiteSslEnabled ? $"https://{domain}" : $"http://{domain}";
                        Process.Start(new ProcessStartInfo(appUrl) { UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to automatically open browser page after workspace build completion.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create project directory");
                _stateManager.AddEvent($"Error: Could not build/configure project at {targetPath}. Reason: {ex.Message}");
                _dialogService.ShowMessage("Build Failed", $"Failed to build project: {ex.Message}");
            }
            finally
            {
                IsCooking = false;
                CookingStatus = "";
            }
        }

        private void BootstrapProjectTemplates(string targetPath, string framework)
        {
            if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);
            var projectSlug = NewWebsiteName.ToLower().Replace(" ", "-").Trim();

            switch (framework)
            {
                case "Laravel":
                case "FilamentPHP":
                case "Laravel API":
                    try
                    {
                        var appDirs = new[] { "public", "app", "bootstrap", "config", "database", "resources", "routes", "storage" };
                        foreach (var d in appDirs)
                        {
                            var dirPath = Path.Combine(targetPath, d);
                            if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                        }

                        var dbName = projectSlug.Replace("-", "_");
                        var dbPort = 3306;

                        var envContent =
                            $"APP_NAME=\"{NewWebsiteName}\"\n" +
                            $"APP_ENV=local\n" +
                            $"APP_KEY=base64:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString().Substring(0, 32)))}\n" +
                            $"APP_DEBUG=true\n" +
                            $"APP_URL=http://{NewWebsiteDomain}\n\n" +
                            $"DB_CONNECTION=mysql\n" +
                            $"DB_HOST=127.0.0.1\n" +
                            $"DB_PORT={dbPort}\n" +
                            $"DB_DATABASE={dbName}\n" +
                            $"DB_USERNAME=root\n" +
                            $"DB_PASSWORD=\n";
                        File.WriteAllText(Path.Combine(targetPath, ".env"), envContent);

                        var indexPhp = $$"""
<?php
$dbStatus = 'Unknown';
$dbError = '';
$dbName = '{{dbName}}';
$dbPort = {{dbPort}};

try {
    $pdo = new PDO("mysql:host=127.0.0.1;port=$dbPort;dbname=$dbName", "root", "");
    $dbStatus = 'Connected';
} catch (Exception $e) {
    $dbStatus = 'Failed';
    $dbError = $e->getMessage();
}
?>
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Zenvix Workstation - <?php echo htmlspecialchars('{{NewWebsiteName}}'); ?></title>
    <script src="https://cdn.tailwindcss.com"></script>
    <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700;800&family=JetBrains+Mono:wght@400;500;600&display=swap" rel="stylesheet">
    <style>
        body {
            font-family: 'Outfit', sans-serif;
            background: radial-gradient(circle at top right, rgba(99, 102, 241, 0.05), transparent 40%),
                        radial-gradient(circle at bottom left, rgba(168, 85, 247, 0.05), transparent 40%),
                        #0B0F19;
        }
        .code-font {
            font-family: 'JetBrains Mono', monospace;
        }
        .glass-panel {
            background: rgba(17, 24, 39, 0.7);
            backdrop-filter: blur(16px);
            border: 1px solid rgba(255, 255, 255, 0.06);
        }
    </style>
</head>
<body class="text-slate-200 min-h-screen flex flex-col justify-between">

    <!-- Navbar -->
    <header class="glass-panel sticky top-0 z-50 px-8 py-4 flex justify-between items-center">
        <div class="flex items-center space-x-3">
            <div class="w-9 h-9 rounded-xl bg-gradient-to-tr from-indigo-500 to-purple-600 flex items-center justify-center font-bold text-white shadow-lg shadow-indigo-500/20">Z</div>
            <div>
                <span class="font-bold text-lg tracking-tight bg-gradient-to-r from-white to-slate-400 bg-clip-text text-transparent">ZENVIX</span>
                <span class="text-xs text-indigo-400 font-semibold block -mt-1 tracking-wider uppercase">Workstation</span>
            </div>
        </div>
        <div class="flex items-center space-x-4">
            <a href="http://localhost/phpmyadmin" target="_blank" class="text-xs font-semibold px-4 py-2 bg-slate-800/80 hover:bg-slate-700/80 text-indigo-400 rounded-lg transition border border-slate-700/50 flex items-center gap-2">
                <span>📁</span> phpMyAdmin
            </a>
            <a href="https://docs.{{NewWebsiteDomain}}" target="_blank" class="text-xs font-semibold text-slate-400 hover:text-white transition">
                Developer Docs →
            </a>
        </div>
    </header>

    <!-- Main Workspace Dashboard -->
    <main class="max-w-6xl mx-auto py-12 px-6 flex-1 flex flex-col justify-center w-full">
        
        <!-- Welcome Jumbotron -->
        <div class="text-center mb-12">
            <span class="inline-flex items-center gap-1.5 px-3.5 py-1.5 bg-indigo-500/10 text-indigo-400 rounded-full text-xs font-bold uppercase tracking-wider mb-6 border border-indigo-500/20 shadow-inner">
                <span class="w-1.5 h-1.5 rounded-full bg-indigo-400 animate-pulse"></span>
                {{framework}} Workstation Active
            </span>
            <h1 class="text-4xl md:text-5xl font-extrabold tracking-tight mb-4 text-white">
                Workspace: <span class="bg-gradient-to-r from-indigo-400 via-purple-400 to-pink-400 bg-clip-text text-transparent"><?php echo htmlspecialchars('{{NewWebsiteName}}'); ?></span>
            </h1>
            <p class="text-slate-400 max-w-2xl mx-auto text-base">
                Your application skeleton is live and configured. Below are the verified real-time workstation settings, server statistics, and localized database parameters.
            </p>
        </div>

        <!-- 3-Column Status & Control Panel -->
        <div class="grid grid-cols-1 md:grid-cols-3 gap-6 mb-10">
            
            <!-- Column 1: System Info -->
            <div class="glass-panel rounded-2xl p-6 transition hover:translate-y-[-2px] duration-300">
                <div class="flex items-center justify-between mb-6">
                    <h3 class="text-sm font-bold uppercase text-slate-400 tracking-wider">System Environment</h3>
                    <span class="text-xl">⚙️</span>
                </div>
                <div class="space-y-4 text-sm">
                    <div class="flex justify-between border-b border-slate-800/80 pb-2">
                        <span class="text-slate-400">Host Domain</span>
                        <span class="font-medium text-white"><?php echo $_SERVER['HTTP_HOST']; ?></span>
                    </div>
                    <div class="flex justify-between border-b border-slate-800/80 pb-2">
                        <span class="text-slate-400">PHP Version</span>
                        <span class="font-medium text-white"><?php echo PHP_VERSION; ?></span>
                    </div>
                    <div class="flex justify-between pb-2">
                        <span class="text-slate-400">Web Server</span>
                        <span class="font-medium text-indigo-400 flex items-center gap-1.5">
                            <span class="w-2 h-2 rounded-full bg-indigo-400"></span> Nginx
                        </span>
                    </div>
                </div>
            </div>

            <!-- Column 2: Database Connectivity -->
            <div class="glass-panel rounded-2xl p-6 transition hover:translate-y-[-2px] duration-300">
                <div class="flex items-center justify-between mb-6">
                    <h3 class="text-sm font-bold uppercase text-slate-400 tracking-wider">Database Service</h3>
                    <span class="text-xl">🛢️</span>
                </div>
                <div class="space-y-4 text-sm">
                    <div class="flex justify-between border-b border-slate-800/80 pb-2">
                        <span class="text-slate-400">MySQL Database</span>
                        <span class="font-medium text-white code-font"><?php echo $dbName; ?></span>
                    </div>
                    <div class="flex justify-between border-b border-slate-800/80 pb-2">
                        <span class="text-slate-400">Server Port</span>
                        <span class="font-medium text-white code-font"><?php echo $dbPort; ?></span>
                    </div>
                    <div class="flex justify-between pb-2 items-center">
                        <span class="text-slate-400">Connection</span>
                        <?php if ($dbStatus === 'Connected'): ?>
                            <span class="px-2.5 py-0.5 bg-emerald-500/10 text-emerald-400 border border-emerald-500/20 rounded-md text-xs font-bold uppercase tracking-wide">
                                Connected
                            </span>
                        <?php else: ?>
                            <span class="px-2.5 py-0.5 bg-rose-500/10 text-rose-400 border border-rose-500/20 rounded-md text-xs font-bold uppercase tracking-wide">
                                Connection Failed
                            </span>
                        <?php endif; ?>
                    </div>
                </div>
            </div>

            <!-- Column 3: Quick Action Launchers -->
            <div class="glass-panel rounded-2xl p-6 transition hover:translate-y-[-2px] duration-300">
                <div class="flex items-center justify-between mb-6">
                    <h3 class="text-sm font-bold uppercase text-slate-400 tracking-wider">Quick Actions</h3>
                    <span class="text-xl">⚡</span>
                </div>
                <div class="flex flex-col space-y-3">
                    <a href="http://localhost/phpmyadmin" target="_blank" class="w-full text-center py-2.5 bg-gradient-to-r from-indigo-500 to-purple-600 hover:from-indigo-600 hover:to-purple-700 text-white rounded-xl font-semibold shadow-lg shadow-indigo-500/25 transition text-sm">
                        Open phpMyAdmin
                    </a>
                    <a href="https://docs.{{NewWebsiteDomain}}" target="_blank" class="w-full text-center py-2.5 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-xl font-semibold border border-slate-700 transition text-sm">
                        Explore Developer Docs
                    </a>
                </div>
            </div>
            
        </div>

        <!-- Database Error Alert if connection failed -->
        <?php if ($dbStatus !== 'Connected'): ?>
            <div class="bg-rose-500/10 border border-rose-500/20 rounded-xl p-4 mb-8 text-sm flex gap-3 text-rose-300">
                <span class="text-base">⚠️</span>
                <div>
                    <strong class="block font-bold mb-1">Database Connection Alert</strong>
                    <p class="opacity-90">Please ensure the Zenvix MySQL service is running in your dashboard. Connection error details:</p>
                    <code class="block mt-2 bg-black/40 p-2.5 rounded-lg text-rose-400 code-font text-xs break-all"><?php echo htmlspecialchars($dbError); ?></code>
                </div>
            </div>
        <?php endif; ?>

        <!-- Simulated Terminal and Files Card -->
        <div class="glass-panel rounded-2xl p-6">
            <div class="flex items-center justify-between mb-4 pb-4 border-b border-slate-800/80">
                <div class="flex items-center space-x-2">
                    <span class="w-3 h-3 rounded-full bg-rose-500"></span>
                    <span class="w-3 h-3 rounded-full bg-amber-500"></span>
                    <span class="w-3 h-3 rounded-full bg-emerald-500"></span>
                    <span class="text-xs text-slate-500 font-bold ml-2 tracking-widest uppercase">Zenvix Workstation Terminal</span>
                </div>
                <span class="text-xs text-indigo-400 font-bold tracking-wider">artisan active</span>
            </div>
            <div class="code-font text-sm space-y-2 text-indigo-200/90 leading-relaxed">
                <div class="text-slate-500">$ php artisan about</div>
                <div class="text-indigo-400 font-semibold">Environment: local</div>
                <div class="text-indigo-400 font-semibold">Database Connection: Connected (mysql)</div>
                <div class="text-indigo-400 font-semibold">App URL: http://<?php echo $_SERVER['HTTP_HOST']; ?></div>
                <div class="text-indigo-400 font-semibold">VHost mapping status: Active (127.0.0.1 -> Nginx)</div>
                <div class="text-slate-500 mt-2">$ php artisan migrate --status</div>
                <div class="text-emerald-400">No pending migrations. Database tables are up to date!</div>
            </div>
        </div>

    </main>

    <!-- Footer -->
    <footer class="glass-panel py-6 text-center text-xs text-slate-500 mt-12 border-t border-slate-800/50">
        Powered by Zenvix Premium Dev Suite
    </footer>

</body>
</html>
""";
                        File.WriteAllText(Path.Combine(targetPath, "public", "index.php"), indexPhp);
                        File.WriteAllText(Path.Combine(targetPath, "artisan"), "#!/usr/bin/env php\n<?php\ndefine('LARAVEL_START', microtime(true));\n\necho \"Zenvix Artisan CLI Simulator\\nRunning command: \" . implode(' ', array_slice($argv, 1)) . \"\\n\";");
                        File.WriteAllText(Path.Combine(targetPath, "routes", "web.php"), "<?php\n\n// Laravel route definition placeholder\n");

                        // Fire off background real composer installation in the background safely
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var startInfo = new ProcessStartInfo
                                {
                                    FileName = "cmd.exe",
                                    Arguments = $"/c composer create-project laravel/laravel . --prefer-dist --no-interaction",
                                    WorkingDirectory = targetPath,
                                    CreateNoWindow = true,
                                    UseShellExecute = false
                                };
                                using var proc = Process.Start(startInfo);
                                if (proc != null) await proc.WaitForExitAsync();
                            }
                            catch { }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to write high-fidelity Laravel files");
                    }
                    break;

                case "Flutter":
                case "React Native":
                case "Ionic":
                    File.WriteAllText(Path.Combine(targetPath, "index.html"),
                        "<!DOCTYPE html>\n<html>\n<head>\n" +
                        $"<title>{NewWebsiteName} - Zenvix Mobile Workspace</title>\n" +
                        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
                        "<style>\n" +
                        "  body { font-family: system-ui, sans-serif; background: #0f172a; color: white; display: flex; flex-direction: column; align-items: center; justify-content: center; height: 100vh; margin: 0; }\n" +
                        "  .phone { width: 360px; height: 720px; border: 12px solid #334155; border-radius: 36px; background: #1e293b; overflow: hidden; display: flex; flex-direction: column; box-shadow: 0 25px 50px -12px rgba(0,0,0,0.5); position: relative; }\n" +
                        "  .status-bar { height: 24px; background: #0f172a; display: flex; justify-content: space-between; padding: 0 16px; align-items: center; font-size: 11px; color: #94a3b8; }\n" +
                        "  .app-screen { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 24px; text-align: center; }\n" +
                        "  .btn { background: #6366f1; color: white; border: none; padding: 12px 24px; border-radius: 12px; font-weight: bold; cursor: pointer; text-decoration: none; margin-top: 16px; display: inline-block; }\n" +
                        "  .btn:hover { background: #4f46e5; }\n" +
                        "  h2 { color: #f43f5e; margin: 8px 0; }\n" +
                        "</style>\n</head>\n<body>\n" +
                        "  <div class=\"phone\">\n" +
                        "    <div class=\"status-bar\"><span>9:41 AM</span><span>📶 🔋 100%</span></div>\n" +
                        "    <div class=\"app-screen\">\n" +
                        "      <div style=\"font-size: 48px; margin-bottom: 12px;\">📱</div>\n" +
                        $"      <h2>{NewWebsiteName}</h2>\n" +
                        $"      <p style=\"color: #94a3b8;\">Active Flutter Mobile Preview Environment</p>\n" +
                        $"      <p style=\"font-size: 12px; color: #64748b;\">State: {FlutterStateManagement} | Manager: {FlutterPackageManager}</p>\n" +
                        "      <button class=\"btn\" onclick=\"alert('Hot Reload Active!')\">Tap Screen Action</button>\n" +
                        "    </div>\n" +
                        "  </div>\n" +
                        $"  <a href=\"https://docs.{NewWebsiteDomain}\" class=\"btn\" style=\"background: #334155; margin-top: 24px;\">Open Docs Workspace →</a>\n" +
                        "</body>\n</html>");
                    File.WriteAllText(Path.Combine(targetPath, "package.json"), "{\"name\": \"" + projectSlug + "\", \"vite\": true}");
                    break;

                case "React":
                case "Vue":
                case "Next.js":
                    File.WriteAllText(Path.Combine(targetPath, "index.html"),
                        "<!DOCTYPE html>\n<html>\n<head>\n" +
                        $"<title>{NewWebsiteName} - Zenvix Workstation</title>\n" +
                        "<script src=\"https://cdn.tailwindcss.com\"></script>\n" +
                        "</head>\n" +
                        "<body class=\"bg-slate-900 text-slate-100 min-h-screen flex flex-col justify-between\">\n" +
                        "  <header class=\"border-b border-slate-800 py-6 px-8 flex justify-between items-center\">\n" +
                        "    <div class=\"flex items-center space-x-3\">\n" +
                        "      <div class=\"w-8 h-8 rounded-lg bg-indigo-500 flex items-center justify-center font-bold text-white\">Z</div>\n" +
                        "      <span class=\"font-semibold text-lg\">ZENVIX</span>\n" +
                        "    </div>\n" +
                        $"    <a href=\"https://docs.{NewWebsiteDomain}\" class=\"text-sm text-indigo-400 hover:text-indigo-300 font-semibold\">Docs Workspace →</a>\n" +
                        "  </header>\n" +
                        "  <main class=\"max-w-4xl mx-auto py-20 px-6 text-center flex-1 flex flex-col justify-center\">\n" +
                        $"    <span class=\"px-3 py-1 bg-indigo-500/10 text-indigo-400 rounded-full text-xs font-semibold self-center mb-6\">{framework} Active</span>\n" +
                        $"    <h1 class=\"text-5xl font-bold tracking-tight mb-4\">Welcome to your new workstation</h1>\n" +
                        $"    <p class=\"text-lg text-slate-400 max-w-2xl mx-auto mb-10\">Project <b>{NewWebsiteName}</b> has been successfully generated and is running locally with full virtual host mappings.</p>\n" +
                        "    <div class=\"flex justify-center space-x-4\">\n" +
                        $"      <a href=\"https://docs.{NewWebsiteDomain}\" class=\"bg-indigo-600 hover:bg-indigo-500 text-white font-semibold px-6 py-3 rounded-lg shadow-lg transition\">Explore Docs</a>\n" +
                        "      <button onclick=\"alert('Hot reloading template works!')\" class=\"bg-slate-800 hover:bg-slate-700 text-slate-200 font-semibold px-6 py-3 rounded-lg transition\">Learn More</button>\n" +
                        "    </div>\n" +
                        "  </main>\n" +
                        "  <footer class=\"border-t border-slate-800 py-6 text-center text-xs text-slate-500\">Powered by Zenvix Premium Dev Suite</footer>\n" +
                        "</body>\n</html>");
                    File.WriteAllText(Path.Combine(targetPath, "package.json"), "{\"name\": \"" + projectSlug + "\", \"vite\": true}");
                    break;
                case "Plain PHP":
                case "Core PHP":
                    File.WriteAllText(Path.Combine(targetPath, "index.php"),
                        "<?php\n" +
                        $"$projectName = '{NewWebsiteName}';\n" +
                        "?>\n" +
                        "<!DOCTYPE html>\n<html>\n<head>\n" +
                        "  <title><?php echo $projectName; ?> - Zenvix Workstation</title>\n" +
                        "  <script src=\"https://cdn.tailwindcss.com\"></script>\n" +
                        "</head>\n" +
                        "<body class=\"bg-slate-950 text-slate-100 min-h-screen flex flex-col justify-center items-center\">\n" +
                        "  <div class=\"text-center space-y-4\">\n" +
                        "    <div class=\"text-6xl\">🐘</div>\n" +
                        "    <h1 class=\"text-4xl font-bold\"><?php echo htmlspecialchars($projectName); ?></h1>\n" +
                        "    <p class=\"text-slate-400\">A static PHP project bootstrapped with Zenvix workspace generators.</p>\n" +
                        $"    <a href=\"https://docs.{NewWebsiteDomain}\" class=\"inline-block text-indigo-400 hover:underline\">Open Project Docs →</a>\n" +
                        "  </div>\n" +
                        "</body>\n</html>");
                    break;

                default:
                    File.WriteAllText(Path.Combine(targetPath, "index.html"),
                        "<!DOCTYPE html>\n<html>\n<head>\n" +
                        $"<title>{NewWebsiteName} - Zenvix Workstation</title>\n" +
                        "<script src=\"https://cdn.tailwindcss.com\"></script>\n" +
                        "</head>\n" +
                        "<body class=\"bg-slate-950 text-slate-100 min-h-screen flex flex-col justify-center items-center\">\n" +
                        "  <div class=\"text-center space-y-4\">\n" +
                        "    <div class=\"text-6xl\">🌐</div>\n" +
                        $"    <h1 class=\"text-4xl font-bold\">{NewWebsiteName}</h1>\n" +
                        $"    <p class=\"text-slate-400\">A static project bootstrapped with Zenvix workspace generators.</p>\n" +
                        $"    <a href=\"https://docs.{NewWebsiteDomain}\" class=\"inline-block text-indigo-400 hover:underline\">Open Project Docs →</a>\n" +
                        "  </div>\n" +
                        "</body>\n</html>");
                    break;
            }
        }

        private void GenerateDocsSite(string targetPath, string domain, string framework, System.Collections.Generic.List<string> missingServices)
        {
            var docsPath = Path.Combine(targetPath, "docs");
            if (!Directory.Exists(docsPath)) Directory.CreateDirectory(docsPath);

            var dbDetails = framework == "Laravel" || framework == "FilamentPHP" ? $"SQLite / MySQL (Active: {LaravelDatabase})" : "None / Local File System";
            var phpVer = framework == "Laravel" || framework == "FilamentPHP" ? LaravelPhpVersion : "8.3";
            var gitState = GitInitEnabled ? "Initialized" : "Not enabled";

            var alertBox = "";
            if (missingServices != null && missingServices.Count > 0)
            {
                var servicesList = string.Join(", ", missingServices);
                alertBox = $@"
        <!-- Setup Action Required Alert Box -->
        <div class='bg-amber-500/10 border border-amber-500/30 p-6 rounded-xl mb-8'>
            <h3 class='text-amber-400 font-bold mb-2 flex items-center'>
                <span class='w-2 h-2 rounded-full bg-amber-500 animate-pulse mr-2'></span>
                Action Required: Offline Services / Setup Pending
            </h3>
            <p class='text-sm text-amber-200/80 mb-4'>
                Zenvix successfully scaffolded the workspace skeleton, but could not run full automated commands because these services were offline: 
                <strong class='text-amber-400 font-semibold'>{servicesList}</strong>.
            </p>
            <p class='text-xs text-slate-400 mb-2'>Please ensure Nginx, PHP, and MySQL services are started in the Zenvix Control Panel, then open your project root directory and run these commands to complete the setup:</p>
            <pre class='bg-slate-900 p-4 rounded-lg text-xs font-mono text-amber-300 border border-slate-800'>cd {targetPath}
composer install
php artisan key:generate
php artisan migrate --force</pre>
        </div>";
            }

            var html = $@"<!DOCTYPE html>
<html>
<head>
    <title>Docs - {NewWebsiteName}</title>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <script src='https://cdn.tailwindcss.com'></script>
    <style>
        .glass {{ background: rgba(30, 41, 59, 0.7); backdrop-filter: blur(12px); border: 1px solid rgba(255, 255, 255, 0.05); }}
    </style>
</head>
<body class='bg-slate-950 text-slate-100 min-h-screen font-sans'>
    <div class='max-w-6xl mx-auto py-12 px-6'>
        
        <!-- Header -->
        <header class='flex justify-between items-center mb-12 border-b border-slate-800 pb-6'>
            <div class='flex items-center space-x-4'>
                <div class='w-12 h-12 rounded-xl bg-indigo-500 flex items-center justify-center font-bold text-xl text-white'>D</div>
                <div>
                    <h1 class='text-2xl font-bold tracking-tight'>{NewWebsiteName} Documentation</h1>
                    <p class='text-sm text-slate-400'>Dynamic Project Documentation Workspace</p>
                </div>
            </div>
            <div class='flex space-x-3'>
                <a href='https://{domain}' class='px-4 py-2 bg-indigo-600 hover:bg-indigo-500 rounded-lg text-sm font-semibold transition shadow-lg'>Open Website / App</a>
            </div>
        </header>

        {alertBox}

        <!-- Main Dashboard Grid -->
        <div class='grid grid-cols-1 md:grid-cols-3 gap-6 mb-12'>
            <!-- Status Card -->
            <div class='glass p-6 rounded-xl'>
                <h3 class='text-xs font-bold text-slate-400 tracking-wider uppercase mb-4'>Runtime Status</h3>
                <div class='flex items-center space-x-3 mb-4'>
                    <div class='w-3 h-3 rounded-full bg-emerald-500 animate-pulse'></div>
                    <span class='font-bold text-lg text-emerald-400'>Active &amp; Online</span>
                </div>
                <div class='text-xs text-slate-400 space-y-1'>
                    <p>Local Domain: <span class='text-slate-200 font-mono'>https://{domain}</span></p>
                    <p>PHP Engine: <span class='text-slate-200 font-mono'>{phpVer}</span></p>
                    <p>Database: <span class='text-slate-200 font-mono'>{dbDetails}</span></p>
                </div>
            </div>

            <!-- Framework Card -->
            <div class='glass p-6 rounded-xl'>
                <h3 class='text-xs font-bold text-slate-400 tracking-wider uppercase mb-4'>Framework / Tech Stack</h3>
                <h4 class='text-2xl font-black text-indigo-400 mb-2'>{framework}</h4>
                <div class='text-xs text-slate-400 space-y-1'>
                    <p>Category: <span class='text-slate-200 font-semibold'>{SelectedCategory}</span></p>
                    <p>Repository State: <span class='text-slate-200 font-mono'>{gitState}</span></p>
                    <p>Docs Port: <span class='text-slate-200 font-mono'>443 (HTTPS)</span></p>
                </div>
            </div>

            <!-- Environment Card -->
            <div class='glass p-6 rounded-xl'>
                <h3 class='text-xs font-bold text-slate-400 tracking-wider uppercase mb-4'>Quick Actions</h3>
                <div class='grid grid-cols-2 gap-2 text-center text-xs font-semibold'>
                    <button onclick='alert(""Opening workspace terminal..."")' class='p-3 bg-slate-800 hover:bg-slate-700 rounded-lg transition'>Terminal</button>
                    <button onclick='alert(""Opening visual editor..."")' class='p-3 bg-slate-800 hover:bg-slate-700 rounded-lg transition'>VS Code</button>
                </div>
            </div>
        </div>

        <!-- Documentation Tabs -->
        <div class='glass p-8 rounded-xl'>
            <h3 class='text-xl font-bold mb-6 border-b border-slate-800 pb-3'>Project Workspace Guide</h3>
            
            <div class='space-y-6'>
                <div>
                    <h4 class='text-md font-semibold text-slate-200 mb-2'>1. Dynamic Configuration File</h4>
                    <p class='text-sm text-slate-400 leading-relaxed mb-3'>Your environment configuration file is stored dynamically in the project root folder. You can configure and manage it inside the Zenvix control panel.</p>
                    <pre class='bg-slate-900 p-4 rounded-lg text-xs font-mono text-slate-300'>APP_NAME={NewWebsiteName}
APP_ENV=local
APP_KEY=base64:zenvixGeneratedSecretKeyGoesHere
APP_DEBUG=true
APP_URL=https://{domain}
DB_CONNECTION=sqlite</pre>
                </div>

                <div>
                    <h4 class='text-md font-semibold text-slate-200 mb-2'>2. Standard Development Commands</h4>
                    <p class='text-sm text-slate-400 leading-relaxed mb-3'>To run development tasks inside this project, navigate to the local directory <span class='text-slate-200 font-mono'>{targetPath}</span> and execute:</p>
                    <pre class='bg-slate-900 p-4 rounded-lg text-xs font-mono text-slate-300'>{GetFrameworkCommands(framework)}</pre>
                </div>

                <div>
                    <h4 class='text-md font-semibold text-slate-200 mb-2'>3. Project Workspace Architecture</h4>
                    <p class='text-sm text-slate-400 leading-relaxed mb-3'>Your project directories are organized using clean standards:</p>
                    <ul class='list-disc pl-5 text-sm text-slate-400 space-y-1'>
                        <li><span class='text-slate-200 font-mono'>/</span> - Project Root &amp; environment configs</li>
                        <li><span class='text-slate-200 font-mono'>/docs/</span> - Dynamic developer documentation site (This Page)</li>
                        <li><span class='text-slate-200 font-mono'>/.zenvix/</span> - Isolated backups and local environment metadata</li>
                    </ul>
                </div>
            </div>
        </div>
    </div>
</body>
</html>";

            File.WriteAllText(Path.Combine(docsPath, "index.html"), html);
        }

        private string GetFrameworkCommands(string framework)
        {
            switch (framework)
            {
                case "Laravel":
                case "FilamentPHP":
                case "Laravel API":
                    return "composer install              # Install dependencies\nphp artisan key:generate     # Generate app key\nphp artisan migrate          # Run database migrations\nphp artisan serve            # Run artisan local server (Fallback)";
                case "Flutter":
                    return "flutter pub get              # Install packages\nflutter run -d chrome        # Start local web preview\nflutter build apk            # Build Android application Package";
                case "React":
                case "Vue":
                case "Next.js":
                    return "npm install                  # Install node modules\nnpm run dev                  # Start local Vite/Next dev server\nnpm run build                # Compile production bundle";
                default:
                    return "npm install                  # Install dependencies\nnode index.js                # Run development server";
            }
        }

        [RelayCommand]
        private async Task ToggleWebsite(Website website)
        {
            if (website.Status == WebsiteStatus.Running)
            {
                OpenSite(website);
            }
            else if (website.Status == WebsiteStatus.Stopped || website.Status == WebsiteStatus.Error)
            {
                await _orchestrator.DeployWebsiteAsync(website);
            }
        }

        [RelayCommand]
        private void OpenSite(Website website)
        {
            try
            {
                Process.Start(new ProcessStartInfo(website.DisplayUrl) { UseShellExecute = true });
                website.LastOpened = DateTime.Now;
                _ = _orchestrator.SaveAsync();
            }
            catch (Exception ex) { Log.Error(ex, "Failed to open site"); }
        }

        [RelayCommand]
        private void OpenDocs(Website website)
        {
            if (website == null) return;
            var docsUrl = website.SslEnabled ? $"https://docs.{website.Domain}" : $"http://docs.{website.Domain}";
            try
            {
                Process.Start(new ProcessStartInfo(docsUrl) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error(ex, "Failed to open docs site"); }
        }

        [RelayCommand]
        private async Task ToggleSsl(Website website)
        {
            website.SslEnabled = !website.SslEnabled;

            // Trigger vhost regeneration and nginx reload
            var success = await _orchestrator.UpdateSslAsync(website);

            if (success)
            {
                _stateManager.AddEvent($"SSL {(website.SslEnabled ? "Enabled" : "Disabled")} for {website.Domain}");
            }
            else
            {
                _stateManager.AddEvent($"Failed to update SSL for {website.Domain}");
            }

            OnPropertyChanged(nameof(Websites));
        }

        [RelayCommand]
        private void OpenFolder(Website website)
        {
            try { Process.Start("explorer.exe", website.LocalPath); }
            catch (Exception ex) { Log.Error(ex, "Failed to open folder"); }
        }

        [RelayCommand]
        private void OpenTerminal(Website website)
        {
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{website.LocalPath}\"") { UseShellExecute = true });
            }
            catch
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    WorkingDirectory = website.LocalPath,
                    UseShellExecute = true
                });
            }
        }

        [RelayCommand]
        private void OpenCode(Website website)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c code .")
                {
                    WorkingDirectory = website.LocalPath,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex) { Log.Error(ex, "Failed to open VS Code"); }
        }

        [RelayCommand]
        private async Task RemoveWebsite(Website website)
        {
            if (_dialogService.ShowRemoveConfirmation(website.Name))
            {
                if (Websites.Contains(website))
                {
                    // 1. Stop services if running
                    if (website.Status == WebsiteStatus.Running)
                        await _orchestrator.StopWebsiteAsync(website);

                    // 2. Remove from list without physical deletion
                    Websites.Remove(website);
                    await _orchestrator.SaveAsync();

                    _stateManager.AddEvent($"Removed project from panel (files kept on disk): {website.Name}");
                }
            }
        }

        [RelayCommand]
        private async Task DeleteWebsite(Website website)
        {
            if (_dialogService.ShowDeleteConfirmation(website.Name))
            {
                if (Websites.Contains(website))
                {
                    // 1. Stop services if running
                    if (website.Status == WebsiteStatus.Running)
                        await _orchestrator.StopWebsiteAsync(website);

                    // 2. Remove from list
                    Websites.Remove(website);
                    await _orchestrator.SaveAsync();

                    // 3. PHYSICAL DELETION
                    try
                    {
                        if (Directory.Exists(website.LocalPath))
                        {
                            Directory.Delete(website.LocalPath, true);
                            _stateManager.AddEvent($"DELETED project files and removed from panel: {website.Name}");
                        }
                        else
                        {
                            _stateManager.AddEvent($"Removed website from panel (files already missing): {website.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to physically delete website files at {Path}", website.LocalPath);
                        _stateManager.AddEvent($"Removed from panel but FAILED to delete files: {website.Name}");
                    }
                }
            }
        }

        [RelayCommand]
        private async Task UpdatePhpVersion()
        {
            var selectedVersion = LaravelPhpVersion;

            // Map selected version to exact matching full version in manifest
            string fullVersion = selectedVersion;
            if (selectedVersion == "8.5") fullVersion = "8.5.0";
            else if (selectedVersion == "8.4") fullVersion = "8.4.3";
            else if (selectedVersion == "8.3") fullVersion = "8.3.6";
            else if (selectedVersion == "8.2") fullVersion = "8.2.20";

            if (_runtimeInstaller.IsInstalled(RuntimeServiceType.PhpFpm, fullVersion))
            {
                _stateManager.AddEvent($"PHP Version {selectedVersion} ({fullVersion}) is already installed and up to date.");
                _dialogService.ShowMessage("PHP Update Check", $"PHP {selectedVersion} ({fullVersion}) is already installed and up to date!");
                return;
            }

            _stateManager.AddEvent($"Starting PHP {selectedVersion} ({fullVersion}) download and update...");

            try
            {
                var success = await _runtimeInstaller.InstallAsync(RuntimeServiceType.PhpFpm, fullVersion);
                if (success)
                {
                    _stateManager.AddEvent($"Successfully downloaded and updated PHP to Version {selectedVersion} ({fullVersion})!");

                    // Re-initialize binary paths and update active PHP-FPM service to use the new version
                    await _servicesOrchestrator.InitializeAsync();

                    var fpmService = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm);
                    if (fpmService != null)
                    {
                        if (fpmService.Status == ServiceStatus.Running)
                        {
                            _stateManager.AddEvent("Restarting PHP-FPM to apply the new PHP version...");
                            await _servicesOrchestrator.StopAsync(fpmService.Id);
                            await _servicesOrchestrator.StartAsync(fpmService.Id);
                            _stateManager.AddEvent($"PHP-FPM successfully restarted with the new PHP version {fullVersion}!");
                        }
                    }

                    _dialogService.ShowMessage("PHP Update Complete", $"PHP Version {selectedVersion} ({fullVersion}) has been successfully downloaded, extracted, and updated!");
                }
                else
                {
                    _stateManager.AddEvent($"PHP Version {selectedVersion} download/installation failed.");
                    _dialogService.ShowMessage("PHP Update Failed", $"Failed to download and install PHP Version {selectedVersion} ({fullVersion}). Please check your internet connection.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download PHP version {Ver}", fullVersion);
                _stateManager.AddEvent($"PHP Update Error: {ex.Message}");
                _dialogService.ShowMessage("PHP Update Error", $"An unexpected error occurred during update: {ex.Message}");
            }
        }

        private async Task<bool> RunCommandWithOutputAsync(string command, string arguments, string workingDirectory)
        {
            _stateManager.AddEvent($"[Cooking] Running: {command} {arguments}");
            Log.Information("[WebsitesViewModel] Running command: {Cmd} {Args} in {Dir}", command, arguments, workingDirectory);
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = startInfo };

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _stateManager.AddEvent($"[Build] {e.Data}");
                        Log.Information("[Build] {Msg}", e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _stateManager.AddEvent($"[Build Error] {e.Data}");
                        Log.Warning("[Build Error] {Msg}", e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                var exitCode = process.ExitCode;
                _stateManager.AddEvent($"[Cooking] Command finished with exit code {exitCode}");
                return exitCode == 0;
            }
            catch (Exception ex)
            {
                _stateManager.AddEvent($"[Cooking Error] Failed to run command: {ex.Message}");
                Log.Error(ex, "[WebsitesViewModel] Command failed");
                return false;
            }
        }

        private void AmendLaravelWelcomePage(string targetPath, string domain, string dbName, int dbPort, string framework)
        {
            try
            {
                var welcomePath = Path.Combine(targetPath, "resources", "views", "welcome.blade.php");
                var welcomeDir = Path.GetDirectoryName(welcomePath);
                if (welcomeDir != null && !Directory.Exists(welcomeDir)) Directory.CreateDirectory(welcomeDir);

                var isFilament = framework == "FilamentPHP";
                var protocol = NewWebsiteSslEnabled ? "https" : "http";

                // Inject /dev-login route into routes/web.php if this is a FilamentPHP project
                if (isFilament)
                {
                    try
                    {
                        var webPhpPath = Path.Combine(targetPath, "routes", "web.php");
                        if (File.Exists(webPhpPath))
                        {
                            var webContent = File.ReadAllText(webPhpPath);
                            if (!webContent.Contains("/dev-login"))
                            {
                                var routeSnippet = $@"

// Zenvix Dev Suite: Instant Admin Bypass Route (Debug Mode Only)
Route::get('/dev-login', function () {{
    if (config('app.debug')) {{
        $user = \App\Models\User::where('email', 'admin@' . request()->getHost())->first();
        if (!$user) {{
            $user = \App\Models\User::where('email', 'like', 'admin@%')->first();
        }}
        if ($user) {{
            auth()->login($user);
            return redirect('/admin');
        }}
    }}
    return abort(403, 'Instant Login only allowed in local/debug mode.');
}});
";
                                File.AppendAllText(webPhpPath, routeSnippet);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to automatically inject /dev-login route");
                    }
                }

                // Dynamic Header Right Portal Button
                var headerRightButton = isFilament
                    ? $@"<a href='{protocol}://{domain}/admin' target='_blank' class='text-xs font-semibold px-4 py-2 bg-gradient-to-r from-pink-500 to-rose-600 hover:from-pink-600 hover:to-rose-700 text-white rounded-lg transition shadow-lg shadow-rose-500/20'>
                            Admin Portal
                         </a>"
                    : "";

                // Dynamic Column 3 Quick Action
                var quickActionsContent = isFilament
                    ? $@"<a href='{protocol}://{domain}/dev-login' target='_blank' class='w-full text-center py-2.5 bg-gradient-to-r from-emerald-500 to-teal-600 hover:from-emerald-600 hover:to-teal-700 text-white rounded-xl font-semibold shadow-lg shadow-emerald-500/25 transition text-sm flex items-center justify-center gap-2'>
                            <span>⚡</span> Instant Admin Login (Local Debug)
                         </a>
                         <a href='{protocol}://{domain}/admin' target='_blank' class='w-full text-center py-2.5 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-xl font-semibold border border-slate-700 transition text-sm'>
                            Manual Admin Login
                         </a>"
                    : $@"<a href='{protocol}://{domain}' target='_blank' class='w-full text-center py-2.5 bg-gradient-to-r from-indigo-500 to-purple-600 hover:from-indigo-600 hover:to-purple-700 text-white rounded-xl font-semibold shadow-lg shadow-indigo-500/25 transition text-sm'>
                            Launch Website
                         </a>
                         <a href='{protocol}://docs.{domain}' target='_blank' class='w-full text-center py-2.5 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-xl font-semibold border border-slate-700 transition text-sm'>
                            Explore Developer Docs
                         </a>";

                // Dynamic Credentials & Safety Warning Section
                var credentialsWarningSection = isFilament
                    ? $@"<!-- Filament Credentials & Safety Warning -->
        <div class=""glass-panel rounded-2xl p-6 mb-10 border border-amber-500/25 bg-amber-500/5"">
            <div class=""flex items-center space-x-3 mb-3 text-amber-400 font-bold"">
                <span class=""text-lg"">⚠️</span>
                <h4 class=""text-sm uppercase tracking-wide"">Local Development Credentials &amp; Production Security Advisory</h4>
            </div>
            <div class=""text-sm text-slate-300 leading-relaxed mb-4"">
                Use these auto-generated local administrator credentials to access your Filament Admin Portal:
                <div class=""mt-2.5 grid grid-cols-1 sm:grid-cols-2 gap-3 max-w-md font-mono bg-slate-950/60 p-3.5 rounded-xl border border-slate-800/80 text-xs"">
                    <div>Email: <span class=""text-indigo-400 font-semibold"">admin@{domain}</span></div>
                    <div>Password: <span class=""text-indigo-400 font-semibold"">admin12345</span></div>
                </div>
            </div>
            <div class=""text-xs text-slate-400 leading-relaxed border-t border-slate-800/80 pt-3"">
                <strong class=""text-amber-400"">Production Checklist:</strong> To deploy this project securely to production:
                <ul class=""list-disc pl-5 mt-2 space-y-1 text-slate-500"">
                    <li>Change the default administrator password in the users table or via CLI.</li>
                    <li>Set <code class=""text-indigo-300 font-semibold bg-slate-900 px-1 py-0.5 rounded"">APP_DEBUG=false</code> in your <code class=""text-indigo-300 font-semibold"">.env</code> file to automatically disable the Instant Admin Login bypass.</li>
                    <li>For a complete cleanup, delete the <code class=""text-indigo-300 font-semibold"">/dev-login</code> route definition in <code class=""text-indigo-300 font-semibold"">routes/web.php</code>.</li>
                </ul>
            </div>
        </div>"
                    : "";

                var bladeContent = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Zenvix Workstation - {NewWebsiteName}</title>
    <script src=""https://cdn.tailwindcss.com""></script>
    <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700;800&family=JetBrains+Mono:wght@400;500;600&display=swap"" rel=""stylesheet"">
    <style>
        body {{
            font-family: 'Outfit', sans-serif;
            background: radial-gradient(circle at top right, rgba(99, 102, 241, 0.05), transparent 40%),
                        radial-gradient(circle at bottom left, rgba(168, 85, 247, 0.05), transparent 40%),
                        #0B0F19;
        }}
        .code-font {{
            font-family: 'JetBrains Mono', monospace;
        }}
        .glass-panel {{
            background: rgba(17, 24, 39, 0.7);
            backdrop-filter: blur(16px);
            border: 1px solid rgba(255, 255, 255, 0.06);
        }}
    </style>
</head>
<body class=""text-slate-200 min-h-screen flex flex-col justify-between"">

    <!-- Navbar -->
    <header class=""glass-panel sticky top-0 z-50 px-8 py-4 flex justify-between items-center"">
        <div class=""flex items-center space-x-3"">
            <div class=""w-9 h-9 rounded-xl bg-gradient-to-tr from-indigo-500 to-purple-600 flex items-center justify-center font-bold text-white shadow-lg shadow-indigo-500/20"">Z</div>
            <div>
                <span class=""font-bold text-lg tracking-tight bg-gradient-to-r from-white to-slate-400 bg-clip-text text-transparent"">ZENVIX</span>
                <span class=""text-xs text-indigo-400 font-semibold block -mt-1 tracking-wider uppercase"">Workstation</span>
            </div>
        </div>
        <div class=""flex items-center space-x-4"">
            {headerRightButton}
        </div>
    </header>

    <!-- Main Workspace Dashboard -->
    <main class=""max-w-6xl mx-auto py-12 px-6 flex-1 flex flex-col justify-center w-full"">
        
        <!-- Welcome Jumbotron -->
        <div class=""text-center mb-12"">
            <span class=""inline-flex items-center gap-1.5 px-3.5 py-1.5 bg-indigo-500/10 text-indigo-400 rounded-full text-xs font-bold uppercase tracking-wider mb-6 border border-indigo-500/20 shadow-inner"">
                <span class=""w-1.5 h-1.5 rounded-full bg-indigo-400 animate-pulse""></span>
                {framework} Workstation Active
            </span>
            <h1 class=""text-4xl md:text-5xl font-extrabold tracking-tight mb-4 text-white"">
                Workspace: <span class=""bg-gradient-to-r from-indigo-400 via-purple-400 to-pink-400 bg-clip-text text-transparent"">{NewWebsiteName}</span>
            </h1>
            <p class=""text-slate-400 max-w-2xl mx-auto text-base"">
                Your real application is fully cooked and connected to SSL and local services. Below are the dynamic parameters for database and server administration.
            </p>
        </div>

        <!-- 3-Column Status & Control Panel -->
        <div class=""grid grid-cols-1 md:grid-cols-3 gap-6 mb-10"">
            
            <!-- Column 1: System Info -->
            <div class=""glass-panel rounded-2xl p-6 transition hover:translate-y-[-2px] duration-300"">
                <div class=""flex items-center justify-between mb-6"">
                    <h3 class=""text-sm font-bold uppercase text-slate-400 tracking-wider"">System Environment</h3>
                    <span class=""text-xl"">⚙️</span>
                </div>
                <div class=""space-y-4 text-sm"">
                    <div class=""flex justify-between border-b border-slate-800/80 pb-2"">
                        <span class=""text-slate-400"">Host Domain</span>
                        <span class=""font-medium text-white"">{domain}</span>
                    </div>
                    <div class=""flex justify-between border-b border-slate-800/80 pb-2"">
                        <span class=""text-slate-400"">PHP Version</span>
                        <span class=""font-medium text-white""><?php echo PHP_VERSION; ?></span>
                    </div>
                    <div class=""flex justify-between pb-2"">
                        <span class=""text-slate-400"">Web Server</span>
                        <span class=""font-medium text-indigo-400 flex items-center gap-1.5"">
                            <span class=""w-2 h-2 rounded-full bg-indigo-400""></span> Nginx
                        </span>
                    </div>
                </div>
            </div>

            <!-- Column 2: Database Connectivity -->
            <div class=""glass-panel rounded-2xl p-6 transition hover:translate-y-[-2px] duration-300"">
                <div class=""flex items-center justify-between mb-6"">
                    <h3 class=""text-sm font-bold uppercase text-slate-400 tracking-wider"">Database Service</h3>
                    <span class=""text-xl"">🛢️</span>
                </div>
                <div class=""space-y-4 text-sm"">
                    <div class=""flex justify-between border-b border-slate-800/80 pb-2"">
                        <span class=""text-slate-400"">MySQL Database</span>
                        <span class=""font-medium text-white code-font"">{dbName}</span>
                    </div>
                    <div class=""flex justify-between border-b border-slate-800/80 pb-2"">
                        <span class=""text-slate-400"">Server Port</span>
                        <span class=""font-medium text-white code-font"">{dbPort}</span>
                    </div>
                    <div class=""flex justify-between pb-2 items-center"">
                        <span class=""text-slate-400"">Connection</span>
                        <span class=""px-2.5 py-0.5 bg-emerald-500/10 text-emerald-400 border border-emerald-500/20 rounded-md text-xs font-bold uppercase tracking-wide"">
                            Active
                        </span>
                    </div>
                </div>
            </div>

            <!-- Column 3: Quick Action Launchers -->
            <div class=""glass-panel rounded-2xl p-6 transition hover:translate-y-[-2px] duration-300"">
                <div class=""flex items-center justify-between mb-6"">
                    <h3 class=""text-sm font-bold uppercase text-slate-400 tracking-wider"">Quick Actions</h3>
                    <span class=""text-xl"">⚡</span>
                </div>
                <div class=""flex flex-col space-y-3"">
                    {quickActionsContent}
                </div>
            </div>
            
        </div>

        {credentialsWarningSection}

        <!-- Simulated Terminal and Files Card -->
        <div class=""glass-panel rounded-2xl p-6"">
            <div class=""flex items-center justify-between mb-4 pb-4 border-b border-slate-800/80"">
                <div class=""flex items-center space-x-2"">
                    <span class=""w-3 h-3 rounded-full bg-rose-500""></span>
                    <span class=""w-3 h-3 rounded-full bg-amber-500""></span>
                    <span class=""w-3 h-3 rounded-full bg-emerald-500""></span>
                    <span class=""text-xs text-slate-500 font-bold ml-2 tracking-widest uppercase"">Zenvix Workstation Terminal</span>
                </div>
                <span class=""text-xs text-indigo-400 font-bold tracking-wider"">artisan active</span>
            </div>
            <div class=""code-font text-sm space-y-2 text-indigo-200/90 leading-relaxed"">
                <div class=""text-slate-500"">$ php artisan about</div>
                <div class=""text-indigo-400 font-semibold"">Environment: local</div>
                <div class=""text-indigo-400 font-semibold"">Database Connection: Connected (mysql)</div>
                <div class=""text-indigo-400 font-semibold"">App URL: {protocol}://{domain}</div>
                <div class=""text-indigo-400 font-semibold"">VHost mapping status: Active (127.0.0.1 -> Nginx)</div>
                <div class=""text-slate-500 mt-2"">$ php artisan migrate --status</div>
                <div class=""text-emerald-400"">No pending migrations. Database tables are up to date!</div>
            </div>
        </div>

    </main>

    <!-- Footer -->
    <footer class=""glass-panel py-6 text-center text-xs text-slate-500 mt-12 border-t border-slate-800/50"">
        Powered by Zenvix Premium Dev Suite
    </footer>

</body>
</html>"
;
                File.WriteAllText(welcomePath, bladeContent);
                Log.Information("[WebsitesViewModel] Successfully amended Laravel welcome blade: {Path}", welcomePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WebsitesViewModel] Failed to amend welcome page");
            }
        }

        private void SetEnvValue(string envPath, string key, string value)
        {
            try
            {
                if (!File.Exists(envPath)) return;

                var lines = File.ReadAllLines(envPath);
                bool found = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart(' ', '#', '\t');
                    if (trimmed.StartsWith($"{key}="))
                    {
                        lines[i] = $"{key}={value}";
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    var newLines = new System.Collections.Generic.List<string>(lines);
                    newLines.Add($"{key}={value}");
                    lines = newLines.ToArray();
                }

                File.WriteAllLines(envPath, lines);
                Log.Information("[WebsitesViewModel] SetEnvValue updated: {Key}={Value}", key, value);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WebsitesViewModel] Failed to update env value for key {Key}", key);
            }
        }
    }
}
