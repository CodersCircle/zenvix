using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zenvix.Modules.Deployment
{
    public interface IGitDeployService
    {
        Task<string?> GetGitVersionAsync();
        Task<bool> ConnectAsync(DeploymentProfile profile, IProgress<string> log, CancellationToken ct = default);
        Task<bool> DeployAsync(DeploymentProfile profile, string commitMessage, IProgress<string> log, CancellationToken ct = default);
    }

    /// <summary>
    /// Connects a local project to a Git remote (GitHub, GitLab, Bitbucket, own server) and pushes it.
    /// Uses the installed git CLI, so the user's saved credentials (Git Credential Manager / SSH keys) are used.
    /// </summary>
    public class GitDeployService : IGitDeployService
    {
        private const string DefaultGitIgnore =
            "# Added by Zenvix\n.env\n.env.*\n!.env.example\nnode_modules/\nvendor/\n*.log\n.idea/\n.vscode/\nstorage/logs/\n";

        public async Task<string?> GetGitVersionAsync()
        {
            try
            {
                var r = await RunGitAsync(Environment.CurrentDirectory, "--version", CancellationToken.None);
                return r.ExitCode == 0 ? r.Output.Trim() : null;
            }
            catch { return null; }
        }

        public async Task<bool> ConnectAsync(DeploymentProfile p, IProgress<string> log, CancellationToken ct = default)
        {
            if (!ValidateCommon(p, log)) return false;
            if (await GetGitVersionAsync() == null)
            {
                log.Report("✖ Git is not installed. Install it from https://git-scm.com/download/win and try again.");
                return false;
            }

            if (!Directory.Exists(Path.Combine(p.LocalPath, ".git")))
            {
                if (!await Step(p, $"init -b {Q(p.GitBranch)}", log, ct)) return false;
            }

            var gitignore = Path.Combine(p.LocalPath, ".gitignore");
            if (!File.Exists(gitignore))
            {
                await File.WriteAllTextAsync(gitignore, DefaultGitIgnore, ct);
                log.Report("Created .gitignore (keeps .env, vendor and node_modules out of the repository).");
            }

            var existing = await RunGitAsync(p.LocalPath, "remote get-url origin", ct);
            var verb = existing.ExitCode == 0 ? "set-url" : "add";
            if (!await Step(p, $"remote {verb} origin {Q(p.GitRemoteUrl)}", log, ct)) return false;

            log.Report("Checking access to the remote ...");
            var ls = await RunGitAsync(p.LocalPath, "ls-remote --heads origin", ct);
            if (ls.ExitCode != 0)
            {
                log.Report(ls.Output.Trim());
                log.Report("✖ Could not reach the repository. Check the URL and that you are signed in (Git Credential Manager will prompt on first push).");
                return false;
            }
            log.Report($"✔ Connected to {p.GitRemoteUrl}");
            return true;
        }

        public async Task<bool> DeployAsync(DeploymentProfile p, string commitMessage, IProgress<string> log, CancellationToken ct = default)
        {
            if (!ValidateCommon(p, log)) return false;
            if (!Directory.Exists(Path.Combine(p.LocalPath, ".git")) && !await ConnectAsync(p, log, ct)) return false;

            if (!await Step(p, "add -A", log, ct)) return false;

            var status = await RunGitAsync(p.LocalPath, "status --porcelain", ct);
            if (!string.IsNullOrWhiteSpace(status.Output))
            {
                var msg = string.IsNullOrWhiteSpace(commitMessage) ? $"Deploy from Zenvix {DateTime.Now:yyyy-MM-dd HH:mm}" : commitMessage;
                var commit = await RunGitAsync(p.LocalPath, $"commit -m {Q(msg)}", ct);
                log.Report(commit.Output.Trim());
                if (commit.ExitCode != 0)
                {
                    if (commit.Output.Contains("user.email") || commit.Output.Contains("user.name"))
                        log.Report("✖ Git doesn't know who you are. Run: git config --global user.name \"Your Name\" and git config --global user.email you@example.com");
                    return false;
                }
            }
            else
            {
                log.Report("No new changes to commit – pushing existing commits.");
            }

            if (!await Step(p, $"push -u origin {Q("HEAD:" + p.GitBranch)}", log, ct)) return false;
            log.Report($"🚀 Pushed to {p.GitBranch}. If your host deploys on push (Vercel, Netlify, Forge, cPanel Git, a webhook…) it will pick this up now.");
            return true;
        }

        private static bool ValidateCommon(DeploymentProfile p, IProgress<string> log)
        {
            if (!Directory.Exists(p.LocalPath)) { log.Report($"✖ Local folder not found: {p.LocalPath}"); return false; }
            if (string.IsNullOrWhiteSpace(p.GitRemoteUrl)) { log.Report("✖ Repository URL is required."); return false; }
            if (string.IsNullOrWhiteSpace(p.GitBranch)) p.GitBranch = "main";
            return true;
        }

        private static async Task<bool> Step(DeploymentProfile p, string args, IProgress<string> log, CancellationToken ct)
        {
            log.Report($"$ git {args}");
            var r = await RunGitAsync(p.LocalPath, args, ct);
            if (!string.IsNullOrWhiteSpace(r.Output)) log.Report(r.Output.TrimEnd());
            if (r.ExitCode != 0) log.Report($"✖ git exited with code {r.ExitCode}");
            return r.ExitCode == 0;
        }

        private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        private static async Task<(int ExitCode, string Output)> RunGitAsync(string cwd, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Never hang on a console prompt; GUI credential helpers still work.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, (await stdout) + (await stderr));
        }
    }
}
