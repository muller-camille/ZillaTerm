using System.IO;
using System.Windows;
using System.Windows.Controls;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Historique d'une liste partagée : journal des modifications (qui, quand, quoi) et versions enregistrées, chacune
/// restaurable (la restauration est elle-même une nouvelle révision, donc annulable).
/// </summary>
public partial class SharedHistoryDialog : Window
{
    private readonly SharedServerList _list;
    private readonly Func<SharedVersion, Task<int?>> _restore;

    /// <param name="restore">Restauration d'une version ; null si elle a échoué (l'erreur est déjà montrée).</param>
    public SharedHistoryDialog(SharedServerList list, Func<SharedVersion, Task<int?>> restore)
    {
        InitializeComponent();
        _list = list;
        _restore = restore;
        Title = Text.Format(Strings.SharedHistoryTitle, list.Name);
        VersionsNote.Text = Text.Format(Strings.SharedHistoryVersionsNote, list.VersionsDirectory);
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Lignes du journal, la plus récente d'abord.</summary>
    internal IReadOnlyList<ChangeRow> Changes { get; private set; } = [];

    /// <summary>Versions enregistrées, la plus récente d'abord.</summary>
    internal IReadOnlyList<VersionRow> Versions { get; private set; } = [];

    /// <summary>Relit la liste et ses versions (partage réseau : hors du fil de l'interface).</summary>
    internal async Task LoadAsync()
    {
        SetStatus(Strings.SharedHistoryLoading);
        List<SharedVersion> versions;
        string? versionsError = null;
        try
        {
            versions = await Task.Run(() =>
            {
                _list.Load();
                try
                {
                    return _list.Versions();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    versionsError = e.Message;
                    return [];
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(e.Message, error: true);
            return;
        }

        var content = _list.Content;
        var journal = content?.Changes ?? [];
        Changes = journal.AsEnumerable().Reverse().Select(c => new ChangeRow(c)).ToList();
        Versions = versions.Select(v => new VersionRow(v, journal.Where(c => c.Revision == v.Revision).ToList())).ToList();
        ChangesGrid.ItemsSource = Changes;
        VersionsGrid.ItemsSource = Versions;
        HeaderText.Text = content is null
            ? _list.Path
            : Text.Format(Strings.SharedHistoryHeader, _list.Path, content.Revision, content.Servers.Count);
        if (_list.Error is { } error)
        {
            SetStatus(error, error: true);
        }
        else if (versionsError is not null)
        {
            SetStatus(Text.Format(Strings.SharedHistoryNoVersions, versionsError), error: true);
        }
        else
        {
            SetStatus("");
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    // Restauration en cours : « Restaurer » reste grisé, même si une autre version est sélectionnée entre-temps (deux
    // restaurations se croiseraient, la dernière écrite l'emportant).
    private bool _restoring;

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e) => UpdateRestoreButton();

    private void UpdateRestoreButton() =>
        RestoreButton.IsEnabled = !_restoring && VersionsGrid.SelectedItem is VersionRow && _list.Content?.IsShared == true;

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_restoring || VersionsGrid.SelectedItem is not VersionRow row)
        {
            return;
        }

        if (!ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Title,
                Heading = Text.Format(Strings.SharedRestoreHeading, _list.Name, row.Revision),
                Subject = row.Date,
                Bullets = [Strings.SharedRestoreEffect, Strings.SharedRestoreUndo],
                Kind = ConfirmKind.Warning,
                Actions = [Strings.SharedRestoreAction],
            }))
        {
            return;
        }

        await RestoreAsync(row);
    }

    /// <summary>Restaure la version (déjà confirmée), puis relit l'historique.</summary>
    internal async Task RestoreAsync(VersionRow row)
    {
        _restoring = true;
        UpdateRestoreButton();
        try
        {
            if (await _restore(row.Version) is not null)
            {
                await LoadAsync();
                SetStatus(Text.Format(Strings.SharedRestored, row.Revision));
            }
        }
        finally
        {
            _restoring = false;
            UpdateRestoreButton();
        }
    }

    /// <summary>Ligne du journal.</summary>
    internal sealed class ChangeRow(SharedChange change)
    {
        public int Revision { get; } = change.Revision;

        public string Date { get; } = SharedText.Date(change.At);

        public string By { get; } = change.By;

        public string Action { get; } = SharedText.Action(change);

        public string Server { get; } = change.Server ?? (change.Action == SharedAction.Created ? change.Detail : null) ?? "";

        public string Folder { get; } = change.Folder ?? "";
    }

    /// <summary>Version enregistrée, avec l'auteur et le résumé des modifications de cette révision.</summary>
    internal sealed class VersionRow(SharedVersion version, List<SharedChange> changes)
    {
        private const int MaxNamed = 6;

        public SharedVersion Version { get; } = version;

        public int Revision => Version.Revision;

        public string Date { get; } = SharedText.Date(version.Saved);

        public string By { get; } = string.Join(", ", changes.Select(c => c.By).Distinct(StringComparer.Ordinal));

        public string Summary { get; } = Summarize(changes);

        private static string Summarize(List<SharedChange> changes)
        {
            var parts = changes.Select(c => c.Action switch
            {
                SharedAction.Added => "+ " + c.Server,
                SharedAction.Removed => "− " + c.Server,
                _ => SharedText.Action(c),
            }).ToList();
            return parts.Count <= MaxNamed
                ? string.Join(", ", parts)
                : string.Join(", ", parts.Take(MaxNamed)) + Text.Format(Strings.AndMore, parts.Count - MaxNamed);
        }
    }
}
