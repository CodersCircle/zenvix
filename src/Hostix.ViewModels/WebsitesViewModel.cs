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
        private string _laravelPhpVersion = "8.3";

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
            IDialogService dialogService)
        {
            _orchestrator = orchestrator;
            _stateManager = stateManager;
            _dialogService = dialogService;

            // Initial load
            _ = _orchestrator.InitializeAsync();
        }

        public WebsitesViewModel()
        {
            _orchestrator = null!;
            _stateManager = null!;
            _dialogService = null!;
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
                    Frameworks.Add("FilamentPHP");
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

                // 5. Bootstrap dynamic template
                if (!isImport)
                {
                    var framework = string.IsNullOrEmpty(SelectedFramework) ? "Tailwind Starter" : SelectedFramework;
                    BootstrapProjectTemplates(targetPath, framework);

                    // 6. Generate Docs Site if enabled
                    if (CreateDocsSite)
                    {
                        GenerateDocsSite(targetPath, domain, framework);
                    }
                }

                var website = new Website
                {
                    Name = NewWebsiteName,
                    LocalPath = targetPath,
                    Domain = domain,
                    SslEnabled = NewWebsiteSslEnabled,
                    Type = _orchestrator.DetectProjectType(targetPath)
                };

                await _orchestrator.DeployWebsiteAsync(website);

                // 7. Automatically Deploy Docs site if enabled
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
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create project directory");
                _stateManager.AddEvent($"Error: Could not create folder at {targetPath}. Check permissions.");
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
                    File.WriteAllText(Path.Combine(targetPath, "index.php"), 
                        "<?php\n\n" +
                        "echo '<div style=\"font-family: sans-serif; text-align: center; margin-top: 10%; background: #fafafa; padding: 40px; border-radius: 12px; max-width: 600px; margin-left: auto; margin-right: auto; box-shadow: 0 4px 6px rgba(0,0,0,0.05); border: 1px solid #e5e7eb;\">';" +
                        "echo '<div style=\"font-size: 48px; margin-bottom: 20px;\">🚀</div>';" +
                        $"echo '<h1 style=\"color: #1e1b4b; margin-bottom: 8px;\">Zenvix {framework} Workstation Active</h1>';" +
                        $"echo '<p style=\"color: #4b5563; font-size: 15px;\">Project: <b>' . htmlspecialchars(\"" + NewWebsiteName + "\") . '</b></p>';" +
                        $"echo '<p style=\"color: #6b7280; font-size: 13px;\">Database: <b>" + LaravelDatabase + "</b> | PHP: <b>" + LaravelPhpVersion + "</b> | Testing: <b>" + LaravelTestingFramework + "</b></p>';" +
                        $"echo '<p style=\"color: #6b7280; font-size: 13px; margin-bottom: 24px;\">Authentication Starter: <b>" + (LaravelAuthEnabled ? "Enabled (Breeze/Jetstream)" : "Disabled") + "</b></p>';" +
                        $"echo '<a href=\"https://docs." + NewWebsiteDomain + "\" style=\"display: inline-block; background: #6366f1; color: white; padding: 12px 24px; border-radius: 8px; font-weight: bold; text-decoration: none; font-size: 14px;\">View Developer Docs Workspace →</a>';" +
                        "echo '</div>';");
                    File.WriteAllText(Path.Combine(targetPath, "artisan"), "# Laravel Artisan Command Runner");
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

        private void GenerateDocsSite(string targetPath, string domain, string framework)
        {
            var docsPath = Path.Combine(targetPath, "docs");
            if (!Directory.Exists(docsPath)) Directory.CreateDirectory(docsPath);

            var dbDetails = framework == "Laravel" || framework == "FilamentPHP" ? $"SQLite / MySQL (Active: {LaravelDatabase})" : "None / Local File System";
            var phpVer = framework == "Laravel" || framework == "FilamentPHP" ? LaravelPhpVersion : "8.3";
            var gitState = GitInitEnabled ? "Initialized" : "Not enabled";

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
    }
}
