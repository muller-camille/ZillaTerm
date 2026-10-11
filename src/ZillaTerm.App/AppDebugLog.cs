using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App;

/// <summary>Démarrage et arrêt du journal de débogage selon l'option des paramètres.</summary>
internal static class AppDebugLog
{
    /// <summary>Active ou désactive le journal comme le demande <paramref name="settings"/>.</summary>
    public static void Apply(AppSettings settings)
    {
        if (settings.DebugLogEnabled && !DebugLog.Enabled)
        {
            DebugLog.Start(DebugLog.DefaultPath, Header(settings));
        }
        else if (!settings.DebugLogEnabled && DebugLog.Enabled)
        {
            DebugLog.Stop();
        }
    }

    /// <summary>Version de l'application, du système et du contrôle Bureau à distance, options utiles au diagnostic.</summary>
    private static string Header(AppSettings s)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        return string.Create(CultureInfo.InvariantCulture, $"""
            Journal de débogage activé : ZillaTerm {version}
            Système : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}, processus {RuntimeInformation.ProcessArchitecture}
            Contrôle Bureau à distance (mstscax.dll) : {RdpControlVersion()}
            Langue : {CultureInfo.CurrentUICulture.Name} (réglage « {s.Language} »)
            Options : authentification {s.AuthMethod}, PSMP « {s.PsmpAddress} » + {s.PsmpServers.Count} par domaine, SSH dans l'onglet {s.SshInApp}, PSM Windows dans l'onglet {s.PsmDesktopInTab}, maintien de la session PVWA {s.KeepPvwaSessionAlive}
            """);
    }

    private static string RdpControlVersion()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "mstscax.dll");
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?" : "absent";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }
}
