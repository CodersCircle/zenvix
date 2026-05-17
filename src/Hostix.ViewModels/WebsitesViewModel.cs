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

        public string FullCreatedPathDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(NewWebsitePath)) return "None selected";
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

        [RelayCommand]
        private void ShowAddWebsite()
        {
            // Default path if empty
            if (string.IsNullOrEmpty(NewWebsitePath))
            {
                NewWebsitePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HostixProjects");
                if (!Directory.Exists(NewWebsitePath)) Directory.CreateDirectory(NewWebsitePath);
            }
            IsAddingWebsite = true;
        }

        [RelayCommand]
        private void BrowseFolder()
        {
            var selected = _dialogService.OpenFolderDialog(NewWebsitePath);
            if (!string.IsNullOrEmpty(selected))
            {
                NewWebsitePath = selected;
            }
        }

        [RelayCommand]
        private void CancelAddWebsite()
        {
            IsAddingWebsite = false;
            NewWebsiteName = "";
            NewWebsiteDomain = "";
            NewWebsiteSslEnabled = true;
        }

        [RelayCommand]
        private async Task CreateWebsite()
        {
            if (string.IsNullOrWhiteSpace(NewWebsiteName) || string.IsNullOrWhiteSpace(NewWebsitePath)) return;

            var projectSlug = NewWebsiteName.ToLower().Replace(" ", "-").Trim();
            var targetPath = Path.Combine(NewWebsitePath, projectSlug);

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

                // 3. Bootstrap if empty
                if (!Directory.EnumerateFileSystemEntries(targetPath).Any())
                {
                    var starterFile = Path.Combine(targetPath, "index.php");
                    File.WriteAllText(starterFile, "<?php\n\necho \"<center><h1>Welcome to Zenvix!</h1></center>\";\necho \"<center><p>Your project <b>\" + NewWebsiteName + \"</b> is ready.</p></center>\";");
                }

                // 4. Resolve Domain                                                                    
                var domain = string.IsNullOrWhiteSpace(NewWebsiteDomain)
                    ? $"{projectSlug}.test"
                    : NewWebsiteDomain;

                // 5. Prevent Duplicates
                if (Websites.Any(w => w.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                {
                    _stateManager.AddEvent($"Error: A website with domain {domain} already exists.");
                    return;
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
                CancelAddWebsite();
                _stateManager.AddEvent($"Success: Created project at {targetPath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create project directory");
                _stateManager.AddEvent($"Error: Could not create folder at {targetPath}. Check permissions.");
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
