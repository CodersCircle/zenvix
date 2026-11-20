using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Zenvix.Runtime.Services
{
    /// <summary>
    /// Which PHP extensions are turned on (Settings → PHP extensions). Stored in config/php-extensions.json;
    /// until the user changes anything the defaults below are used.
    /// </summary>
    public static class PhpExtensions
    {
        /// <summary>What Laravel, WordPress and most PHP apps need.</summary>
        public static readonly string[] Defaults =
        {
            "curl", "mbstring", "openssl", "pdo_mysql", "mysqli", "fileinfo", "gettext", "gd", "sqlite3", "pdo_sqlite",
            "zip", "exif", "sodium", "intl", "bcmath", "ftp", "pdo_pgsql", "pgsql", "opcache",
        };

        /// <summary>Loaded with zend_extension= instead of extension=.</summary>
        private static readonly HashSet<string> Zend = new(StringComparer.OrdinalIgnoreCase) { "opcache", "xdebug" };

        /// <summary>PHP's own test DLLs, never useful to users.</summary>
        private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "dl_test", "zend_test" };

        public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bcmath"] = "Arbitrary precision math", ["bz2"] = "Bzip2 compression", ["curl"] = "HTTP requests",
            ["exif"] = "Image metadata", ["fileinfo"] = "Detect file types (uploads)", ["ftp"] = "FTP client",
            ["gd"] = "Image editing", ["gettext"] = "Translations", ["gmp"] = "Big-number math",
            ["intl"] = "Internationalisation (Laravel, Symfony)", ["ldap"] = "LDAP / Active Directory",
            ["mbstring"] = "Multibyte strings (required by Laravel)", ["mysqli"] = "MySQL (WordPress)",
            ["openssl"] = "HTTPS / encryption", ["opcache"] = "Faster PHP (bytecode cache)",
            ["pdo_mysql"] = "MySQL for Laravel / PDO", ["pdo_pgsql"] = "PostgreSQL for PDO", ["pgsql"] = "PostgreSQL",
            ["pdo_sqlite"] = "SQLite for PDO", ["sqlite3"] = "SQLite", ["soap"] = "SOAP web services",
            ["sockets"] = "Low-level sockets", ["sodium"] = "Modern cryptography", ["xsl"] = "XSL transforms",
            ["zip"] = "Zip archives (Composer)", ["tidy"] = "HTML clean-up", ["xdebug"] = "Step debugger (slower)",
            ["odbc"] = "ODBC databases", ["pdo_odbc"] = "ODBC for PDO", ["pdo_firebird"] = "Firebird for PDO",
            ["snmp"] = "SNMP", ["shmop"] = "Shared memory", ["sysvshm"] = "System V shared memory",
            ["enchant"] = "Spell checking", ["dba"] = "Key-value databases", ["ffi"] = "Call C libraries",
            ["com_dotnet"] = "COM / .NET (Windows)",
        };

        private static string Store(string? baseDir) =>
            Path.Combine(baseDir ?? AppDomain.CurrentDomain.BaseDirectory, "config", "php-extensions.json");

        /// <summary>Extensions shipped with the PHP build in <paramref name="extDir"/>, plus built-in opcache.</summary>
        public static IReadOnlyList<string> Available(string extDir)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "opcache" }; // built into PHP 8.5+, a DLL before
            if (Directory.Exists(extDir))
                foreach (var f in Directory.GetFiles(extDir, "php_*.dll"))
                {
                    var n = Path.GetFileNameWithoutExtension(f)[4..];
                    if (!Hidden.Contains(n)) names.Add(n);
                }
            return names.ToList();
        }

        public static HashSet<string> Enabled(string? baseDir = null)
        {
            try
            {
                var f = Store(baseDir);
                if (File.Exists(f))
                    return new HashSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(f)) ?? Defaults, StringComparer.OrdinalIgnoreCase);
            }
            catch { /* corrupt file: fall back to defaults */ }
            return new HashSet<string>(Defaults, StringComparer.OrdinalIgnoreCase);
        }

        public static void SetEnabled(string name, bool on, string? baseDir = null)
        {
            var set = Enabled(baseDir);
            if (on) set.Add(name); else set.Remove(name);
            var f = Store(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(set.OrderBy(x => x).ToArray()));
        }

        /// <summary>
        /// php.ini lines for the enabled extensions that exist in <paramref name="extDir"/>. Each is loaded once;
        /// extensions compiled into PHP (e.g. opcache on 8.5+) have no DLL and need no line.
        /// </summary>
        public static string IniLines(string extDir, IEnumerable<string>? alsoRequired = null, bool includeZend = true, string? baseDir = null)
        {
            var wanted = Enabled(baseDir);
            if (alsoRequired != null) wanted.UnionWith(alsoRequired);
            var sb = new StringBuilder();
            foreach (var ext in wanted.OrderBy(x => x))
            {
                if (!File.Exists(Path.Combine(extDir, $"php_{ext}.dll"))) continue;
                if (Zend.Contains(ext)) { if (includeZend) sb.AppendLine($"zend_extension={ext}"); }
                else sb.AppendLine($"extension={ext}");
            }
            return sb.ToString();
        }
    }
}
