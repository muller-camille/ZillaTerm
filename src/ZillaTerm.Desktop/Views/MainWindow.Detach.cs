using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;

namespace ZillaTerm.App.Views;

/// <summary>
/// Onglets SSH détachés dans des fenêtres séparées (autre écran) : le terminal est déplacé dans la fenêtre, l'onglet
/// garde sa place avec « Afficher la fenêtre » et « Ramener dans l'onglet », et l'onglet Fichiers continue de
/// travailler sur la session quand cet onglet est sélectionné. Les onglets Bureau à distance ne se détachent pas :
/// leur contrôle est une fenêtre Win32 d'un autre thread, que déplacer risquerait de couper la session.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<SshSession, DetachedSessionWindow> _detached = [];

    /// <summary>Terminal d'un onglet SSH, qu'il soit dans l'onglet, dans une fenêtre séparée ou dans la vue parallèle.</summary>
    private SshSessionView? SshViewOf(TabItem tab) =>
        tab.Content as SshSessionView
        ?? (tab.Tag is SshSession session
            ? _detached.TryGetValue(session, out var window) ? window.View : _parallel?.ViewOf(session)
            : null);

    /// <param name="screenPoint">Position du curseur (pixels de l'écran) quand l'onglet est glissé hors de la fenêtre.</param>
    private void DetachTab(TabItem tab, PixelPoint? screenPoint = null)
    {
        if (tab.Tag is ParallelView)
        {
            DetachParallel(screenPoint);
            return;
        }

        if (tab.Tag is not SshSession session || tab.Content is not SshSessionView view || _detached.ContainsKey(session))
        {
            return;
        }

        double width = Math.Max(view.Bounds.Width, 640);
        double height = Math.Max(view.Bounds.Height, 400);
        tab.Content = DetachedPlaceholder(session);
        var window = new DetachedSessionWindow(view, session.Label, Icon) { Width = width, Height = height };
        if (screenPoint is { } point)
        {
            // L'onglet suit le curseur : la fenêtre s'ouvre sous lui, sur l'écran où il a été lâché (quelle que soit son
            // échelle).
            ScreenPlacement.OpenAt(window, point, -80, -15);
        }
        else
        {
            ScreenPlacement.OpenBeside(window, this);
        }

        window.Closed += (_, _) =>
        {
            _detached.Remove(session);
            if (window.SessionClosed)
            {
                return;
            }

            // Fenêtre fermée par l'utilisateur : le terminal revient dans son onglet, la session continue.
            var back = window.TakeView();
            if (MainTabs.Items.Contains(tab))
            {
                tab.Content = back;
                MarkDetached(tab, false);
                MainTabs.SelectedItem = tab;
                back.FocusTerminal();
            }
        };
        _detached[session] = window;
        MarkDetached(tab, true);
        window.Show();
        window.Activate();
    }

    /// <summary>Ferme la fenêtre séparée d'une session qui se termine (onglet fermé, application fermée).</summary>
    private void CloseDetachedWindow(SshSession session)
    {
        if (_detached.TryGetValue(session, out var window))
        {
            window.SessionClosed = true;
            window.Close();
        }
    }

    private void CloseAllDetachedWindows()
    {
        foreach (var session in _detached.Keys.ToList())
        {
            CloseDetachedWindow(session);
        }
    }

    /// <summary>Contenu de l'onglet pendant que le terminal est dans une fenêtre séparée.</summary>
    private Control DetachedPlaceholder(SshSession session)
    {
        var show = new Button { Content = Strings.DetachedShow, Padding = new Thickness(10, 3, 10, 3) };
        show.Click += (_, _) =>
        {
            if (_detached.TryGetValue(session, out var window))
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            }
        };
        var back = new Button { Content = Strings.DetachedReattach, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        back.Click += (_, _) =>
        {
            if (_detached.TryGetValue(session, out var window))
            {
                window.Close();
            }
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = Strings.DetachedPlaceholder, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
                },
                Palette.Muted(new TextBlock
                {
                    Text = Strings.DetachedHint, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
                }),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { show, back } },
            },
        };
    }

    /// <summary>En-tête en italique (et info-bulle) pendant que la session est dans une fenêtre séparée.</summary>
    private static void MarkDetached(TabItem tab, bool detached)
    {
        if (tab.Header is Panel header && header.Children.OfType<TextBlock>().FirstOrDefault() is { } title)
        {
            title.FontStyle = detached ? FontStyle.Italic : FontStyle.Normal;
            ToolTip.SetTip(title, detached ? Strings.DetachedPlaceholder : null);
        }
    }

    /// <summary>
    /// Glisser l'en-tête d'un onglet SSH hors de la fenêtre le détache à l'endroit où le curseur sort (autre écran).
    /// </summary>
    private void EnableDragToDetach(TabItem tab, Control header)
    {
        // Distance avant de commencer à glisser : trois fois le seuil habituel (4 pixels), comme sous WPF.
        const double DragStart = 12;
        Point? pressed = null;
        bool dragging = false;
        header.AddHandler(PointerPressedEvent, (_, e) =>
        {
            // Pas depuis le bouton de fermeture.
            pressed = tab.Tag is SshSession or ParallelView && e.GetCurrentPoint(header).Properties.IsLeftButtonPressed
                      && !IsInButton(e.Source as Visual, header) ? e.GetPosition(this) : null;
            dragging = false;
        }, RoutingStrategies.Tunnel);
        header.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (pressed is not { } start || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                pressed = null;
                return;
            }

            var position = e.GetPosition(this);
            if (!dragging && (Math.Abs(position.X - start.X) > DragStart || Math.Abs(position.Y - start.Y) > DragStart))
            {
                dragging = true;
                e.Pointer.Capture(header);
            }

            if (dragging && (position.X < 0 || position.Y < 0 || position.X > Bounds.Width || position.Y > Bounds.Height))
            {
                pressed = null;
                dragging = false;
                e.Pointer.Capture(null);
                DetachTab(tab, this.PointToScreen(position));
            }
        }, RoutingStrategies.Tunnel);
        header.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            pressed = null;
            if (dragging)
            {
                dragging = false;
                e.Pointer.Capture(null);
            }
        }, RoutingStrategies.Tunnel);
        header.PointerCaptureLost += (_, _) =>
        {
            pressed = null;
            dragging = false;
        };
    }

    private static bool IsInButton(Visual? element, Visual stop)
    {
        for (var current = element; current is not null && current != stop; current = current.GetVisualParent())
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }
}
