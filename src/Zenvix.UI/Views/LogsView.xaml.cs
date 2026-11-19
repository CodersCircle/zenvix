using System.Collections.Specialized;

namespace Zenvix.UI.Views
{
    public partial class LogsView : System.Windows.Controls.UserControl
    {
        public LogsView()
        {
            InitializeComponent();
            // Follow the newest line, like `tail -f`.
            ((INotifyCollectionChanged)LineList.Items).CollectionChanged += (_, _) =>
            {
                if (LineList.Items.Count > 0) LineList.ScrollIntoView(LineList.Items[^1]);
            };
        }
    }
}
