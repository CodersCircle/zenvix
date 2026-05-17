using System;
using System.IO;
using System.Collections.Generic;
using Serilog;

namespace Hostix.Modules.Services
{
    public interface IEnvironmentManager
    {
        void BackupEnv(string projectPath);
        void RestoreEnv(string projectPath, string backupName);
        void SetEnvValue(string projectPath, string key, string value);
    }

    public class EnvironmentManager : IEnvironmentManager
    {
        public void BackupEnv(string projectPath)
        {
            var envPath = Path.Combine(projectPath, ".env");
            if (!File.Exists(envPath)) return;

            var backupDir = Path.Combine(projectPath, ".zenvix", "backups");
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupDir, $".env.backup_{timestamp}");

            File.Copy(envPath, backupPath);
            Log.Information("Backup created for {Project}: {BackupName}", Path.GetFileName(projectPath), Path.GetFileName(backupPath));
        }

        public void RestoreEnv(string projectPath, string backupName)
        {
            var backupPath = Path.Combine(projectPath, ".zenvix", "backups", backupName);
            var envPath = Path.Combine(projectPath, ".env");

            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, envPath, true);
                Log.Information("Restored .env from {BackupName}", backupName);
            }
        }

        public void SetEnvValue(string projectPath, string key, string value)
        {
            var envPath = Path.Combine(projectPath, ".env");
            if (!File.Exists(envPath)) return;

            var lines = File.ReadAllLines(envPath);
            bool found = false;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith($"{key}="))
                {
                    lines[i] = $"{key}={value}";
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                var newLines = new List<string>(lines);
                newLines.Add($"{key}={value}");
                lines = newLines.ToArray();
            }

            File.WriteAllLines(envPath, lines);
            Log.Information("Updated .env: {Key}={Value}", key, value);
        }
    }
}
