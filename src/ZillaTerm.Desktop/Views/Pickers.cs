using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ZillaTerm.App.Views;

/// <summary>
/// Choix de fichiers et de dossiers du poste (remplace OpenFileDialog, SaveFileDialog et OpenFolderDialog de WPF). Les
/// filtres gardent l'écriture de WPF (« Libellé|*.ext;*.ext2|Libellé|*.* ») : les textes traduits ne changent pas.
/// </summary>
internal static class Pickers
{
    /// <summary>Un fichier à ouvrir ; null si annulé ou s'il n'est pas un fichier local.</summary>
    public static async Task<string?> OpenFileAsync(Window owner, string? filter = null, string? title = null, string? folder = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = Types(filter),
            SuggestedStartLocation = await Folder(owner, folder),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Plusieurs fichiers à ouvrir (fichiers locaux seulement) ; vide si annulé.</summary>
    public static async Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, string? filter = null, string? title = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = Types(filter),
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>Fichier à enregistrer ; null si annulé.</summary>
    /// <param name="overwritePrompt">Faux : pas de question pour un fichier existant (l'appelant le traite lui-même).</param>
    public static async Task<string?> SaveFileAsync(Window owner, string? filter = null, string? suggestedName = null, string? title = null,
        string? folder = null, bool overwritePrompt = true)
    {
        var types = Types(filter);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = types,
            DefaultExtension = types?.FirstOrDefault()?.Patterns?.FirstOrDefault() is { } pattern && pattern.StartsWith("*.", StringComparison.Ordinal)
                && pattern.IndexOf('*', 2) < 0 ? pattern[2..] : null,
            ShowOverwritePrompt = overwritePrompt,
            SuggestedStartLocation = await Folder(owner, folder),
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Un dossier ; null si annulé.</summary>
    public static async Task<string?> OpenFolderAsync(Window owner, string? title = null, string? folder = null)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await Folder(owner, folder),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>Filtre WPF (« Libellé|motifs|Libellé|motifs ») en types de fichiers d'Avalonia.</summary>
    internal static List<FilePickerFileType>? Types(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            types.Add(new FilePickerFileType(parts[i])
            {
                Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(AllFiles).ToList(),
            });
        }

        return types;
    }

    /// <summary>
    /// Hors de Windows, le motif « *.* » ne retient que les noms qui contiennent un point : un fichier sans extension
    /// (config d'OpenSSH, consignes) serait caché ; il y devient « * ».
    /// </summary>
    private static string AllFiles(string pattern) => pattern == "*.*" && !OperatingSystem.IsWindows() ? "*" : pattern;

    private static async Task<IStorageFolder?> Folder(Window owner, string? path) =>
        string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(path);
}
