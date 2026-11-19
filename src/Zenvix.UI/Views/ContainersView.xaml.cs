using System.Collections.Specialized;

namespace Zenvix.UI.Views
{
    public partial class ContainersView : System.Windows.Controls.UserControl
    {
        public ContainersView()
        {
            InitializeComponent();
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            };
        }
    }
}
