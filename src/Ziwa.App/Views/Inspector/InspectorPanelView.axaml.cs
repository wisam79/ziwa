using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Ziwa.App.Views.Inspector;

public partial class InspectorPanelView : UserControl
{
    public InspectorPanelView()
    {
        InitializeComponent();
        InspectorTabs.AddHandler(KeyDownEvent, OnTabHeaderKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnTabHeaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TabItem { IsFocused: true } header || e.KeyModifiers != KeyModifiers.None ||
            e.Key is not (Key.Left or Key.Right))
        {
            return;
        }

        int count = InspectorTabs.Items.Count;
        bool forward = (e.Key == Key.Left) == (FlowDirection == FlowDirection.RightToLeft);
        int next = (InspectorTabs.Items.IndexOf(header) + (forward ? 1 : -1) + count) % count;
        if (InspectorTabs.Items[next] is TabItem tab)
        {
            tab.IsSelected = true;
            tab.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    internal void ScrollToPreflight() => PageSettings.ScrollToPreflight();
}
