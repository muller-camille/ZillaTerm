using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;
using Microsoft.Win32;

namespace ZillaTerm.App.Views;

/// <summary>
/// Comparaison de deux fichiers côte à côte, en mémoire : lignes retirées en rouge à gauche, ajoutées en vert à
/// droite, modifiées des deux côtés. Différence suivante / précédente (F7 / Maj+F7), espaces ignorés, seulement les
/// différences, enregistrement au format « diff -u ». Un fichier binaire (ou trop grand) est comparé par sa somme
/// SHA-256. L'outil de comparaison des Paramètres reçoit des copies temporaires, supprimées à la fermeture.
/// </summary>
public partial class CompareWindow : Window
{
    private static string TemporaryRoot => Services.PrivateTemp.Combine("compare");

    private readonly DiffSide _left;
    private readonly DiffSide _right;
    private readonly byte[] _leftBytes;
    private readonly byte[] _rightBytes;
    private readonly AppSettings _settings;
    private DiffResult? _result;
    private string? _temporary;

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
        LeftTitle.ToolTip = leftShown;
        RightTitle.ToolTip = rightShown;
        if (settings.CompareTool.Length > 0)
        {
            ToolButton.Content = Text.Format(Strings.CompareOpenTool, Path.GetFileNameWithoutExtension(settings.CompareTool));
            ToolButton.Visibility = Visibility.Visible;
        }

        Render();
        Closed += (_, _) => Cleanup();
    }

    /// <summary>Résultat affiché (tests).</summary>
    internal DiffResult? Result => _result;

    private static string RemoteName(string label) => label.Split([':', '\\', '/']).LastOrDefault(p => p.Trim().Length > 0)?.Trim() ?? label;

    private void Render()
    {
        if (!_left.IsText || !_right.IsText)
        {
            // Binaire ou trop grand : pas de lignes, seulement la taille et la somme.
            _result = null;
            RowsList.Visibility = Visibility.Collapsed;
            BinaryPanel.Visibility = Visibility.Visible;
            bool same = _left.Sha256 == _right.Sha256;
            BinaryText.Text = Text.Format(same ? Strings.CompareBinarySame : Strings.CompareBinaryDifferent,
                RemoteEntry.Visible(_left.Name), RemotePath.FormatSize(_left.Length), _left.Sha256,
                RemoteEntry.Visible(_right.Name), RemotePath.FormatSize(_right.Length), _right.Sha256);
            SummaryText.Text = same ? Strings.CompareIdentical : Strings.CompareDifferent;
            SummaryText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, same ? "SuccessBrush" : "ErrorBrush");
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
            SummaryText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessBrush");
        }
        else
        {
            SummaryText.Text = Text.Format(Strings.CompareSummary, _result.Blocks, _result.Removed, _result.Added);
            SummaryText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
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

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Render();

    private void OnNext(object sender, RoutedEventArgs e) => GoToDifference(1);

    private void OnPrevious(object sender, RoutedEventArgs e) => GoToDifference(-1);

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F7)
        {
            e.Handled = true;
            GoToDifference(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
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
                RowsList.ScrollIntoView(rows[Math.Min(rows.Count - 1, i + 8)]);
                RowsList.ScrollIntoView(rows[i]);
                return;
            }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Strings.CompareSave.Replace("_", "").TrimEnd('…'),
            Filter = Strings.CompareSaveFilter,
            FileName = RemoteName(_left.Name) + ".diff",
            AddExtension = true,
            DefaultExt = ".diff",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, TextDiff.Unified(_result, _left.Name, _right.Name), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Outil de comparaison des Paramètres : il reçoit deux copies temporaires (dossier propre à cette fenêtre, supprimé
    /// à sa fermeture et au prochain lancement).
    /// </summary>
    private void OnExternalTool(object sender, RoutedEventArgs e)
    {
        try
        {
            _temporary ??= Directory.CreateDirectory(Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"))).FullName;
            var left = Path.Combine(_temporary, "1-" + WindowsFileName.Sanitize(RemoteName(_left.Name)));
            var right = Path.Combine(_temporary, "2-" + WindowsFileName.Sanitize(RemoteName(_right.Name)));
            File.WriteAllBytes(left, _leftBytes);
            File.WriteAllBytes(right, _rightBytes);
            var template = string.IsNullOrWhiteSpace(_settings.CompareToolArguments) ? "\"{0}\" \"{1}\"" : _settings.CompareToolArguments;
            Process.Start(new ProcessStartInfo(_settings.CompareTool, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, left, right))
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FormatException
                                       or InvalidOperationException)
        {
            MessageBox.Show(this, Text.Format(Strings.CompareToolFailed, ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
    public static void CleanTemporaryFiles() => DeleteFolder(TemporaryRoot);

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
