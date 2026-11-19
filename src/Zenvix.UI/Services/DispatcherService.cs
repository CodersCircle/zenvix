using System;
using System.Windows;
using Zenvix.ViewModels.Services;

namespace Zenvix.UI.Services
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
