using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;

namespace ZillaTerm.App.Views;

/// <summary>
/// Vue parallèle : un onglet qui affiche jusqu'à 8 sessions SSH ensemble (voir <see cref="ParallelView"/>). Les
/// terminaux y sont déplacés ; l'onglet de chaque session garde sa place (« Afficher la vue parallèle », « Ramener dans
/// l'onglet »). Fermer la vue rend les terminaux à leurs onglets, sans fermer les sessions. Les onglets Bureau à
/// distance n'y vont pas, pour la même raison qu'ils ne se détachent pas.
/// </summary>
public partial class MainWindow
{
    private TabItem? _parallelTab;
    private ParallelView? _parallel;

    private void OnParallel(object? sender, RoutedEventArgs e) => _ = ChooseParallelSessionsAsync();

    private TabItem? TabOf(SshSession session) => MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, session));

    /// <summary>Fenêtre de choix des sessions de la vue parallèle.</summary>
    private async Task ChooseParallelSessionsAsync(SshSession? preselect = null)
    {
        var sessions = MainTabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<SshSession>().ToList();
        if (sessions.Count == 0)
        {
            await ConfirmDialog.ShowMessageAsync(this, Strings.ParallelNoSession, ConfirmKind.Info, title: Strings.ParallelTitle);
            return;
        }

        var selected = _parallel?.Sessions.ToList() ?? [];
        if (preselect is not null && !selected.Contains(preselect))
        {
            selected.Add(preselect);
        }
        else if (selected.Count == 0 && MainTabs.SelectedItem is TabItem { Tag: SshSession current })
        {
            selected.Add(current);
        }

        var dialog = new ParallelDialog(sessions, selected);
        if (await dialog.ShowDialogAsync(this))
        {
            SetParallelSessions(dialog.Selected);
        }
    }

    /// <summary>Place exactement ces sessions dans la vue parallèle (aucune : la vue se ferme).</summary>
    private void SetParallelSessions(IReadOnlyList<SshSession> sessions)
    {
        if (_parallel is not null)
        {
            foreach (var session in _parallel.Sessions.Except(sessions).ToList())
            {
                ReturnFromParallel(session);
            }
        }

        if (sessions.Count == 0)
        {
            CloseParallel();
            return;
        }

        EnsureParallelTab();
        foreach (var session in sessions)
        {
            MoveToParallel(session);
        }

        UpdateParallelHeader();
        ShowParallel();
    }

    /// <summary>Affiche la vue parallèle : son onglet, ou sa fenêtre séparée.</summary>
    private void ShowParallel()
    {
        MainTabs.SelectedItem = _parallelTab;
        _parallelWindow?.Activate();
    }

    private void MoveToParallel(SshSession session)
    {
        if (_parallel is null || _parallel.Contains(session) || _parallel.IsFull || TabOf(session) is not { } tab)
        {
            return;
        }

        // Session détachée : son terminal revient d'abord dans l'onglet (la fenêtre séparée se ferme).
        if (_detached.TryGetValue(session, out var window))
        {
            window.Close();
        }

        if (tab.Content is not SshSessionView view)
        {
            return;
        }

        tab.Content = ParallelPlaceholder(session);
        MarkInParallel(tab, true);
        _parallel.Add(view);
    }

    /// <summary>Le terminal quitte la vue parallèle et retrouve son onglet.</summary>
    private void ReturnFromParallel(SshSession session)
    {
        if (_parallel?.Remove(session) is { } view && TabOf(session) is { } tab)
        {
            tab.Content = view;
            MarkInParallel(tab, false);
        }
    }

    /// <summary>Ferme la vue parallèle : chaque terminal retourne dans son onglet, les sessions continuent.</summary>
    private void CloseParallel()
    {
        if (_parallel is null)
        {
            return;
        }

        CloseParallelWindow();

        foreach (var session in _parallel.Sessions.ToList())
        {
            ReturnFromParallel(session);
        }

        var tab = _parallelTab;
        _parallel = null;
        _parallelTab = null;
        if (tab is not null)
        {
            bool selected = ReferenceEquals(MainTabs.SelectedItem, tab);
            MainTabs.Items.Remove(tab);
            if (selected || MainTabs.SelectedItem is null)
            {
                MainTabs.SelectedItem = SessionTabs().LastOrDefault() ?? HomeTab;
            }
        }
    }

    /// <summary>La session se ferme (onglet fermé) : elle quitte la vue, qui se ferme si elle est vide.</summary>
    private void DropFromParallel(SshSession session)
    {
        if (_parallel?.Contains(session) != true)
        {
            return;
        }

        _parallel.Remove(session);
        if (_parallel.Sessions.Count == 0)
        {
            CloseParallel();
        }
        else
        {
            UpdateParallelHeader();
        }
    }

    /// <summary>Menu de l'onglet : ajouter la session à la vue parallèle, ou l'en retirer.</summary>
    private void ToggleParallel(SshSession session)
    {
        if (_parallel?.Contains(session) == true)
        {
            ReturnFromParallel(session);
            if (_parallel.Sessions.Count == 0)
            {
                CloseParallel();
            }
            else
            {
                UpdateParallelHeader();
            }

            if (TabOf(session) is { } tab)
            {
                MainTabs.SelectedItem = tab;
            }
        }
        else if (_parallel is null)
        {
            _ = ChooseParallelSessionsAsync(session);
        }
        else if (_parallel.IsFull)
        {
            _ = ConfirmDialog.ShowMessageAsync(this, Strings.ParallelFull, ConfirmKind.Info, title: Strings.ParallelTitle);
        }
        else
        {
            MoveToParallel(session);
            UpdateParallelHeader();
            ShowParallel();
        }
    }

    private void EnsureParallelTab()
    {
        if (_parallel is not null)
        {
            return;
        }

        var view = new ParallelView();
        view.RemoveRequested += session =>
        {
            ReturnFromParallel(session);
            if (view.Sessions.Count == 0)
            {
                CloseParallel();
            }
            else
            {
                UpdateParallelHeader();
            }
        };
        view.CloseRequested += CloseParallel;
        view.DetachRequested += () =>
        {
            if (view.IsDetached)
            {
                ReattachParallel();
            }
            else
            {
                DetachParallel();
            }
        };
        view.ChooseRequested += () => _ = ChooseParallelSessionsAsync();
        view.SendFilesRequested += () => FilesPanel.ShowMultiUpload(view.Sessions.ToList());
        view.ActiveSessionChanged += session =>
        {
            if (ReferenceEquals(MainTabs.SelectedItem, _parallelTab))
            {
                FilesPanel.Attach(session);
            }
        };

        var tab = new TabItem { Content = view, Tag = view };
        var close = new Button
        {
            Theme = this.FindResource("TabCloseButton") as ControlTheme,
            Content = Palette.Icon("IconClose", 11),
        };
        ToolTip.SetTip(close, Strings.ParallelCloseTip);
        Avalonia.Automation.AutomationProperties.SetName(close, Strings.ParallelCloseTip);
        close.Click += (_, _) => CloseParallel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        ToolTip.SetTip(header, Strings.ParallelCloseTip);
        var icon = Palette.Icon("IconParallel", 16);
        icon.Margin = new Thickness(0, 0, 6, 0);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        header.Children.Add(close);
        header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                CloseParallel();
            }
        };
        var detach = new MenuItem { Header = Strings.MenuTabDetach };
        detach.Click += (_, _) => DetachParallel();
        var closeView = new MenuItem { Header = Strings.ParallelClose };
        closeView.Click += (_, _) => CloseParallel();
        header.ContextMenu = new ContextMenu { Items = { detach, new Separator(), closeView } };
        header.ContextMenu.Opened += (_, _) => detach.IsEnabled = _parallelWindow is null;
        tab.Header = header;
        EnableDragToDetach(tab, header);
        _parallel = view;
        _parallelTab = tab;
        MainTabs.Items.Add(tab);
    }

    private void UpdateParallelHeader()
    {
        if (_parallelTab?.Header is Panel header && header.Children.OfType<TextBlock>().FirstOrDefault() is { } title && _parallel is not null)
        {
            title.Text = Text.Format(Strings.ParallelTabHeader, _parallel.Sessions.Count);
        }

        if (_parallelWindow is { } window)
        {
            window.Title = ParallelWindowTitle();
        }
    }

    /// <summary>Contenu de l'onglet d'une session affichée dans la vue parallèle.</summary>
    private Control ParallelPlaceholder(SshSession session)
    {
        var show = new Button { Content = Strings.ParallelShowView, Padding = new Thickness(10, 3, 10, 3) };
        show.Click += (_, _) =>
        {
            if (_parallelTab is not null)
            {
                ShowParallel();
            }
        };
        var back = new Button { Content = Strings.DetachedReattach, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        back.Click += (_, _) => ToggleParallel(session);

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = Strings.ParallelPlaceholder, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
                },
                Palette.Muted(new TextBlock
                {
                    Text = Strings.ParallelPlaceholderHint, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
                }),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { show, back } },
            },
        };
    }

    /// <summary>En-tête en italique (et info-bulle) pendant que la session est dans la vue parallèle.</summary>
    private static void MarkInParallel(TabItem tab, bool inParallel)
    {
        if (tab.Header is Panel header && header.Children.OfType<TextBlock>().FirstOrDefault() is { } title)
        {
            title.FontStyle = inParallel ? FontStyle.Italic : FontStyle.Normal;
            ToolTip.SetTip(title, inParallel ? Strings.ParallelPlaceholder : null);
        }
    }
}
