using System;
using System.Threading.Tasks;
using Hostix.Core.Models;

namespace Hostix.Core.Services
{
    public interface IRuntimeInstaller
    {
        /// <summary>
        /// Checks if the required runtime is installed internally.
        /// </summary>
        bool IsInstalled(RuntimeServiceType type, string version);

        /// <summary>
        /// Downloads and extracts the portable runtime to the internal runtimes directory.
        /// </summary>
        Task<bool> InstallAsync(RuntimeServiceType type, string version, IProgress<double>? progress = null);

        /// <summary>
        /// Ensures the internal directory structure is ready for runtimes.
        /// </summary>
        void EnsureStructure();

        /// <summary>
        /// Gets the absolute path to the internal runtime directory for a specific type/version.
        /// </summary>
        string GetRuntimeDirectory(RuntimeServiceType type, string version);

        /// <summary>
        /// Returns the detailed log of the last installation attempt.
        /// </summary>
        string GetLastReport();

        /// <summary>
        /// Fires when a new log entry is added during installation.
        /// </summary>
        event Action<string>? LogMessage;
    }
}
