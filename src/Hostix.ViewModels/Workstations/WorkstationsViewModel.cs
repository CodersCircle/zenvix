using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Hostix.Core.Workstations.Models;

namespace Hostix.ViewModels.Workstations
{
    public partial class WorkstationsViewModel : ObservableObject
    {
        public ObservableCollection<WorkstationProject> Projects { get; } = new();
        
        [ObservableProperty]
        private bool _isLoading;

        public WorkstationsViewModel()
        {
            // Initial placeholder data to test the new UI tab safely
            Projects.Add(new WorkstationProject 
            { 
                Name = "My New Next.js App", 
                Path = @"C:\Hostix\Projects\next-app",
                LocalDomain = "next-app.test",
                Framework = new ProjectFramework { Name = "Next.js", Type = "Node" }
            });

            Projects.Add(new WorkstationProject 
            { 
                Name = "Laravel Workstation", 
                Path = @"C:\Hostix\Projects\laravel-app",
                LocalDomain = "laravel-app.test",
                Framework = new ProjectFramework { Name = "Laravel", Type = "PHP" }
            });
        }
    }
}
