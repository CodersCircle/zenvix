using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Serilog;

namespace Hostix.Modules.Services
{
    public interface IDomainManager
    {
        bool RegisterDomain(string domain);
        void UnregisterDomain(string domain);
    }

    public class HostsManager : IDomainManager
    {
        private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        private const string HostixHeader = "# --- Hostix Managed Domains Start ---";
        private const string HostixFooter = "# --- Hostix Managed Domains End ---";

        public bool RegisterDomain(string domain)
        {
            try
            {
                var lines = File.ReadAllLines(HostsPath).ToList();
                var entry = $"127.0.0.1\t{domain}";

                if (lines.Contains(entry)) return true;

                // Ensure Hostix block exists
                int startIndex = lines.IndexOf(HostixHeader);
                int endIndex = lines.IndexOf(HostixFooter);

                if (startIndex == -1)
                {
                    lines.Add("");
                    lines.Add(HostixHeader);
                    lines.Add(HostixFooter);
                    startIndex = lines.Count - 2;
                    endIndex = lines.Count - 1;
                }

                lines.Insert(endIndex, entry);
                File.WriteAllLines(HostsPath, lines);
                Log.Information("Domain {Domain} registered in Windows hosts file.", domain);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Log.Warning("[HostsManager] Permission denied for hosts file. Run Hostix as ADMINISTRATOR to enable domain mapping for {Domain}.", domain);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error registering domain {Domain}", domain);
                return false;
            }
        }

        public void UnregisterDomain(string domain)
        {
            try
            {
                var lines = File.ReadAllLines(HostsPath).ToList();
                var entry = $"127.0.0.1\t{domain}";

                if (lines.Remove(entry))
                {
                    File.WriteAllLines(HostsPath, lines);
                    Log.Information("Domain {Domain} removed from Windows hosts file.", domain);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error unregistering domain {Domain}", domain);
            }
        }
    }
}
