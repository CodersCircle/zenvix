using System.Collections.Specialized;

namespace Zenvix.UI.Views
{
    public partial class DeploymentView : System.Windows.Controls.UserControl
    {
        public DeploymentView()
        {
            InitializeComponent();
            // Keep the newest activity line in view.
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            };
        }
    }
}
