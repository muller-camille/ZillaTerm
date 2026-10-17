using System.IO;
using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Views;

/// <summary>
/// Fichiers d'environnement (<see cref="EnvironmentProfile"/>) : celui posé à côté de l'exécutable et le fichier central
/// sont proposés au démarrage quand ils ont changé ; un fichier choisi s'importe à la demande. Chaque fois, les
/// changements sont montrés et à confirmer : le PVWA et les PSMP reçoivent le mot de passe CyberArk.
/// </summary>
internal static class EnvironmentImport
{
    /// <summary>
    /// Fichier lu à côté de l'exécutable (application partagée avec son environnement, par exemple dans un zip) ; à défaut,
    /// celui qui porte l'ancien nom de l'application.
    /// </summary>
    internal static string LocalFile
    {
        get
        {
            var file = Path.Combine(AppContext.BaseDirectory, EnvironmentProfile.FileName);
            var legacy = Path.Combine(AppContext.BaseDirectory, EnvironmentProfile.LegacyFileName);
            return !File.Exists(file) && File.Exists(legacy) ? legacy : file;
        }
    }

    internal const string FileFilter = "*.env.json|*.env.json|*.json|*.json";

    /// <summary>Au démarrage : fichier à côté de l'exécutable puis fichier central, s'ils ont changé depuis la dernière fois.</summary>
    internal static void OfferAtStartup(AppSettings settings)
    {
        Offer(null, settings, LocalFile, automatic: true);
        if (!string.IsNullOrWhiteSpace(settings.EnvironmentFile))
        {
            Offer(null, settings, settings.EnvironmentFile, automatic: true);
        }
    }

    /// <summary>
    /// Propose l'environnement du fichier <paramref name="path"/>. <paramref name="automatic"/> (démarrage) : rien si le
    /// fichier est absent ou inchangé depuis qu'il a été proposé ; sinon un fichier illisible ou sans changement est signalé.
    /// </summary>
    /// <returns>Vrai si l'environnement a été appliqué (réglages enregistrés).</returns>
    internal static bool Offer(Window? owner, AppSettings settings, string path, bool automatic)
    {
        var full = path.Trim();
        var key = full.ToLowerInvariant();
        EnvironmentFile file;
        try
        {
            if (!automatic)
            {
                file = EnvironmentProfile.Load(full);
            }
            else if (LoadAtStartup(full) is { } loaded)
            {
                file = loaded;
            }
            else
            {
                return false;
            }
        }
        catch (EnvironmentFileException ex)
        {
            // Fichier refusé : signalé une fois par version du fichier.
            var hash = Hash(full);
            if (automatic && hash is not null && settings.EnvironmentFileHashes.GetValueOrDefault(key) == hash)
            {
                return false;
            }

            DebugLog.Write("env", $"Fichier d'environnement refusé : {full} ({ex.Message})");
            ShowError(owner, full, Describe(ex));
            if (hash is not null)
            {
                settings.EnvironmentFileHashes[key] = hash;
                Save(settings);
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Partage indisponible au démarrage : on réessaiera au prochain.
            DebugLog.Write("env", $"Fichier d'environnement illisible : {full}", ex);
            if (!automatic)
            {
                ShowError(owner, full, ex.Message);
            }

            return false;
        }

        if (automatic && settings.EnvironmentFileHashes.GetValueOrDefault(key) == file.Sha256)
        {
            return false;
        }

        bool applied = Confirm(owner, settings, full, file, automatic, Strings.EnvMessage);
        if (applied)
        {
            file.Profile.ApplyTo(settings);
        }

        settings.EnvironmentFileHashes[key] = file.Sha256;
        Save(settings);
        return applied;
    }

    /// <summary>
    /// Montre les changements du fichier par rapport à <paramref name="settings"/> et demande l'accord (le PVWA et les
    /// PSMP reçoivent le mot de passe CyberArk) ; sans changement, le dit quand le fichier a été choisi. N'applique rien.
    /// </summary>
    /// <returns>Vrai si l'utilisateur accepte les changements.</returns>
    private static bool Confirm(Window? owner, AppSettings settings, string full, EnvironmentFile file, bool automatic, string message)
    {
        var changes = file.Profile.Diff(settings);
        var applicable = changes.Where(c => !c.Ignored).ToList();
        var ignored = changes.Where(c => c.Ignored)
            .Select(c => Text.Format(c.NotPsmp ? Strings.EnvHostKeyNotPsmp : Strings.EnvHostKeyIgnored, c.Detail)).ToList();
        bool accepted = false;
        if (applicable.Count > 0)
        {
            bool sensitive = applicable.Any(c => c.Sensitive);
            accepted = ConfirmDialog.Confirm(owner, new ConfirmRequest
            {
                Title = Strings.EnvTitle,
                Heading = string.IsNullOrWhiteSpace(file.Profile.Name) ? Strings.EnvHeading : Text.Format(Strings.EnvHeadingNamed, ShortName(file.Profile.Name)),
                Subject = full,
                Message = message,
                Items = applicable.Select(Line).ToList(),
                Bullets = ignored,
                Codes = [("SHA-256", file.Sha256)],
                Kind = sensitive ? ConfirmKind.Warning : ConfirmKind.Question,
                Banner = sensitive ? Strings.EnvSensitiveBanner : null,
                Acknowledge = sensitive ? Strings.EnvAcknowledge : null,
                Actions = [Strings.EnvApply],
                CancelLabel = Strings.EnvSkip,
            });
            DebugLog.Write("env", $"Environnement {(accepted ? "accepté" : "non appliqué")} : {full} (SHA-256 {file.Sha256}, {applicable.Count} changement(s))");
        }
        else if (!automatic)
        {
            ConfirmDialog.Ask(owner, new ConfirmRequest
            {
                Title = Strings.EnvTitle,
                Heading = Strings.EnvNothingToChange,
                Subject = full,
                Bullets = ignored,
                Codes = [("SHA-256", file.Sha256)],
                Kind = ignored.Count > 0 ? ConfirmKind.Warning : ConfirmKind.Info,
                Actions = [],
                CancelLabel = Strings.Ok,
            });
        }
        else if (ignored.Count > 0)
        {
            DebugLog.Write("env", $"{full} : {string.Join(" ; ", ignored)}");
        }

        return accepted;
    }

    /// <summary>
    /// Fichier choisi dans les Paramètres : ses changements par rapport aux valeurs affichées (<paramref name="settings"/>)
    /// sont montrés. Accepté, il est rendu pour être appliqué à « Enregistrer » ; rien n'est appliqué ni enregistré ici.
    /// </summary>
    internal static EnvironmentFile? Review(Window owner, AppSettings settings, string path)
    {
        var full = path.Trim();
        EnvironmentFile file;
        try
        {
            file = EnvironmentProfile.Load(full);
        }
        catch (EnvironmentFileException ex)
        {
            DebugLog.Write("env", $"Fichier d'environnement refusé : {full} ({ex.Message})");
            ShowError(owner, full, Describe(ex));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            DebugLog.Write("env", $"Fichier d'environnement illisible : {full}", ex);
            ShowError(owner, full, ex.Message);
            return null;
        }

        return Confirm(owner, settings, full, file, automatic: false, Strings.EnvMessageOnSave) ? file : null;
    }

    /// <summary>Attente maximale d'un fichier d'environnement au démarrage.</summary>
    internal static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lecture au démarrage, hors du fil de l'interface et limitée à <see cref="StartupWait"/> : un partage réseau
    /// injoignable (poste hors VPN) ferait sinon attendre l'ouverture de ZillaTerm pendant des dizaines de secondes.
    /// Null si le fichier est absent ou trop lent à lire (on réessaiera au prochain démarrage).
    /// </summary>
    private static EnvironmentFile? LoadAtStartup(string path)
    {
        var load = Task.Run(() => File.Exists(path) ? EnvironmentProfile.Load(path) : null);
        if (!((IAsyncResult)load).AsyncWaitHandle.WaitOne(StartupWait))
        {
            DebugLog.Write("env", $"Fichier d'environnement trop lent à lire, ignoré à ce démarrage : {path}");
            // Lecture abandonnée : son éventuelle erreur est observée, sans autre suite.
            load.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return null;
        }

        return load.GetAwaiter().GetResult();
    }

    /// <summary>Une ligne du récapitulatif : « Réglage : ancienne valeur → nouvelle valeur ».</summary>
    internal static string Line(EnvironmentChange change)
    {
        var label = change.Setting switch
        {
            EnvironmentSetting.PvwaUrl => Strings.EnvPvwa,
            EnvironmentSetting.AuthMethod => Strings.EnvAuthMethod,
            EnvironmentSetting.DefaultPsmp => Strings.EnvDefaultPsmp,
            EnvironmentSetting.PsmpServers => Strings.EnvPsmpServers,
            EnvironmentSetting.WindowsComponent => Strings.EnvWindowsComponent,
            EnvironmentSetting.PlatformComponent => Text.Format(Strings.EnvPlatformComponent, change.Detail),
            EnvironmentSetting.SharedList => change.Detail is { } server ? Text.Format(Strings.EnvSharedListNetwork, server) : Strings.EnvSharedList,
            EnvironmentSetting.HostKey => Text.Format(Strings.EnvHostKey, change.Detail),
            EnvironmentSetting.CentralFile => Strings.EnvCentralFile,
            EnvironmentSetting.KeepPvwaSessionAlive => Strings.EnvKeepAlive,
            EnvironmentSetting.KeepAccountListCopy => Strings.EnvKeepAccountListCopy,
            EnvironmentSetting.SshInApp => Strings.EnvSshInApp,
            EnvironmentSetting.PsmDesktopInTab => Strings.EnvPsmDesktopInTab,
            EnvironmentSetting.PsmRedirectDrives => Strings.EnvPsmRedirectDrives,
            EnvironmentSetting.PsmShareFoldersOnly => Strings.EnvPsmShareFoldersOnly,
            EnvironmentSetting.PsmSharedFolders => change.Detail is { } folderServer
                ? Text.Format(Strings.EnvPsmSharedFoldersNetwork, folderServer)
                : Strings.EnvPsmSharedFolders,
            EnvironmentSetting.PauseRdpDisplay => Strings.EnvPauseRdpDisplay,
            EnvironmentSetting.CheckForUpdates => Strings.EnvCheckForUpdates,
            EnvironmentSetting.DutyText => Strings.EnvDutyText,
            EnvironmentSetting.DutyTextFile => change.Detail is { } dutyServer ? Text.Format(Strings.EnvDutyFileNetwork, dutyServer) : Strings.EnvDutyFile,
            EnvironmentSetting.ServerTags => Strings.EnvServerTags,
            EnvironmentSetting.InterventionReminders => Strings.EnvInterventionReminders,
            EnvironmentSetting.SessionSharing => Strings.EnvSessionSharing,
            EnvironmentSetting.ShareDirect => Strings.EnvShareDirect,
            EnvironmentSetting.SharePort => Strings.EnvSharePort,
            EnvironmentSetting.ShareRelayFolder => change.Detail is { } relayServer ? Text.Format(Strings.EnvShareRelayNetwork, relayServer) : Strings.EnvShareRelay,
            _ => Strings.EnvUploadProtocol,
        };
        if (change.Setting is EnvironmentSetting.DutyText or EnvironmentSetting.InterventionReminders)
        {
            // Texte de plusieurs lignes : son début, sur une ligne (le texte complet s'affiche ensuite dans la fenêtre Intervention).
            return change.Current.Length == 0
                ? Text.Format(Strings.EnvLineNew, label, ShortName(change.New))
                : Text.Format(Strings.EnvLineChange, label, ShortName(change.Current), change.New.Length == 0 ? Strings.EnvNone : ShortName(change.New));
        }

        var next = Value(change.New);
        return change.Current.Length == 0
            ? Text.Format(Strings.EnvLineNew, label, next)
            : Text.Format(Strings.EnvLineChange, label, Value(change.Current), next);
    }

    /// <summary>Nom de l'environnement sur une ligne, tronqué (il vient du fichier).</summary>
    private static string ShortName(string name)
    {
        var line = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..60] + "…";
    }

    private static string Value(string value) => value switch
    {
        "" => Strings.EnvNone,
        "true" => Strings.EnvYes,
        "false" => Strings.EnvNo,
        _ => value,
    };

    private static string Describe(EnvironmentFileException ex) => Text.Format(ex.Problem switch
    {
        EnvironmentProblem.TooLarge => Strings.EnvTooLarge,
        EnvironmentProblem.InvalidJson => Strings.EnvInvalidJson,
        EnvironmentProblem.UnsupportedFormat => Strings.EnvUnsupportedFormat,
        EnvironmentProblem.InvalidPvwa => Strings.EnvInvalidPvwa,
        EnvironmentProblem.InvalidPsmpAddress => Strings.EnvInvalidPsmp,
        EnvironmentProblem.InvalidPort => Strings.EnvInvalidPort,
        EnvironmentProblem.InvalidDomain => Strings.EnvInvalidDomain,
        EnvironmentProblem.DuplicateDomain => Strings.EnvDuplicateDomain,
        EnvironmentProblem.InvalidComponent => Strings.EnvInvalidComponent,
        EnvironmentProblem.InvalidHostKey => Strings.EnvInvalidHostKey,
        EnvironmentProblem.InvalidDutyText => Strings.EnvInvalidDutyText,
        EnvironmentProblem.InvalidServerTag => Strings.EnvInvalidServerTag,
        EnvironmentProblem.InvalidReminder => Strings.EnvInvalidReminder,
        EnvironmentProblem.InvalidSharedFolder => Strings.EnvInvalidSharedFolder,
        _ => Strings.EnvInvalidPath,
    }, ex.Detail);

    private static void ShowError(Window? owner, string path, string reason) => ConfirmDialog.Ask(owner, new ConfirmRequest
    {
        Title = Strings.EnvTitle,
        Heading = Strings.EnvRefused,
        Subject = path,
        Message = reason,
        Kind = ConfirmKind.Danger,
        Actions = [],
        CancelLabel = Strings.Ok,
    });

    private static string? Hash(string path)
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(AppSettings settings)
    {
        try
        {
            settings.Save(AppSettings.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("env", "Réglages non enregistrés", ex);
        }
    }
}
