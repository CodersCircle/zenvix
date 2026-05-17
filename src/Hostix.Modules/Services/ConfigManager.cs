using System;
using System.IO;
using Serilog;

namespace Hostix.Modules.Services
{
    public interface IConfigManager
    {
        void SaveConfig(string path, string content);
        void RestoreLastGoodConfig(string path);
    }

    public class ConfigManager : IConfigManager
    {
        public void SaveConfig(string path, string content)
        {
            try
            {
                // Create backup before overwrite
                if (File.Exists(path))
                {
                    var backupPath = path + ".bak";
                    File.Copy(path, backupPath, true);
                    Log.Debug("Created config backup: {Backup}", backupPath);
                }

                File.WriteAllText(path, content);
                Log.Information("Saved config: {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save config: {Path}", path);
                RestoreLastGoodConfig(path);
            }
        }

        public void RestoreLastGoodConfig(string path)
        {
            var backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, path, true);
                Log.Warning("Restored last known good configuration for: {Path}", path);
            }
            else
            {
                Log.Error("No backup found to restore for: {Path}", path);
            }
        }
    }
}
