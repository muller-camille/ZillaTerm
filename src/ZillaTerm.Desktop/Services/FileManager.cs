using System.Diagnostics;
using System.IO;

namespace ZillaTerm.App.Services;

/// <summary>
/// Gestionnaire de fichiers du poste ouvert sur un fichier (sélectionné dans son dossier) ou sur un dossier : Explorateur
/// sous Windows, Finder sous macOS, gestionnaire de fichiers du bureau sous Linux (interface D-Bus FileManager1, sinon
/// xdg-open sur le dossier). Le chemin, complet, sans guillemet ni caractère de contrôle, est passé comme un seul
/// argument, et les programmes lancés sont ceux des dossiers du système (jamais un exécutable du dossier courant).
/// </summary>
internal static class FileManager
{
    /// <summary>Dossiers du système où sont cherchés gdbus, xdg-open et open.</summary>
    private static readonly string[] SystemFolders = ["/usr/bin", "/bin", "/usr/local/bin"];

    /// <summary>Dossier du fichier, le fichier sélectionné si le gestionnaire de fichiers le permet.</summary>
    /// <exception cref="ArgumentException">Chemin relatif, ou contenant un guillemet ou un caractère de contrôle.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Le gestionnaire de fichiers n'a pas pu être lancé.</exception>
    public static void ShowFile(string path)
    {
        var full = Checked(path);
        if (OperatingSystem.IsWindows())
        {
            WindowsExplorer.ShowFile(full);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Start(Program("open"), "-R", full);
        }
        else if (!ShowItemOverDbus(full))
        {
            Start(Program("xdg-open"), Path.GetDirectoryName(full) ?? full);
        }
    }

    /// <summary>Contenu du dossier.</summary>
    /// <exception cref="ArgumentException">Chemin relatif, ou contenant un guillemet ou un caractère de contrôle.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Le gestionnaire de fichiers n'a pas pu être lancé.</exception>
    public static void OpenFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Checked(folder));
        if (OperatingSystem.IsWindows())
        {
            WindowsExplorer.OpenFolder(full);
        }
        else
        {
            Start(Program(OperatingSystem.IsMacOS() ? "open" : "xdg-open"), full);
        }
    }

    /// <summary>
    /// Méthode ShowItems de org.freedesktop.FileManager1 (Nautilus, Dolphin, Nemo, Thunar…) ; faux si gdbus manque ou si
    /// aucun gestionnaire de fichiers ne répond.
    /// </summary>
    private static bool ShowItemOverDbus(string path)
    {
        if (Program("gdbus") is not { } gdbus)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(gdbus)
            {
                ArgumentList =
                {
                    "call", "--session", "--dest", "org.freedesktop.FileManager1", "--object-path", "/org/freedesktop/FileManager1",
                    "--method", "org.freedesktop.FileManager1.ShowItems", $"['{GVariantText(new Uri(path).AbsoluteUri)}']", "",
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                // Le gestionnaire de fichiers a sans doute reçu la demande ; gdbus n'a plus rien à faire.
                process.Kill();
                return true;
            }

            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Chaîne entre apostrophes du format texte de GVariant : barre oblique inverse et apostrophe échappées.</summary>
    internal static string GVariantText(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static string? Program(string name) =>
        SystemFolders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    private static void Start(string? program, params string[] arguments)
    {
        if (program is null)
        {
            throw new System.ComponentModel.Win32Exception(2, "Gestionnaire de fichiers introuvable.");
        }

        var info = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info)?.Dispose();
    }

    private static string Checked(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(c => c == '"' || char.IsControl(c)) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Chemin refusé pour le gestionnaire de fichiers : {path}", nameof(path));
        }

        return Path.GetFullPath(path);
    }
}
