using System;
using System.Windows;

namespace Hostix.ViewModels.Services
{
    public interface IDispatcherService
    {
        void Invoke(Action action);
        void BeginInvoke(Action action);
    }
}
