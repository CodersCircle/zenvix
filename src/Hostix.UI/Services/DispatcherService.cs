using System;
using System.Windows;
using Hostix.ViewModels.Services;

namespace Hostix.UI.Services
{
    public class DispatcherService : IDispatcherService
    {
        public void Invoke(Action action)
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(action);
        }

        public void BeginInvoke(Action action)
        {
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(action);
        }
    }
}
