using System;
using System.Windows;
using System.Windows.Media;
using MahApps.Metro.Controls;

namespace Zenvix.UI;
public partial class MainWindow : MetroWindow
{
    // The layout is designed for this content size (the default 1300x800 window minus the title bar).
    private const double DesignWidth = 1290;
    private const double DesignHeight = 770;
    private const double MinScale = 0.7;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { FitToScreen(); ApplyScale(); };
        SizeChanged += (_, _) => ApplyScale();
        StateChanged += (_, _) => ApplyScale();
    }

    /// <summary>On small screens (e.g. 1366x768, or 125% scaling) the window must fit the work area.</summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        if (Width > area.Width || Height > area.Height)
        {
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
    }

    /// <summary>
    /// Scales the whole UI (layout and fonts together) when the window is smaller than the design size,
    /// so nothing is cut off on small or high-DPI screens. Larger windows stay at 100%.
    /// </summary>
    private void ApplyScale()
    {
        if (Content is not FrameworkElement root || ActualWidth <= 0) return;
        var available = (w: ActualWidth, h: ActualHeight - TitleBarHeight);
        var scale = Math.Clamp(Math.Min(available.w / DesignWidth, available.h / DesignHeight), MinScale, 1.0);
        scale = Math.Round(scale, 2);

        if (scale >= 0.995)
        {
            if (root.LayoutTransform != Transform.Identity) root.LayoutTransform = Transform.Identity;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        }
        else if (root.LayoutTransform is not ScaleTransform current || Math.Abs(current.ScaleX - scale) > 0.004)
        {
            root.LayoutTransform = new ScaleTransform(scale, scale);
            // Scaled text stays sharp with Ideal layout; Display mode would blur when resized.
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        }
    }
}
