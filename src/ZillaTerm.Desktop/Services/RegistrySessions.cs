using System.Runtime.Versioning;
using Microsoft.Win32;
using ZillaTerm.Core.Migration;

namespace ZillaTerm.App.Services;

/// <summary>
/// Sessions d'un autre logiciel rangées dans le registre de l'utilisateur (PuTTY, KiTTY, WinSCP) : seules les valeurs
/// utiles à l'import sont lues (serveur, port, protocole, utilisateur, dossier), jamais un mot de passe. Le registre
/// n'existe que sous Windows : ailleurs, ces sources ne sont pas proposées (SessionImportWindow.Sources).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistrySessions
{
    public static List<ImportedSession> Read(ImportSourceKind kind)
    {
        var path = SessionSources.RegistryPath(kind);
        var names = SessionSources.RegistryValueNames(kind);
        var keys = new List<RegKey>();
        using (var root = Registry.CurrentUser.OpenSubKey(path))
        {
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(name);
                if (key is null)
                {
                    continue;
                }

                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in names)
                {
                    switch (key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames))
                    {
                        case string text:
                            values[value] = text;
                            break;
                        case int number:
                            values[value] = number;
                            break;
                    }
                }

                keys.Add(new RegKey($@"HKEY_CURRENT_USER\{path}\{name}", values));
            }
        }

        return SessionSources.FromRegistry(kind, keys);
    }
}
