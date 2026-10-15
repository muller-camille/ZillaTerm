using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;

namespace ZillaTerm.App.Views;

/// <summary>
/// Envoi des mêmes fichiers vers plusieurs serveurs : fichiers ou dossiers locaux, dossier de destination (« ~ » = le
/// dossier personnel de chaque compte), sessions SSH destinataires. Chaque serveur devient un élément de la file des
/// transferts, vérifié par SHA-256 comme un envoi ordinaire.
/// </summary>
public partial class MultiUploadDialog : DialogWindow
{
    private readonly List<(RemoteSession Session, CheckBox Box)> _servers = [];

    public MultiUploadDialog()
    {
        InitializeComponent();
        FilesList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FilesList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <param name="sessions">Sessions SSH ouvertes ; seules les connectées peuvent être choisies.</param>
    /// <param name="selected">Sessions cochées au départ.</param>
    /// <param name="destination">Dossier proposé (celui affiché dans l'onglet Fichiers, ou « ~ »).</param>
    /// <param name="protocol">Protocole d'envoi des Paramètres (SCP ou SFTP), rappelé dans la fenêtre.</param>
    public MultiUploadDialog(IReadOnlyList<RemoteSession> sessions, IReadOnlyCollection<RemoteSession> selected, string destination,
        string protocol, IEnumerable<string>? paths = null)
        : this()
    {
        DestinationBox.Text = destination;
        ProtocolText.Text = Text.Format(Strings.MultiUploadProtocol, protocol);
        foreach (var session in sessions)
        {
            bool connected = session.State == RemoteSessionState.Connected;
            var box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = connected ? session.Label : Text.Format(Strings.MultiUploadDisconnected, session.Label),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                IsChecked = connected && selected.Contains(session),
                IsEnabled = connected,
                Margin = new Thickness(0, 2, 0, 2),
            };
            _servers.Add((session, box));
            ServersPanel.Children.Add(box);
        }

        foreach (var path in paths ?? [])
        {
            AddPath(path);
        }

        FocusLater(DestinationBox);
    }

    /// <summary>Fichiers et dossiers locaux à envoyer.</summary>
    public IReadOnlyList<string> Paths => FilesList.Items.OfType<string>().ToList();

    public string Destination => (DestinationBox.Text ?? "").Trim();

    /// <summary>Sessions destinataires, dans l'ordre des onglets.</summary>
    public IReadOnlyList<RemoteSession> Sessions => _servers.Where(s => s.Box.IsChecked == true).Select(s => s.Session).ToList();

    internal void AddPath(string path)
    {
        if (!FilesList.Items.OfType<string>().Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            FilesList.Items.Add(path);
        }
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        foreach (var file in await Pickers.OpenFilesAsync(this, title: Strings.MultiUploadAddFiles.Replace("_", "")))
        {
            AddPath(file);
        }
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        // Plusieurs dossiers à la fois quand le système le permet (choix multiple de WPF).
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = Strings.MultiUploadAddFolder.Replace("_", ""),
            AllowMultiple = true,
        });
        foreach (var folder in folders.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            AddPath(folder);
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        foreach (var item in FilesList.SelectedItems?.OfType<string>().ToList() ?? [])
        {
            FilesList.Items.Remove(item);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        // Fichiers et dossiers de ce poste seulement (chemins locaux).
        foreach (var path in e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? [])
        {
            AddPath(path);
        }
    }

    private void OnSend(object? sender, RoutedEventArgs e)
    {
        string? error = Paths.Count == 0 ? Strings.MultiUploadNoFile
            : Paths.FirstOrDefault(p => !File.Exists(p) && !Directory.Exists(p)) is { } gone ? Text.Format(Strings.MultiUploadMissing, gone)
            : Destination.Length == 0 ? Strings.MultiUploadNoDestination
            : Sessions.Count == 0 ? Strings.MultiUploadNoServer
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        DialogResult = true;
    }
}
