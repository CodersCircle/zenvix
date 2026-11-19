using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Zenvix.Core.Models;

namespace Zenvix.Modules.Toolchain
{
    public enum UpdateKind { Tool, ProjectPackage }

    public sealed record UpdateItem(
        UpdateKind Kind, string Name, string Description, string? Installed, string? Latest,
        ToolKind? Tool = null, string? ProjectPath = null, string? Package = null)
    {
        /// <summary>The newest two stable versions (newest first) the user can pick from.</summary>
        public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();
        /// <summary>The user's own copy of this tool outside Zenvix, if installed.</summary>
        public SystemTool? System { get; init; }
        public bool SystemUpdateAvailable => System?.Version != null && Latest != null && ToolchainManager.IsNewer(Latest, System.Version);

        public bool IsInstalled => Installed != null;
        public bool UpdateAvailable => Installed != null && Latest != null && ToolchainManager.IsNewer(Latest, Installed);
        /// <summary>Major upgrades (e.g. Laravel 12 → 13) need code changes – we point to the guide instead of auto-updating.</summary>
        public bool IsMajorUpgrade => UpdateAvailable && ToolchainManager.ParseVersion(Latest!).Major > ToolchainManager.ParseVersion(Installed!).Major;
    }

    public interface IUpdateService
    {
        event Action<IReadOnlyList<UpdateItem>>? UpdatesFound;
        IReadOnlyList<UpdateItem> LastResult { get; }
        DateTime? LastChecked { get; }
        Task<IReadOnlyList<UpdateItem>> CheckAsync(IEnumerable<Website> projects, CancellationToken ct = default);
        Task<bool> ApplyAsync(UpdateItem item, IProgress<string> log, CancellationToken ct = default);
        /// <summary>Installs a specific one of <see cref="UpdateItem.Versions"/> of a Zenvix-managed tool.</summary>
        Task<bool> InstallVersionAsync(UpdateItem item, string version, IProgress<string> log, CancellationToken ct = default);
        /// <summary>Updates the user's own (non-Zenvix) installation of a tool.</summary>
        Task<bool> UpdateSystemAsync(UpdateItem item, IProgress<string> log, CancellationToken ct = default);
        void StartBackgroundChecks(Func<IEnumerable<Website>> projects, TimeSpan interval);
    }

    /// <summary>Checks Zenvix-managed SDKs and project packages (Laravel, Filament, Livewire) for new stable releases.</summary>
    public sealed class UpdateService : IUpdateService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly string[] TrackedPackages = { "laravel/framework", "filament/filament", "livewire/livewire" };
        private readonly IToolchainManager _tools;
        private Timer? _timer;

        public event Action<IReadOnlyList<UpdateItem>>? UpdatesFound;
        public IReadOnlyList<UpdateItem> LastResult { get; private set; } = Array.Empty<UpdateItem>();
        public DateTime? LastChecked { get; private set; }

        public UpdateService(IToolchainManager tools) => _tools = tools;

        public void StartBackgroundChecks(Func<IEnumerable<Website>> projects, TimeSpan interval)
        {
            _timer?.Dispose();
            _timer = new Timer(async _ =>
            {
                try { await CheckAsync(projects()); }
                catch (Exception ex) { Log.Warning(ex, "[Updates] background check failed"); }
            }, null, TimeSpan.FromSeconds(5), interval);
        }

        public async Task<IReadOnlyList<UpdateItem>> CheckAsync(IEnumerable<Website> projects, CancellationToken ct = default)
        {
            var items = new List<UpdateItem>();

            foreach (var t in await _tools.GetStatusAsync(checkLatest: false, ct))
            {
                var releases = await _tools.GetReleasesAsync(t.Kind, 2, ct);
                var system = await _tools.DetectSystemAsync(t.Kind, ct);
                items.Add(new UpdateItem(UpdateKind.Tool, t.Name, t.Description, t.Installed, releases.FirstOrDefault()?.Version, Tool: t.Kind)
                {
                    Versions = releases.Select(r => r.Version).ToList(),
                    System = system,
                });
            }

            var latestCache = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var site in projects.Where(p => Directory.Exists(p.LocalPath)).ToList())
            {
                var installed = ReadComposerLock(site.LocalPath);
                foreach (var pkg in TrackedPackages)
                {
                    if (!installed.TryGetValue(pkg, out var ver)) continue;
                    if (!latestCache.TryGetValue(pkg, out var versions))
                        latestCache[pkg] = versions = await LatestStablePerMajorAsync(pkg, 2, ct);
                    items.Add(new UpdateItem(UpdateKind.ProjectPackage, $"{site.Name} · {PrettyName(pkg)}",
                        site.LocalPath, ver, versions.FirstOrDefault(), ProjectPath: site.LocalPath, Package: pkg) { Versions = versions });
                }
            }

            LastResult = items;
            LastChecked = DateTime.Now;
            var available = items.Where(i => i.UpdateAvailable).ToList();
            Log.Information("[Updates] checked {Count} items, {Available} update(s) available", items.Count, available.Count);
            UpdatesFound?.Invoke(items);
            return items;
        }

        public async Task<bool> ApplyAsync(UpdateItem item, IProgress<string> log, CancellationToken ct = default)
        {
            if (item.Kind == UpdateKind.Tool && item.Tool is ToolKind tool)
                return await _tools.InstallOrUpdateAsync(tool, log, ct);

            if (item.Kind == UpdateKind.ProjectPackage && item.ProjectPath != null && item.Package != null)
            {
                if (item.IsMajorUpgrade)
                {
                    log.Report($"{PrettyName(item.Package)} {item.Latest} is a new major version – follow its upgrade guide; Zenvix only applies safe minor/patch updates.");
                    return false;
                }
                if (!await _tools.EnsureAsync(ToolKind.Composer, log, ct)) return false;
                var r = await _tools.RunAsync($"composer update {item.Package} -W --no-interaction", item.ProjectPath, log, TimeSpan.FromMinutes(15), ct);
                return r.Ok;
            }
            return false;
        }

        public Task<bool> InstallVersionAsync(UpdateItem item, string version, IProgress<string> log, CancellationToken ct = default)
            => item.Tool is ToolKind tool ? _tools.InstallVersionAsync(tool, version, log, ct) : Task.FromResult(false);

        public Task<bool> UpdateSystemAsync(UpdateItem item, IProgress<string> log, CancellationToken ct = default)
            => item.System != null ? _tools.UpdateSystemAsync(item.System, log, ct) : Task.FromResult(false);

        private static string PrettyName(string pkg) => pkg switch
        {
            "laravel/framework" => "Laravel", "filament/filament" => "Filament", "livewire/livewire" => "Livewire", _ => pkg,
        };

        private static Dictionary<string, string> ReadComposerLock(string projectPath)
        {
            var result = new Dictionary<string, string>();
            var lockFile = Path.Combine(projectPath, "composer.lock");
            if (!File.Exists(lockFile)) return result;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(lockFile));
                foreach (var p in doc.RootElement.GetProperty("packages").EnumerateArray())
                {
                    var name = p.GetProperty("name").GetString();
                    if (name != null && TrackedPackages.Contains(name))
                        result[name] = p.GetProperty("version").GetString()!.TrimStart('v');
                }
            }
            catch (Exception ex) { Log.Debug(ex, "[Updates] could not read {Lock}", lockFile); }
            return result;
        }

        private static async Task<IReadOnlyList<string>> LatestStablePerMajorAsync(string package, int majors, CancellationToken ct)
        {
            var result = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://repo.packagist.org/p2/{package}.json", ct));
                var seenMajors = new HashSet<int>();
                foreach (var v in doc.RootElement.GetProperty("packages").GetProperty(package).EnumerateArray())
                {
                    var ver = (v.GetProperty("version").GetString() ?? "").TrimStart('v');
                    var lower = ver.ToLowerInvariant();
                    if (lower.Contains("dev") || lower.Contains("alpha") || lower.Contains("beta") || lower.Contains("rc")) continue;
                    if (!seenMajors.Add(ToolchainManager.ParseVersion(ver).Major)) continue; // packagist lists newest first
                    result.Add(ver);
                    if (result.Count == majors) break;
                }
            }
            catch (Exception ex) { Log.Warning("[Updates] packagist lookup for {Pkg} failed: {Msg}", package, ex.Message); }
            return result;
        }
    }
}
