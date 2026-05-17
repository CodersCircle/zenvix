using Hostix.Core.Services;
using System.Windows;

namespace Hostix.UI.Services
{
    public class WpfClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                System.Windows.Clipboard.SetText(text);
            }
        }
    }
}
