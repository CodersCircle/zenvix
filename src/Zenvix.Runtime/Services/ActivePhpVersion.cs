using System;
using System.IO;

namespace Zenvix.Runtime.Services
{
    /// <summary>
    /// The PHP version the user chose in Settings. Without a choice Zenvix uses the newest installed PHP,
    /// but installing a new PHP must not silently move every project onto it (e.g. PHP 8.6 breaks older Laravel).
    /// </summary>
    public static class ActivePhpVersion
    {
        private static string PinFile(string baseDir) => Path.Combine(baseDir, "config", "php-version.txt");

        public static string? Get(string? baseDir = null)
        {
            try
            {
                var f = PinFile(baseDir ?? AppDomain.CurrentDomain.BaseDirectory);
                if (!File.Exists(f)) return null;
                var v = File.ReadAllText(f).Trim();
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }

        public static void Set(string version, string? baseDir = null)
        {
            var f = PinFile(baseDir ?? AppDomain.CurrentDomain.BaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, version.Trim());
        }
    }
}
