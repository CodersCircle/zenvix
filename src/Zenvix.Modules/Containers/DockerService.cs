using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Zenvix.Modules.Containers
{
    public enum DockerState { NotInstalled, NotRunning, Running }

    public sealed class ContainerInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Image { get; init; } = "";
        public string State { get; init; } = "";   // running, exited, created, paused ...
        public string Status { get; init; } = "";  // "Up 3 minutes", "Exited (0) 2 hours ago"
        public string Ports { get; init; } = "";
        public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
        public bool CreatedByZenvix { get; init; }

        /// <summary>First published host port, e.g. "0.0.0.0:8081->80/tcp" → 8081.</summary>
        public int? FirstHostPort
        {
            get
            {
                var m = System.Text.RegularExpressions.Regex.Match(Ports, @":(\d+)->");
                return m.Success ? int.Parse(m.Groups[1].Value) : null;
            }
        }
    }

    public sealed record ContainerTemplate(
        string Key, string Title, string Description, string Image, int ContainerPort,
        int PreferredHostPort, string[] Env, string? Volume = null, int? ExtraContainerPort = null, int? ExtraHostPort = null);

    public interface IDockerService
    {
        IReadOnlyList<ContainerTemplate> Templates { get; }
        Task<(DockerState State, string Detail)> GetStateAsync(CancellationToken ct = default);
        bool TryStartDockerDesktop();
        Task<List<ContainerInfo>> ListAsync(CancellationToken ct = default);
        Task<(bool Ok, string Output)> StartAsync(string id, CancellationToken ct = default);
        Task<(bool Ok, string Output)> StopAsync(string id, CancellationToken ct = default);
        Task<(bool Ok, string Output)> RestartAsync(string id, CancellationToken ct = default);
        Task<(bool Ok, string Output)> RemoveAsync(string id, CancellationToken ct = default);
        Task<string> LogsAsync(string id, int tail = 300, CancellationToken ct = default);
        Task<(bool Ok, string Output)> RunTemplateAsync(ContainerTemplate t, string name, IProgress<string> log, CancellationToken ct = default);
        Task<(bool Ok, string Output)> RunCustomAsync(string name, string image, string ports, string env, IProgress<string> log, CancellationToken ct = default);
    }

    /// <summary>Thin wrapper around the docker CLI (Docker Desktop / Docker Engine / Rancher / Podman docker-compat).</summary>
    public class DockerService : IDockerService
    {
        private const string ZenvixLabel = "dev.zenvix.managed=true";

        public IReadOnlyList<ContainerTemplate> Templates { get; } = new List<ContainerTemplate>
        {
            new("mysql",    "MySQL 8.4",        "Relational database",       "mysql:8.4",                 3306,  3306,  new[] { "MYSQL_ROOT_PASSWORD=root" }, "/var/lib/mysql"),
            new("mariadb",  "MariaDB 11",       "MySQL-compatible database", "mariadb:11",                3306,  3306,  new[] { "MARIADB_ROOT_PASSWORD=root" }, "/var/lib/mysql"),
            new("postgres", "PostgreSQL 16",    "Advanced SQL database",     "postgres:16",               5432,  5432,  new[] { "POSTGRES_PASSWORD=postgres" }, "/var/lib/postgresql/data"),
            new("redis",    "Redis 7",          "Cache & queues",            "redis:7",                   6379,  6379,  Array.Empty<string>(), "/data"),
            new("mongo",    "MongoDB 7",        "Document database",         "mongo:7",                   27017, 27017, Array.Empty<string>(), "/data/db"),
            new("mailpit",  "Mailpit",          "Catch test emails",         "axllent/mailpit:latest",    1025,  1026,  Array.Empty<string>(), null, 8025, 8026),
            new("adminer",  "Adminer",          "Database web UI",           "adminer:latest",            8080,  8081,  Array.Empty<string>()),
            new("nginx",    "Nginx (static)",   "Quick static web server",   "nginx:alpine",              80,    8088,  Array.Empty<string>()),
        };

        public async Task<(DockerState State, string Detail)> GetStateAsync(CancellationToken ct = default)
        {
            try
            {
                var r = await RunAsync("version --format \"{{.Server.Version}}\"", ct, timeoutMs: 15000);
                if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut))
                    return (DockerState.Running, $"Docker Engine {r.StdOut.Trim()}");
                return (DockerState.NotRunning, "Docker is installed but the engine is not running.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (DockerState.NotInstalled, "Docker is not installed.");
            }
        }

        public bool TryStartDockerDesktop()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Docker", "Docker", "Docker Desktop.exe"),
            };
            var exe = candidates.FirstOrDefault(File.Exists);
            if (exe == null) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }

        public async Task<List<ContainerInfo>> ListAsync(CancellationToken ct = default)
        {
            var r = await RunAsync("ps -a --no-trunc --format \"{{json .}}\"", ct);
            var list = new List<ContainerInfo>();
            if (r.ExitCode != 0) return list;

            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string S(string n) => root.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
                    list.Add(new ContainerInfo
                    {
                        Id = S("ID"),
                        Name = S("Names"),
                        Image = S("Image"),
                        State = S("State"),
                        Status = S("Status"),
                        Ports = S("Ports"),
                        CreatedByZenvix = S("Labels").Contains(ZenvixLabel),
                    });
                }
                catch (JsonException) { /* skip malformed line */ }
            }
            return list.OrderByDescending(c => c.IsRunning).ThenBy(c => c.Name).ToList();
        }

        public Task<(bool Ok, string Output)> StartAsync(string id, CancellationToken ct = default) => Simple($"start {id}", ct);
        public Task<(bool Ok, string Output)> StopAsync(string id, CancellationToken ct = default) => Simple($"stop {id}", ct);
        public Task<(bool Ok, string Output)> RestartAsync(string id, CancellationToken ct = default) => Simple($"restart {id}", ct);
        public Task<(bool Ok, string Output)> RemoveAsync(string id, CancellationToken ct = default) => Simple($"rm -f {id}", ct);

        public async Task<string> LogsAsync(string id, int tail = 300, CancellationToken ct = default)
        {
            var r = await RunAsync($"logs --tail {tail} --timestamps {id}", ct);
            return (r.StdOut + r.StdErr).Trim();
        }

        public async Task<(bool Ok, string Output)> RunTemplateAsync(ContainerTemplate t, string name, IProgress<string> log, CancellationToken ct = default)
        {
            var hostPort = FindFreePort(t.PreferredHostPort);
            if (hostPort != t.PreferredHostPort)
                log.Report($"Port {t.PreferredHostPort} is busy – using {hostPort} instead.");

            name = Sanitize(name);
            var sb = new StringBuilder($"run -d --name {name} --label {ZenvixLabel} --restart unless-stopped -p {hostPort}:{t.ContainerPort}");
            if (t.ExtraContainerPort is int ecp && t.ExtraHostPort is int ehp)
            {
                var extra = FindFreePort(ehp, exclude: hostPort);
                sb.Append($" -p {extra}:{ecp}");
                log.Report($"Web UI will be on http://localhost:{extra}");
            }
            foreach (var e in t.Env) sb.Append($" -e {e}");
            if (t.Volume != null) sb.Append($" -v zenvix_{name}_data:{t.Volume}");
            sb.Append(' ').Append(t.Image);

            log.Report($"$ docker {sb}");
            log.Report("Pulling image if needed – first run can take a minute ...");
            var r = await RunAsync(sb.ToString(), ct, timeoutMs: 15 * 60 * 1000);
            var output = (r.StdOut + r.StdErr).Trim();
            if (r.ExitCode == 0)
            {
                log.Report($"✔ {t.Title} is running on localhost:{hostPort}");
                if (t.Env.Length > 0) log.Report("Credentials: " + string.Join(", ", t.Env));
            }
            else log.Report($"✖ {output}");
            return (r.ExitCode == 0, output);
        }

        public async Task<(bool Ok, string Output)> RunCustomAsync(string name, string image, string ports, string env, IProgress<string> log, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(image)) return (false, "Image is required.");
            var sb = new StringBuilder("run -d");
            if (!string.IsNullOrWhiteSpace(name)) sb.Append($" --name {Sanitize(name)}");
            sb.Append($" --label {ZenvixLabel}");
            foreach (var p in ports.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) sb.Append($" -p {Sanitize(p)}");
            foreach (var e in env.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.Append($" -e \"{e.Replace("\"", "")}\"");
            sb.Append(' ').Append(Sanitize(image));

            log.Report($"$ docker {sb}");
            var r = await RunAsync(sb.ToString(), ct, timeoutMs: 15 * 60 * 1000);
            var output = (r.StdOut + r.StdErr).Trim();
            log.Report(r.ExitCode == 0 ? "✔ Container started." : $"✖ {output}");
            return (r.ExitCode == 0, output);
        }

        // Only allow characters that are valid in names, images and port specs.
        private static string Sanitize(string s) => new(s.Trim().Where(c => char.IsLetterOrDigit(c) || "-_.:/@".Contains(c)).ToArray());

        private static int FindFreePort(int preferred, int exclude = -1)
        {
            var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            var port = preferred;
            while (used.Contains(port) || port == exclude) port++;
            return port;
        }

        private async Task<(bool Ok, string Output)> Simple(string args, CancellationToken ct)
        {
            var r = await RunAsync(args, ct, timeoutMs: 60000);
            return (r.ExitCode == 0, (r.StdOut + r.StdErr).Trim());
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string args, CancellationToken ct, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo("docker", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Log.Warning("[Docker] 'docker {Args}' timed out", args);
                return (-1, "", "Docker command timed out.");
            }
            return (proc.ExitCode, await stdout, await stderr);
        }
    }
}
