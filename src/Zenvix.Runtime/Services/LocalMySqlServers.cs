using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Serilog;

namespace Zenvix.Runtime.Services
{
    /// <summary>A MySQL/MariaDB installed by another stack or by the user (XAMPP, WAMP, Laragon, MySQL Server...).</summary>
    public sealed record LegacyMySqlSource(string Name, string BinDir, string DataDir, string? DefaultsFile, int Port)
    {
        public string Key => Path.GetFullPath(DataDir).TrimEnd('\\').ToLowerInvariant();
        public string? Exe(params string[] names) => names.Select(n => Path.Combine(BinDir, n)).FirstOrDefault(File.Exists);
    }

    /// <summary>Finds MySQL/MariaDB servers installed outside Zenvix: XAMPP, WAMP, Laragon and Windows services.</summary>
    public static class LocalMySqlServers
    {
        public static IReadOnlyList<LegacyMySqlSource> DetectAll(string zenvixBase)
        {
            var found = new Dictionary<string, LegacyMySqlSource>();
            void Add(LegacyMySqlSource? s)
            {
                if (s == null || !Directory.Exists(s.DataDir) || s.Exe("mysqldump.exe", "mariadb-dump.exe") == null) return;
                if (s.DataDir.StartsWith(zenvixBase, StringComparison.OrdinalIgnoreCase)) return; // Zenvix's own MySQL
                found.TryAdd(s.Key, s);
            }

            foreach (var drive in SafeDrives())
            {
                // XAMPP
                var x = Path.Combine(drive, "xampp", "mysql");
                Add(FromFolder("XAMPP", Path.Combine(x, "bin"), Path.Combine(x, "data"), Path.Combine(x, "bin", "my.ini")));

                // WAMP: wamp64\bin\mysql\mysql8.x\  and  wamp64\bin\mariadb\mariadb11.x\  (data + my.ini inside)
                foreach (var wamp in new[] { "wamp64", "wamp" })
                    foreach (var kind in new[] { "mysql", "mariadb" })
                        foreach (var ver in SafeDirs(Path.Combine(drive, wamp, "bin", kind)))
                            Add(FromFolder("WAMP", Path.Combine(ver, "bin"), Path.Combine(ver, "data"), Path.Combine(ver, "my.ini")));

                // Laragon: laragon\bin\mysql\mysql-8.x-winx64\ ; data in laragon\data\mysql-8 (or mysql)
                var laragon = Path.Combine(drive, "laragon");
                foreach (var ver in SafeDirs(Path.Combine(laragon, "bin", "mysql")))
                {
                    var name = Path.GetFileName(ver).ToLowerInvariant();
                    var major = Regex.Match(name, @"(\d+)").Groups[1].Value;
                    var engine = name.StartsWith("mariadb") ? "mariadb" : "mysql";
                    var data = new[] { $"{engine}-{major}", engine, "mysql" }
                        .Select(d => Path.Combine(laragon, "data", d)).FirstOrDefault(Directory.Exists);
                    if (data != null) Add(FromFolder("Laragon", Path.Combine(ver, "bin"), data, Path.Combine(ver, "my.ini")));
                }
            }

            // MySQL / MariaDB installed as a Windows service (MySQL Installer, MariaDB MSI, Herd/DBngin...)
            try
            {
                using var svcRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                foreach (var name in svcRoot?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var key = svcRoot!.OpenSubKey(name);
                    var image = key?.GetValue("ImagePath") as string;
                    if (image == null || !Regex.IsMatch(image, @"(mysqld|mariadbd)\.exe", RegexOptions.IgnoreCase)) continue;
                    var exe = Regex.Match(image, "^\"?([^\"]+?(mysqld|mariadbd)\\.exe)", RegexOptions.IgnoreCase).Groups[1].Value;
                    var ini = Regex.Match(image, "--defaults-file=\"?([^\"]+?\\.(ini|cnf))", RegexOptions.IgnoreCase).Groups[1].Value;
                    var bin = Path.GetDirectoryName(exe)!;
                    var dataDir = IniValue(ini, "datadir") ?? Path.Combine(Path.GetDirectoryName(bin)!, "data");
                    Add(new LegacyMySqlSource($"MySQL service '{name}'", bin, dataDir, File.Exists(ini) ? ini : null, IniPort(ini)));
                }
            }
            catch (Exception ex) { Log.Debug(ex, "[Import] service scan failed"); }

            return found.Values.ToList();
        }

        private static LegacyMySqlSource? FromFolder(string name, string bin, string data, string ini)
        {
            if (!Directory.Exists(bin)) return null;
            var dataDir = IniValue(ini, "datadir") is string d && Directory.Exists(d) ? d : data;
            return new LegacyMySqlSource(name, bin, dataDir, File.Exists(ini) ? ini : null, IniPort(ini));
        }

        private static string? IniValue(string? ini, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(ini) || !File.Exists(ini)) return null;
                string? section = null;
                foreach (var raw in File.ReadLines(ini))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[")) { section = line.Trim('[', ']').ToLowerInvariant(); continue; }
                    if (section is not ("mysqld" or "server" or "mariadb")) continue;
                    var m = Regex.Match(line, $@"^{key}\s*=\s*""?([^""#;]+)""?", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value.Trim().Replace('/', '\\');
                }
            }
            catch { }
            return null;
        }

        private static int IniPort(string? ini) => int.TryParse(IniValue(ini, "port"), out var p) ? p : 3306;

        private static IEnumerable<string> SafeDrives()
        {
            try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
            catch { return new[] { @"C:\" }; }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

    }
}
