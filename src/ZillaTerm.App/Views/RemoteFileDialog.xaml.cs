using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Choix d'un fichier sur un serveur : s'ouvre sur le dossier du chemin proposé (ou son plus proche parent lisible,
/// sinon le dossier personnel), fichier présélectionné. Les dossiers sont lus sans déplacer l'onglet Fichiers.
/// </summary>
public partial class RemoteFileDialog : Window
{
    private readonly Func<string, bool, CancellationToken, Task<List<RemoteEntry>>> _list;
    private readonly string _home;
    private CancelOnce? _loading;
    // Lettres tapées dans la liste (sélection par le début du nom), comme dans l'onglet Fichiers.
    private readonly TypeAhead _typeAhead = new();

    /// <param name="server">Serveur parcouru (titre de la fenêtre).</param>
    /// <param name="path">Chemin proposé ; « ~ » et les chemins relatifs partent de <paramref name="home"/>.</param>
    /// <param name="list">Lecture d'un dossier (chemin absolu, fichiers cachés compris ou non).</param>
    public RemoteFileDialog(string server, string path, string home, Func<string, bool, CancellationToken, Task<List<RemoteEntry>>> list)
    {
        InitializeComponent();
        Title = Text.Format(Strings.RemotePickTitle, server);
        _list = list;
        _home = home;
        StartPath = RemotePath.ResolveHome(path, home);
        Loaded += async (_, _) =>
        {
            await StartAsync();
            FileList.Focus();
        };
        Closed += (_, _) => _loading?.Cancel();
    }

    /// <summary>Chemin proposé, résolu (absolu).</summary>
    public string StartPath { get; }

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
        var loading = _loading = new CancelOnce();
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
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    private void Choose(RemoteEntry entry)
    {
        SelectedPath = entry.FullPath;
        if (IsLoaded)
        {
            DialogResult = true;
        }
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (CurrentDirectory is { Length: > 0 } and not "/")
        {
            _ = ShowAsync(RemotePath.Parent(CurrentDirectory), select: RemotePath.Name(CurrentDirectory));
        }
    }

    private void OnGo(object sender, RoutedEventArgs e) => _ = GoToAsync(PathBox.Text);

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Entrée dans le chemin : aller au chemin, pas valider la fenêtre.
            e.Handled = true;
            _ = GoToAsync(PathBox.Text);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
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
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && _typeAhead.IsTyping(DateTime.UtcNow))
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
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || e.Text.Any(char.IsControl))
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
        FileList.UpdateLayout();
        (FileList.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus();
    }

    /// <summary>Double-clic sur une ligne (pas sur l'en-tête ni la barre de défilement).</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FileList, source) is ListViewItem { DataContext: RemoteEntry entry })
        {
            e.Handled = true;
            _ = OpenAsync(entry);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ChooseButton.IsEnabled = FileList.SelectedItem is RemoteEntry { IsDirectory: false };

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteEntry { IsDirectory: false } entry)
        {
            Choose(entry);
        }
    }

    private void OnHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (CurrentDirectory.Length > 0)
        {
            _ = ShowAsync(CurrentDirectory, select: (FileList.SelectedItem as RemoteEntry)?.Name);
        }
    }
}
