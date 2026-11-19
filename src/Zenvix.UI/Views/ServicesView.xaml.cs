using System.Windows.Controls;

namespace Zenvix.UI.Views
{
    public partial class ServicesView : System.Windows.Controls.UserControl
    {
        private static readonly object _syncLock = new();

        public ServicesView()
        {
            InitializeComponent();
        }
    }
}
