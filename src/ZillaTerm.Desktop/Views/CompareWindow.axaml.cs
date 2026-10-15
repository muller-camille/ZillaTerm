using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Comparaison de deux fichiers côte à côte, en mémoire : lignes retirées en rouge à gauche, ajoutées en vert à
/// droite, modifiées des deux côtés. Différence suivante / précédente (F7 / Maj+F7), espaces ignorés, seulement les
/// différences, enregistrement au format « diff -u ». Un fichier binaire (ou trop grand) est comparé par sa somme
/// SHA-256. L'outil de comparaison des Paramètres reçoit des copies temporaires, supprimées à la fermeture.
/// </summary>
public partial class CompareWindow : Window
{
    private static string TemporaryRoot => PrivateTemp.Combine("compare");

    /// <summary>Lettres et chiffres gardés tels quels dans le nom des copies temporaires, avec ces caractères.</summary>
    private const string SafeNameCharacters = ".-_";

    private readonly DiffSide _left;
    private readonly DiffSide _right;
    private readonly byte[] _leftBytes;
    private readonly byte[] _rightBytes;
    private readonly AppSettings _settings;
    private DiffResult? _result;
    private string? _temporary;

    public CompareWindow()
        : this("", [], "", [], new AppSettings())
    {
    }

    /// <param name="leftBytes">Contenu du fichier de gauche (gardé en mémoire pour l'outil externe, effacé à la fermeture).</param>
    public CompareWindow(string leftLabel, byte[] leftBytes, string rightLabel, byte[] rightBytes, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _leftBytes = leftBytes;
        _rightBytes = rightBytes;
        _left = DiffSide.FromBytes(leftLabel, leftBytes);
        _right = DiffSide.FromBytes(rightLabel, rightBytes);
        // Chemins venant du serveur : caractères invisibles remplacés par « � » à l'affichage (le diff enregistré garde
        // les chemins bruts).
        var leftShown = RemoteEntry.Visible(leftLabel);
        var rightShown = RemoteEntry.Visible(rightLabel);
        Title = Text.Format(Strings.CompareWindowTitle, RemoteName(leftShown), RemoteName(rightShown));
        LeftTitle.Text = $"− {leftShown}  ({RemotePath.FormatSize(_left.Length)})";
        RightTitle.Text = $"+ {rightShown}  ({RemotePath.FormatSize(_right.Length)})";
        ToolTip.SetTip(LeftTitle, leftShown);
        ToolTip.SetTip(RightTitle, rightShown);
        if (settings.CompareTool.Length > 0)
        {
            ToolButton.Content = Text.Format(Strings.CompareOpenTool, Path.GetFileNameWithoutExtension(settings.CompareTool.Trim().Trim('"')));
            ToolButton.IsVisible = true;
        }

        Render();
        // Avant la liste : F7 vaut dans toute la fenêtre.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => Cleanup();
    }

    /// <summary>Vrai si la sorte de ligne (<see cref="DiffRow.Kind"/>) est celle du paramètre : classes des lignes du modèle.</summary>
    public static IValueConverter IsKind { get; } = new FuncValueConverter<DiffKind, object?, bool>((kind, parameter) => parameter is DiffKind wanted && kind == wanted);

    /// <summary>Résultat affiché (tests).</summary>
    internal DiffResult? Result => _result;

    private static string RemoteName(string label) => label.Split([':', '\\', '/']).LastOrDefault(p => p.Trim().Length > 0)?.Trim() ?? label;

    private void Render()
    {
        if (!_left.IsText || !_right.IsText)
        {
            // Binaire ou trop grand : pas de lignes, seulement la taille et la somme.
            _result = null;
            RowsList.IsVisible = false;
            BinaryPanel.IsVisible = true;
            bool same = _left.Sha256 == _right.Sha256;
            BinaryText.Text = Text.Format(same ? Strings.CompareBinarySame : Strings.CompareBinaryDifferent,
                RemoteEntry.Visible(_left.Name), RemotePath.FormatSize(_left.Length), _left.Sha256,
                RemoteEntry.Visible(_right.Name), RemotePath.FormatSize(_right.Length), _right.Sha256);
            SummaryText.Text = same ? Strings.CompareIdentical : Strings.CompareDifferent;
            SetSummaryColor(same ? "SuccessBrush" : "ErrorBrush");
            WhitespaceBox.IsEnabled = false;
            OnlyDiffBox.IsEnabled = false;
            SaveButton.IsEnabled = false;
            return;
        }

        _result = TextDiff.Compare(_left.Lines!, _right.Lines!, WhitespaceBox.IsChecked == true);
        RowsList.ItemsSource = OnlyDiffBox.IsChecked == true ? _result.OnlyDifferences() : _result.Rows;
        if (_result.Identical)
        {
            SummaryText.Text = _left.Sha256 == _right.Sha256 ? Strings.CompareIdentical : Strings.CompareIdenticalApart;
            SetSummaryColor("SuccessBrush");
        }
        else
        {
            SummaryText.Text = Text.Format(Strings.CompareSummary, _result.Blocks, _result.Removed, _result.Added);
            SetSummaryColor("ErrorBrush");
        }

        var notes = new List<string>();
        if (_left.UsesCrlf != _right.UsesCrlf)
        {
            notes.Add(Text.Format(Strings.CompareLineEndings, _left.UsesCrlf ? "CRLF" : "LF", _right.UsesCrlf ? "CRLF" : "LF"));
        }

        if (_result.Approximate)
        {
            notes.Add(Strings.CompareApproximate);
        }

        notes.Add(Strings.CompareInMemory);
        NoteText.Text = string.Join(" ", notes);
    }

    private void SetSummaryColor(string key) =>
        SummaryText[!TextBlock.ForegroundProperty] = SummaryText.GetResourceObservable(key).ToBinding();

    private void OnOptionChanged(object? sender, RoutedEventArgs e) => Render();

    private void OnNext(object? sender, RoutedEventArgs e) => GoToDifference(1);

    private void OnPrevious(object? sender, RoutedEventArgs e) => GoToDifference(-1);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F7)
        {
            e.Handled = true;
            GoToDifference(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
    }

    /// <summary>Début de la zone de différences suivante (ou précédente), en repartant du début si besoin.</summary>
    internal void GoToDifference(int direction)
    {
        if (RowsList.ItemsSource is not IReadOnlyList<DiffRow> rows || rows.Count == 0)
        {
            return;
        }

        static bool Changed(DiffRow row) => row.Kind is DiffKind.Removed or DiffKind.Added or DiffKind.Changed;
        bool StartsBlock(int i) => Changed(rows[i]) && (i == 0 || !Changed(rows[i - 1]));
        int start = RowsList.SelectedIndex < 0 ? (direction > 0 ? -1 : rows.Count) : RowsList.SelectedIndex;
        for (int step = 1; step <= rows.Count; step++)
        {
            int i = ((start + (direction * step)) % rows.Count + rows.Count) % rows.Count;
            if (StartsBlock(i))
            {
                RowsList.SelectedIndex = i;
                // Quelques lignes de la zone visibles sous son début.
                RowsList.ScrollIntoView(Math.Min(rows.Count - 1, i + 8));
                RowsList.ScrollIntoView(i);
                return;
            }
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_result is not { } result)
        {
            return;
        }

        var path = await Pickers.SaveFileAsync(this, Strings.CompareSaveFilter, RemoteName(_left.Name) + ".diff",
            Strings.CompareSave.Replace("_", "").TrimEnd('…'));
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, TextDiff.Unified(result, _left.Name, _right.Name), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ConfirmDialog.ShowMessageAsync(this, ex.Message, ConfirmKind.Danger, Title);
        }
    }

    /// <summary>
    /// Outil de comparaison des Paramètres : il reçoit deux copies temporaires (dossier propre à cette fenêtre, supprimé
    /// à sa fermeture et au prochain lancement).
    /// </summary>
    private async void OnExternalTool(object? sender, RoutedEventArgs e)
    {
        try
        {
            var info = ToolStartInfo(_settings.CompareTool, _settings.CompareToolArguments, SessionLauncher.FindOnPath, WriteCopies);
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or FormatException
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.CompareToolFailed, ex.Message), ConfirmKind.Danger, Title);
        }
    }

    /// <summary>Copies des deux fichiers pour l'outil externe ; chemins complets (gauche, droite).</summary>
    private (string Left, string Right) WriteCopies()
    {
        if (_temporary is null)
        {
            var folder = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                // Réservé à l'utilisateur, comme le dossier temporaire qui le contient.
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            _temporary = Path.GetFullPath(folder);
        }

        return (WritePrivate(_temporary, "1-" + SafeName(RemoteName(_left.Name)), _leftBytes),
            WritePrivate(_temporary, "2-" + SafeName(RemoteName(_right.Name)), _rightBytes));
    }

    /// <summary>
    /// Nom d'une copie temporaire : celui du fichier, sans rien qu'un programme ou un interpréteur puisse lire autrement
    /// (espaces, guillemets, « &amp; », « % », « ; », « $ »...), qui deviennent « _ ». Le nom vient du serveur.
    /// </summary>
    internal static string SafeName(string name)
    {
        var clean = string.Concat(WindowsFileName.Sanitize(name)
            .Select(c => (char.IsLetterOrDigit(c) && !RemoteEntry.IsInvisible(c)) || SafeNameCharacters.Contains(c) ? c : '_'));
        clean = clean.TrimStart('.', '-');
        if (clean.Length > 100)
        {
            clean = clean[^100..];
        }

        return clean.Length == 0 ? "_" : clean;
    }

    /// <summary>
    /// Fichier écrit dans <paramref name="folder"/> seulement (chemin vérifié), lisible par l'utilisateur seul hors de
    /// Windows ; un fichier déjà là (clic précédent) est remplacé, jamais suivi s'il est devenu un lien.
    /// </summary>
    private static string WritePrivate(string folder, string name, byte[] content)
    {
        var path = Path.GetFullPath(Path.Combine(folder, name));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.GetFileName(path) != name)
        {
            throw new UnauthorizedAccessException($"{path} : {Strings.PermissionDenied}");
        }

        File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(content);
        return path;
    }

    /// <summary>
    /// Lancement de l'outil de comparaison <paramref name="tool"/> sans interpréteur de commandes : le programme par son
    /// chemin complet (un nom seul est cherché dans le PATH, jamais dans le dossier courant), et les arguments du modèle
    /// des Paramètres (« {0} » : fichier de gauche, « {1} » : de droite) découpés AVANT d'y placer les chemins, puis
    /// passés un par un (ArgumentList) : un nom de fichier ne peut ni ajouter d'argument ni devenir une commande.
    /// Les copies (<paramref name="copies"/>) ne sont écrites qu'une fois le programme trouvé.
    /// </summary>
    /// <remarks>macOS : une application (dossier « .app ») est ouverte par « open -a », les arguments après « --args ».</remarks>
    /// <exception cref="FileNotFoundException">Programme introuvable.</exception>
    /// <exception cref="FormatException">Modèle d'arguments invalide.</exception>
    internal static ProcessStartInfo ToolStartInfo(string tool, string? template, Func<string, string?> find, Func<(string Left, string Right)> copies)
    {
        tool = tool.Trim().Trim('"');
        var program = ResolveTool(tool, find) ?? throw new FileNotFoundException(Text.Format("{0} : {1}", tool, Strings.PathNotFound));
        var pattern = SplitArguments(string.IsNullOrWhiteSpace(template) ? AppSettings.DefaultCompareArguments : template);
        // Modèle vérifié avant d'écrire les copies.
        foreach (var part in pattern)
        {
            _ = string.Format(CultureInfo.InvariantCulture, part, "", "");
        }

        var (left, right) = copies();
        var arguments = pattern.Select(part => string.Format(CultureInfo.InvariantCulture, part, left, right)).ToList();
        ProcessStartInfo info;
        if (OperatingSystem.IsMacOS() && Directory.Exists(program))
        {
            info = new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-n", "-a", program, "--args" } };
        }
        else
        {
            info = new ProcessStartInfo(program);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.UseShellExecute = false;
        return info;
    }

    /// <summary>
    /// Chemin complet de l'outil : un chemin absolu existant (ou, sous macOS, une application « .app »), sinon un nom seul
    /// trouvé dans le PATH ; un chemin relatif n'est jamais lancé (il partirait du dossier courant). Null si introuvable.
    /// </summary>
    internal static string? ResolveTool(string tool, Func<string, string?> find)
    {
        if (tool.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(tool))
        {
            return File.Exists(tool) || (OperatingSystem.IsMacOS() && tool.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                                         && Directory.Exists(tool))
                ? Path.GetFullPath(tool)
                : null;
        }

        if (tool.IndexOfAny(['/', '\\']) >= 0)
        {
            return null;
        }

        return find(tool) ?? (OperatingSystem.IsWindows() && Path.GetExtension(tool).Length == 0 ? find(tool + ".exe") : null);
    }

    /// <summary>
    /// Arguments d'un modèle des Paramètres (par ex. <c>/e /u "{0}" "{1}"</c> ou <c>--diff "{0}" "{1}"</c>) : séparés par
    /// les espaces, les guillemets regroupant (et n'étant pas transmis).
    /// </summary>
    internal static List<string> SplitArguments(string template)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool started = false;
        foreach (char c in template)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    /// <summary>Copies temporaires supprimées, contenus effacés de la mémoire.</summary>
    private void Cleanup()
    {
        Array.Clear(_leftBytes);
        Array.Clear(_rightBytes);
        RowsList.ItemsSource = null;
        if (_temporary is not null)
        {
            DeleteFolder(_temporary);
        }
    }

    /// <summary>Copies laissées par une session précédente (outil encore ouvert à la fermeture) : supprimées au lancement.</summary>
    public static void CleanTemporaryFiles()
    {
        string root;
        try
        {
            root = TemporaryRoot;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Dossier temporaire qui n'est plus réservé à l'utilisateur : on n'y touche pas.
            DebugLog.Write("compare", $"Copies temporaires non supprimées : {ex.Message}");
            return;
        }

        DeleteFolder(root);
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("compare", $"Copies temporaires non supprimées : {ex.Message}");
        }
    }
}
