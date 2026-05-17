using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hostix.Core.Models;
using Serilog;

namespace Hostix.Modules.Services
{
    public interface IProjectScanner
    {
        IEnumerable<Website> ScanNow(string rootPath);
        void StartWatching(string rootPath);
        ProjectFramework DetectFramework(string projectPath);
    }

    public class ProjectScanner : IProjectScanner
    {
        private FileSystemWatcher? _watcher;

        public IEnumerable<Website> ScanNow(string rootPath)
        {
            if (!Directory.Exists(rootPath))
            {
                Log.Warning("Project root path does not exist: {Path}", rootPath);
                return Array.Empty<Website>();
            }

            var projects = new List<Website>();

            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                var name = Path.GetFileName(dir);
                var framework = DetectFramework(dir);
                var phpVersion = DetectPhpVersion(dir);

                projects.Add(new Website
                {
                    Name = name,
                    Domain = $"{name.ToLower().Replace(" ", "-")}.test",
                    LocalPath = dir,
                    PhpVersion = phpVersion,
                    Status = WebsiteStatus.Stopped
                });
            }

            Log.Information("Scanned {Count} projects in {Path}", projects.Count, rootPath);
            return projects;
        }

        public ProjectFramework DetectFramework(string path)
        {
            if (File.Exists(Path.Combine(path, "artisan")))         return ProjectFramework.Laravel;
            if (File.Exists(Path.Combine(path, "wp-config.php")))   return ProjectFramework.WordPress;
            if (File.Exists(Path.Combine(path, "next.config.js")))  return ProjectFramework.NextJs;
            if (File.Exists(Path.Combine(path, "nuxt.config.js")))  return ProjectFramework.Vue;
            if (File.Exists(Path.Combine(path, "vite.config.js")))  return ProjectFramework.Vue;
            if (File.Exists(Path.Combine(path, "package.json")))    return ProjectFramework.Node;
            if (Directory.GetFiles(path, "*.php").Any())            return ProjectFramework.Static;
            return ProjectFramework.Unknown;
        }

        private string DetectPhpVersion(string path)
        {
            // Check .php-version file (Herd/Laravel convention)
            var phpVersionFile = Path.Combine(path, ".php-version");
            if (File.Exists(phpVersionFile))
                return File.ReadAllText(phpVersionFile).Trim();

            // Check composer.json for PHP constraint
            var composerJson = Path.Combine(path, "composer.json");
            if (File.Exists(composerJson))
            {
                var content = File.ReadAllText(composerJson);
                if (content.Contains("\"php\": \">=8.3\"")) return "8.3";
                if (content.Contains("\"php\": \">=8.2\"")) return "8.2";
                if (content.Contains("\"php\": \">=8.1\"")) return "8.1";
                if (content.Contains("\"php\": \">=8.0\"")) return "8.0";
                if (content.Contains("\"php\": \">=7.4\"")) return "7.4";
            }

            return "8.2"; // default
        }

        public void StartWatching(string rootPath)
        {
            if (!Directory.Exists(rootPath)) Directory.CreateDirectory(rootPath);

            _watcher = new FileSystemWatcher(rootPath)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _watcher.Created += (s, e) => Log.Information("New project detected: {Name}", e.Name);
            _watcher.Deleted += (s, e) => Log.Information("Project removed: {Name}", e.Name);
            Log.Information("Project watcher active on: {Path}", rootPath);
        }
    }
}
