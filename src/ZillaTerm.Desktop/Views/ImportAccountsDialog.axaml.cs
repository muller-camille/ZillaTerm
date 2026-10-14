using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Première étape de l'import de comptes : choix du fichier CSV (ou enregistrement d'un modèle), safe et plateforme
/// par défaut, et résumé de ce qui sera créé. « Importer » confirme ; rien n'est envoyé au PVWA ici. Les mots de
/// passe lus ne sont jamais convertis en chaînes ; ils sont effacés si la fenêtre est fermée sans confirmer.
/// </summary>
public partial class ImportAccountsDialog : DialogWindow
{
    /// <summary>Taille maximale du fichier lu.</summary>
    private const int MaxFileSize = 10 * 1024 * 1024;

    private AccountImport? _import;
    private string? _path;
    private List<ImportPreviewRow> _preview = [];

    public ImportAccountsDialog() => InitializeComponent();

    public ImportAccountsDialog(IEnumerable<string> safes, IEnumerable<string> platforms, string? safe, string? platform)
        : this()
    {
        SafeBox.ItemsSource = safes.ToList();
        SafeBox.Text = safe ?? "";
        PlatformBox.ItemsSource = platforms.ToList();
        PlatformBox.Text = platform ?? "";
        // Branchés après le chargement : les valeurs ci-dessus ne relisent pas un fichier qui n'est pas encore choisi.
        EditableComboText.OnChanged(SafeBox, Reload);
        EditableComboText.OnChanged(PlatformBox, Reload);
        // Fermée sans confirmer : les mots de passe lus sont effacés.
        Closed += (_, _) => _import?.Clear();
    }

    /// <summary>
    /// Lignes confirmées par « Importer », à passer à <see cref="ImportProgressDialog"/> ; null si la fenêtre a été
    /// fermée sans confirmer. L'appelant devient responsable de leur effacement (<see cref="AccountImport.Clear"/>).
    /// </summary>
    public AccountImport? Confirmed { get; private set; }

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.OpenFileAsync(this, Strings.ImportOpenFilter, Strings.ImportTitle) is { } path)
        {
            LoadFile(path);
        }
    }

    /// <summary>Lit le fichier choisi et montre ce qui sera créé.</summary>
    internal void LoadFile(string path)
    {
        _path = path;
        FileText.Text = _path;
        ToolTip.SetTip(FileText, _path);
        Reload();
    }

    private async void OnSaveTemplate(object? sender, RoutedEventArgs e)
    {
        var path = await Pickers.SaveFileAsync(this, Strings.ExportFilter, Strings.ImportTemplateFileName + ".csv",
            Strings.ImportTemplate.Replace("_", ""));
        if (path is null)
        {
            return;
        }

        try
        {
            // BOM UTF-8 pour qu'Excel lise correctement les accents ; séparateur de la région du système.
            File.WriteAllText(path, AccountCsv.Template(CsvExporter.DefaultSeparator(System.Globalization.CultureInfo.CurrentCulture)),
                new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Text.Format(Strings.ImportFileError, ex.Message));
        }
    }

    /// <summary>Relit le fichier choisi avec le safe et la plateforme par défaut actuels.</summary>
    private void Reload()
    {
        if (_path is null)
        {
            return;
        }

        _import?.Clear();
        _import = null;
        ErrorMessage.IsVisible = false;
        byte[]? bytes = null;
        char[]? text = null;
        try
        {
            if (new FileInfo(_path).Length > MaxFileSize)
            {
                ShowError(Strings.ImportFileTooLarge);
                return;
            }

            bytes = File.ReadAllBytes(_path);
            text = AccountCsv.Decode(bytes);
            _import = AccountCsv.Parse(text, SafeBox.Text, PlatformBox.Text);
            if (_import.Error is { } error)
            {
                ShowError(error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Text.Format(Strings.ImportFileError, ex.Message));
        }
        finally
        {
            // Le fichier peut contenir des mots de passe : copies effacées dès la lecture finie.
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (text is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(text.AsSpan()));
            }

            ShowSummary();
        }
    }

    private void ShowSummary()
    {
        var rows = _import?.Rows ?? [];
        int ready = _import?.Ready ?? 0;
        int errors = rows.Count(r => r.Error is not null);
        int safes = rows.Where(r => r.Account is not null).Select(r => r.Safe).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SummaryText.Text = Text.Format(Strings.ImportSummary, ready, errors) + (safes > 0 ? " " + Text.Format(Strings.ImportSafesCount, safes) : "");
        SummaryText.IsVisible = PreviewGrid.IsVisible = rows.Count > 0;
        OnlyErrorsBox.IsVisible = errors > 0;
        OnlyErrorsBox.IsChecked = OnlyErrorsBox.IsChecked == true && errors > 0;
        _preview = rows.Select(r => new ImportPreviewRow(r)).ToList();
        ShowPreview();
        PasswordWarning.IsVisible = rows.Any(r => r.HasPassword);
        // Le bouton dit ce qui va se passer : « Créer 248 comptes ».
        ImportButton.Content = ready > 0 ? Text.Format(Strings.ImportCreateAccounts, ready) : Strings.ImportButton;
        ImportButton.IsEnabled = ready > 0;
    }

    private void ShowPreview() =>
        PreviewGrid.ItemsSource = OnlyErrorsBox.IsChecked == true ? _preview.Where(r => r.IsError).ToList() : _preview;

    private void OnOnlyErrors(object? sender, RoutedEventArgs e) => ShowPreview();

    /// <summary>Ligne en erreur : texte de la couleur des erreurs.</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e) =>
        e.Row.Classes.Set("Error", e.Row.DataContext is ImportPreviewRow { IsError: true });

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_import is { Ready: > 0 } import)
        {
            // Retiré de _import avant la fermeture : Closed n'efface que ce qui n'a pas été confirmé.
            Confirmed = import;
            _import = null;
            DialogResult = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.IsVisible = true;
    }
}

/// <summary>
/// Ligne de l'aperçu de l'import : état, cible ; le mot de passe n'est indiqué que par sa présence. (Classe de premier
/// niveau, et non imbriquée comme sous WPF : les liaisons compilées du tableau la nomment.)
/// </summary>
internal sealed class ImportPreviewRow(ImportRow row)
{
    public int Line { get; } = row.Line;

    public bool IsError { get; } = row.Error is not null;

    public string Status { get; } = row.Error ?? Strings.ImportReady;

    public string Safe { get; } = row.Safe;

    public string Platform { get; } = row.Platform;

    public string Account { get; } = row.UserName.Length > 0 && row.Address.Length > 0 ? $"{row.UserName}@{row.Address}" : row.UserName + row.Address;

    public string Password { get; } = row.HasPassword ? "••••••" : "";

    /// <summary>Nom lu par les lecteurs d'écran.</summary>
    public override string ToString() => $"{Line}, {Status}, {Account}";
}
