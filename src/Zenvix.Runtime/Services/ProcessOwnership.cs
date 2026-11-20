using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Zenvix.Runtime.Services
{
    /// <summary>
    /// Decides whether a running process belongs to this Zenvix install, so cleanup code never kills
    /// another program's processes (XAMPP's MySQL, Herd's Nginx, a system MySQL service...) that merely
    /// share a name like "mysqld" or "nginx".
    /// </summary>
    public static class ProcessOwnership
    {
        private static readonly string Root =
            Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\') + "\\";

        /// <summary>True only for processes started from the Zenvix folder that are not Zenvix containers.</summary>
        public static bool IsZenvixOwned(Process p)
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null || !path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return false;
                return !ContainerPids().Contains(p.Id); // containers are the user's, kept running on purpose
            }
            catch
            {
                // Access denied (elevated/system process) or already exited: not ours to kill.
                return false;
            }
        }

        private static HashSet<int> ContainerPids()
        {
            var pids = new HashSet<int>();
            try
            {
                var store = Path.Combine(Root, "config", "containers.json");
                if (!File.Exists(store)) return pids;
                using var doc = JsonDocument.Parse(File.ReadAllText(store));
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("Pid", out var pid) && pid.ValueKind == JsonValueKind.Number)
                        pids.Add(pid.GetInt32());
            }
            catch { }
            return pids;
        }
    }
}
