namespace ZillaTerm.App.Views;

/// <summary>
/// Filtres des fenêtres de choix de fichier (<see cref="Pickers"/>), écrits comme sous WPF (« Libellé|*.txt|Tous les
/// fichiers (*.*)|*.* »). Hors de Windows, le motif « *.* » ne retient que les noms qui contiennent un point : un fichier
/// sans extension (config d'OpenSSH, consignes) serait caché ; il y devient « * ».
/// </summary>
internal static class SystemFileFilter
{
    public static string Adapt(string filter) => OperatingSystem.IsWindows()
        ? filter
        : string.Join('|', filter.Split('|').Select((part, i) => i % 2 == 1 && part.Trim() == "*.*" ? "*" : part));
}
