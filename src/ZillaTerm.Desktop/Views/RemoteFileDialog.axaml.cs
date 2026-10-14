using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Choix d'un fichier sur un serveur : s'ouvre sur le dossier du chemin proposé (ou son plus proche parent lisible,
/// sinon le dossier personnel), fichier présélectionné. Les dossiers sont lus sans déplacer l'onglet Fichiers. La lecture
/// d'un dossier est confiée à l'appelant (session SFTP de l'onglet Fichiers) : la fenêtre ne dépend d'aucune session.
/// </summary>
public partial class RemoteFileDialog : DialogWindow
{
    private readonly Func<string, bool, CancellationToken, Task<List<RemoteEntry>>> _list = (_, _, _) => Task.FromResult(new List<RemoteEntry>());
    private readonly string _home = "/";
    private CancellationTokenSource? _loading;
    // Lettres tapées dans la liste (sélection par le début du nom), comme dans l'onglet Fichiers.
    private readonly TypeAhead _typeAhead = new();

    public RemoteFileDialog() => InitializeComponent();

    /// <param name="server">Serveur parcouru (titre de la fenêtre).</param>
    /// <param name="path">Chemin proposé ; « ~ » et les chemins relatifs partent de <paramref name="home"/>.</param>
    /// <param name="home">Dossier personnel du compte sur le serveur.</param>
    /// <param name="list">
    /// Lecture d'un dossier (chemin absolu, fichiers cachés compris ou non), fournie par l'appelant : la session SFTP de
    /// l'onglet Fichiers (BrowseAsync).
    /// </param>
    public RemoteFileDialog(string server, string path, string home, Func<string, bool, CancellationToken, Task<List<RemoteEntry>>> list)
        : this()
    {
        Title = Text.Format(Strings.RemotePickTitle, server);
        _list = list;
        _home = home;
        StartPath = RemotePath.ResolveHome(path, home);
        Opened += async (_, _) =>
        {
            await StartAsync();
            FileList.Focus();
        };
        Closed += (_, _) => _loading?.Cancel();
        // Avant la liste et le champ : Entrée, Retour arrière et Espace y ont un autre sens (voir les gestionnaires).
        FileList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        PathBox.AddHandler(KeyDownEvent, OnPathKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Icône d'un fichier distant : dossier, lien, fichier, « .. » (suit le thème clair ou sombre).</summary>
    public static IValueConverter FileIcon { get; } = new FuncValueConverter<RemoteEntry?, Control>(entry => Palette.Icon(entry switch
    {
        { IsParentLink: true } => "IconParentFolder",
        { IsDirectory: true } => "IconFolder",
        { IsSymbolicLink: true } => "IconFileLink",
        _ => "IconFile",
    }, 16));

    /// <summary>Chemin proposé, résolu (absolu).</summary>
    public string StartPath { get; } = "/";

    /// <summary>Dossier affiché.</summary>
    public string CurrentDirectory { get; private set; } = "";

    /// <summary>Fichier choisi.</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>
    /// Dossier du chemin proposé, sinon son plus proche parent lisible, sinon le dossier personnel. S'arrête dès que la
    /// lecture est abandonnée : l'utilisateur a navigué ailleurs entre-temps, ou la fenêtre est fermée.
    /// </summary>
    internal async Task StartAsync()
    {
        var name = RemotePath.Name(StartPath);
        foreach (var directory in RemotePath.Ancestors(RemotePath.Parent(StartPath)).Append(RemotePath.Normalize(_home)).Distinct())
        {
            if (await ShowAsync(directory, select: name) is not Shown.Failed)
            {
                return;
            }

            name = null;
        }
    }

    /// <summary>Ouvre un dossier, ou choisit un fichier.</summary>
    internal async Task OpenAsync(RemoteEntry entry)
    {
        if (entry.IsDirectory)
        {
            // « .. » : le dossier d'où l'on vient reste sélectionné.
            await ShowAsync(entry.FullPath, select: entry.IsParentLink ? RemotePath.Name(CurrentDirectory) : null);
        }
        else
        {
            Choose(entry);
        }
    }

    /// <summary>Chemin saisi : un dossier, ou un fichier (son dossier s'ouvre, fichier sélectionné).</summary>
    internal async Task GoToAsync(string typed)
    {
        var path = RemotePath.ResolveHome(typed, _home);
        if (await ShowAsync(path, reportError: false) == Shown.Failed)
        {
            await ShowAsync(RemotePath.Parent(path), select: RemotePath.Name(path));
        }
    }

    private enum Shown
    {
        /// <summary>Dossier affiché.</summary>
        Yes,

        /// <summary>Illisible : le dossier affiché ne change pas.</summary>
        Failed,

        /// <summary>Lecture abandonnée (autre dossier demandé, fenêtre fermée).</summary>
        Abandoned,
    }

    /// <summary>Lit et affiche un dossier (le dossier affiché ne change pas s'il est illisible).</summary>
    private async Task<Shown> ShowAsync(string directory, string? select = null, bool reportError = true)
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        directory = RemotePath.Normalize(directory);
        SetStatus(Text.Format(Strings.RemotePickLoading, directory));
        try
        {
            var entries = await _list(directory, HiddenBox.IsChecked == true, loading.Token);
            if (loading.IsCancellationRequested)
            {
                return Shown.Abandoned;
            }

            CurrentDirectory = directory;
            PathBox.Text = directory;
            UpButton.IsEnabled = directory != "/";
            var items = new List<RemoteEntry>();
            if (directory != "/")
            {
                items.Add(RemoteEntry.ParentLink(directory));
            }

            items.AddRange(entries);
            FileList.ItemsSource = items;
            var selected = select is null ? null : entries.FirstOrDefault(e => e.Name == select);
            FileList.SelectedItem = selected;
            if (selected is not null)
            {
                FileList.ScrollIntoView(selected);
            }

            SetStatus(Text.Format(Strings.RemotePickCount, entries.Count));
            return Shown.Yes;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (loading.IsCancellationRequested)
            {
                return Shown.Abandoned;
            }

            if (reportError)
            {
                SetStatus(Text.Format(Strings.RemotePickError, directory, Describe(e)), error: true);
            }

            return Shown.Failed;
        }
    }

    private static string Describe(Exception e) => e switch
    {
        Renci.SshNet.Common.SftpPermissionDeniedException => Strings.PermissionDenied,
        Renci.SshNet.Common.SftpPathNotFoundException => Strings.PathNotFound,
        _ => ErrorText.Describe(e),
    };

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText[!TextBlock.ForegroundProperty] = StatusText.GetResourceObservable(error ? "ErrorBrush" : "MutedBrush").ToBinding();
    }

    private void Choose(RemoteEntry entry)
    {
        SelectedPath = entry.FullPath;
        if (IsLoaded)
        {
            DialogResult = true;
        }
    }

    private void OnUp(object? sender, RoutedEventArgs e)
    {
        if (CurrentDirectory is { Length: > 0 } and not "/")
        {
            _ = ShowAsync(RemotePath.Parent(CurrentDirectory), select: RemotePath.Name(CurrentDirectory));
        }
    }

    private void OnGo(object? sender, RoutedEventArgs e) => _ = GoToAsync(PathBox.Text ?? "");

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Entrée dans le chemin : aller au chemin, pas valider la fenêtre.
            e.Handled = true;
            _ = GoToAsync(PathBox.Text ?? "");
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is RemoteEntry entry)
        {
            e.Handled = true;
            _ = OpenAsync(entry);
        }
        else if (e.Key == Key.Back)
        {
            e.Handled = true;
            OnUp(sender, e);
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && _typeAhead.IsTyping(DateTime.UtcNow))
        {
            // Espace pendant la saisie d'un nom (« rapport final ») : elle en fait partie. La liste la traiterait sinon
            // avant TextInput, et la frappe suivante chercherait « rapportf ».
            e.Handled = true;
            TypeAheadSelect(" ");
        }
    }

    /// <summary>
    /// Lettres tapées dans la liste : sélection de l'élément dont le nom commence ainsi, comme dans l'onglet Fichiers (la
    /// même lettre de nouveau passe au suivant).
    /// </summary>
    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        TypeAheadSelect(e.Text);
        e.Handled = true;
    }

    private void TypeAheadSelect(string text)
    {
        var items = FileList.Items.OfType<RemoteEntry>().ToList();
        int current = items.FindIndex(i => ReferenceEquals(i, FileList.SelectedItem));
        int index = _typeAhead.Find(items.ConvertAll(i => i.DisplayName), current, text, DateTime.UtcNow);
        if (index < 0)
        {
            return;
        }

        var item = items[index];
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
        // Le focus suit la ligne choisie (les flèches repartent d'elle), une fois la ligne créée par la liste.
        Dispatcher.UIThread.Post(() => FileList.ContainerFromItem(item)?.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Double-clic sur une ligne (pas sur l'en-tête ni la barre de défilement).</summary>
    private void OnItemDoubleClick(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: RemoteEntry entry })
        {
            e.Handled = true;
            _ = OpenAsync(entry);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ChooseButton.IsEnabled = FileList.SelectedItem is RemoteEntry { IsDirectory: false };

    private void OnChoose(object? sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteEntry { IsDirectory: false } entry)
        {
            Choose(entry);
        }
    }

    private void OnHiddenChanged(object? sender, RoutedEventArgs e)
    {
        if (CurrentDirectory.Length > 0)
        {
            _ = ShowAsync(CurrentDirectory, select: (FileList.SelectedItem as RemoteEntry)?.Name);
        }
    }
}
