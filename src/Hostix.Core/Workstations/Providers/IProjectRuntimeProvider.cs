using System.Threading.Tasks;
using Hostix.Core.Workstations.Models;

namespace Hostix.Core.Workstations.Providers
{
    public interface IProjectRuntimeProvider
    {
        string ProviderName { get; }
        
        bool CanHandle(string projectDirectory);
        ProjectFramework DetectFramework(string projectDirectory);
        
        Task StartProjectAsync(WorkstationProject project);
        Task StopProjectAsync(WorkstationProject project);
        Task ConfigureLocalDomainAsync(WorkstationProject project, string domainExtension = ".test");
    }
}
