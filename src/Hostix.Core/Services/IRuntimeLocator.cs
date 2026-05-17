using System.Collections.Generic;
using System.Threading.Tasks;
using Hostix.Core.Models;

namespace Hostix.Core.Services
{
    public interface IRuntimeLocator
    {
        /// <summary>
        /// Dynamically discovers all available runtimes on the system based on priority rules.
        /// </summary>
        Task<List<RuntimeMetadata>> DiscoverAllAsync();

        /// <summary>
        /// Finds the best available runtime for a specific service type.
        /// </summary>
        Task<RuntimeMetadata?> FindBestAsync(RuntimeServiceType type);

        /// <summary>
        /// Returns the scan logs for debugging discovery issues.
        /// </summary>
        List<string> GetDiscoveryLogs();
    }
}
