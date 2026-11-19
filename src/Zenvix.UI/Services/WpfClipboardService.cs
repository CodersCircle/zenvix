using Zenvix.Core.Services;
using System.Windows;

namespace Zenvix.UI.Services
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
