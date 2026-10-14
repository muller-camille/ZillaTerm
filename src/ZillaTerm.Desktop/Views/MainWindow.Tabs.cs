using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Views;

/// <summary>
/// Bande des onglets de la zone principale : quand les onglets ne tiennent plus, elle défile (molette, onglet choisi
/// ramené à l'écran) et le bouton « ⌄ » liste tous les onglets avec l'état de leur session.
/// </summary>
public partial class MainWindow
{
    private ScrollViewer? _tabScroll;
    private Button? _tabListButton;

    /// <summary>Branche la bande d'onglets une fois le modèle du TabControl appliqué.</summary>
    private void SetUpTabStrip()
    {
        MainTabs.ApplyTemplate();
        _tabScroll = MainTabs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "TabScroll");
        _tabListButton = MainTabs.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TabListButton");
        if (_tabScroll is null || _tabListButton is null)
        {
            return;
        }

        ToolTip.SetTip(_tabListButton, Strings.TabListTip);
        Avalonia.Automation.AutomationProperties.SetName(_tabListButton, Strings.TabListTip);
        _tabListButton.Click += (_, _) => ShowTabList();
        _tabScroll.ScrollChanged += (_, _) => _tabListButton.IsVisible = _tabScroll.Extent.Width - _tabScroll.Viewport.Width > 0.5;
        _tabScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (_tabScroll.Extent.Width > _tabScroll.Viewport.Width)
            {
                _tabScroll.Offset = _tabScroll.Offset.WithX(Math.Max(0, _tabScroll.Offset.X - e.Delta.Y * 48));
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>L'onglet choisi reste visible dans la bande, même au clavier (Ctrl+Tab) ou depuis la liste.</summary>
    private void BringTabIntoView(TabItem? tab)
    {
        if (tab is not null)
        {
            Dispatcher.UIThread.Post(() => tab.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Liste de tous les onglets (nom et état de la session) ; choisir une entrée affiche l'onglet.</summary>
    private void ShowTabList()
    {
        if (_tabListButton is null)
        {
            return;
        }

        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        foreach (var tab in MainTabs.Items.OfType<TabItem>().Where(t => t.IsVisible))
        {
            var text = tab.Header switch
            {
                SessionTabHeader session => $"{session.Label} · {SessionTabHeader.StateText(session.State)}",
                Panel panel => panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
                TextBlock block => block.Text ?? "",
                _ => tab.Header?.ToString() ?? "",
            };
            // Le nom ne passe pas par les touches d'accès (« _ » des noms de comptes).
            var item = new MenuItem
            {
                Header = new TextBlock { Text = text },
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = tab == MainTabs.SelectedItem,
            };
            item.Click += (_, _) => MainTabs.SelectedItem = tab;
            menu.Items.Add(item);
        }

        menu.Open(_tabListButton);
    }
}
