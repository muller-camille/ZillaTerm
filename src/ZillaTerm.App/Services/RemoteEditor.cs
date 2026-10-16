using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Services;

/// <summary>
/// « Modifier » de l'onglet Fichiers : copie le fichier du serveur dans un dossier temporaire, l'ouvre dans
/// l'éditeur de texte choisi par l'utilisateur et, à chaque enregistrement, propose de le renvoyer sur le serveur
/// (par SFTP, droits conservés ; alerte si le fichier a changé sur le serveur entre-temps).
/// Une instance par session SSH ; les copies locales sont supprimées à la fermeture de la session.
/// </summary>
public sealed class RemoteEditor : IDisposable
{
    /// <summary>Au-delà, on demande confirmation avant d'ouvrir le fichier dans un éditeur de texte.</summary>
    private const long LargeFile = 20L * 1024 * 1024;

    private static string Root => PrivateTemp.Combine("edit");

    private readonly RemoteSession _session;
    private readonly Window _owner;
    private readonly AppSettings _settings;
    private readonly Action<string, bool> _status;
    private readonly Action<string> _remoteChanged;
    private readonly string _directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
    private readonly List<EditedFile> _files = [];
    private readonly HashSet<string> _opening = new(StringComparer.Ordinal);
    private readonly HashSet<EditedFile> _busy = [];
    private readonly HashSet<EditedFile> _savedAgain = [];

    // Renvois en cours d'écriture sur le serveur, et leur fin attendue par la fermeture de la connexion.
    private readonly HashSet<EditedFile> _writing = [];
    private TaskCompletionSource? _writesDone;
    private bool _keepCopies;
    private bool _disposed;

    /// <param name="status">Message pour la barre d'état (texte, erreur).</param>
    /// <param name="remoteChanged">Dossier du serveur dont le contenu vient de changer (pour l'actualiser).</param>
    public RemoteEditor(RemoteSession session, Window owner, AppSettings settings, Action<string, bool> status, Action<string> remoteChanged)
    {
        _session = session;
        _owner = owner;
        _settings = settings;
        _status = status;
        _remoteChanged = remoteChanged;
    }

    /// <summary>
    /// Fichiers modifiés localement mais pas encore renvoyés sur le serveur. Rapide : chaque copie n'est relue que si elle
    /// a changé depuis sa dernière lecture par la surveillance (enregistrement de l'instant).
    /// </summary>
    /// <remarks>Un fichier en cours de renvoi n'y figure pas : la fermeture attend la fin de son écriture (<see cref="WritesDone"/>).</remarks>
    public IReadOnlyList<EditedFile> Unsent => _files.Where(f => !_writing.Contains(f) && f.HasUnsentChanges).ToList();

    /// <summary>Fichiers dont le renvoi s'écrit en ce moment sur le serveur.</summary>
    public IReadOnlyList<EditedFile> Writing => _writing.ToList();

    /// <summary>
    /// Fin des renvois en cours (aussitôt s'il n'y en a pas) : leur connexion ne doit pas être fermée avant, car l'écriture,
    /// faite sur place pour garder propriétaire, droits et liens du fichier, le laisserait tronqué.
    /// </summary>
    public Task WritesDone =>
        _writing.Count == 0 ? Task.CompletedTask : (_writesDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>
    /// Fermeture sans attendre la fin d'un renvoi bloqué (serveur qui ne répond plus) : les copies de cette session restent
    /// sur le poste, supprimées au plus tôt un jour après (<see cref="CleanupStale"/>). Renvoie celles des fichiers en cours
    /// de renvoi.
    /// </summary>
    public IReadOnlyList<string> KeepWritingCopies()
    {
        _keepCopies |= _writing.Count > 0;
        return [.. _writing.Select(f => f.LocalPath)];
    }

    /// <summary>Renvois en cours de ces sessions, sous la forme « serveur:chemin ».</summary>
    public static List<string> WritingFiles(IEnumerable<RemoteEditor> editors) =>
        editors.SelectMany(e => e.Writing.Select(f => $"{e._session.Label}:{f.DisplayPath}")).ToList();

    public async Task EditAsync(RemoteEntry entry)
    {
        // Double-clic puis Entrée (ou F4) pendant la copie : une seule copie locale, ouverte par la première demande.
        if (!_opening.Add(entry.FullPath))
        {
            return;
        }

        try
        {
            if (_files.FirstOrDefault(f => f.RemotePath == entry.FullPath) is { } open)
            {
                await ReopenAsync(open, entry);
            }
            else
            {
                await OpenNewAsync(entry);
            }
        }
        finally
        {
            _opening.Remove(entry.FullPath);
        }
    }

    /// <summary>
    /// Fichier déjà ouvert : il est téléchargé de nouveau à chaque fois (la date du serveur, souvent à la seconde près, ne
    /// suffit pas), et l'éditeur rouvert sur la même copie, remplacée si le contenu du serveur est différent.
    /// </summary>
    private async Task ReopenAsync(EditedFile file, RemoteEntry entry)
    {
        // Question ou renvoi en cours pour ce fichier : la copie n'est pas touchée.
        if (_busy.Add(file))
        {
            try
            {
                await RefreshAsync(file, entry);
            }
            finally
            {
                _busy.Remove(file);
                // Enregistré dans l'éditeur pendant la vérification : le renvoi est proposé maintenant.
                if (_savedAgain.Remove(file))
                {
                    _ = OnSavedAsync(file);
                }
            }
        }

        if (!_disposed && _files.Contains(file))
        {
            LaunchEditor(file.LocalPath);
        }
    }

    /// <summary>Nouvelle copie du serveur ; remplace la copie si son contenu diffère (avec accord si la copie a des modifications non renvoyées).</summary>
    private async Task RefreshAsync(EditedFile file, RemoteEntry entry)
    {
        // Renvoi coupé : c'est notre écriture partielle qui a changé le fichier du serveur, la copie est celle à renvoyer.
        if (file.WriteInterrupted)
        {
            return;
        }

        _status(Text.Format(Strings.EditOpening, file.DisplayName), false);
        string? fresh = null;
        try
        {
            var browser = await _session.GetBrowserAsync();
            // Date et taille relevées avant la copie, comme à l'ouverture : un changement pendant la copie sera signalé au renvoi.
            var (time, length) = await browser.GetStatAsync(file.RemotePath, CancellationToken.None);
            // À côté de la copie (même dossier : remplacement d'un seul coup) ; un nom commençant par un point n'est jamais
            // celui d'une copie (LocalCopyName les retire).
            fresh = Path.Combine(Path.GetDirectoryName(file.LocalPath)!, "." + Guid.NewGuid().ToString("N")[..8]);
            var check = await browser.DownloadAsync(entry, fresh, null, CancellationToken.None);
            if (!check.Matches)
            {
                throw CopyRejected(check);
            }

            if (_disposed || !_files.Contains(file))
            {
                return;
            }

            // Modifications non renvoyées (y compris un enregistrement fait pendant la copie) : jamais écrasées sans accord.
            var result = await Task.Run(() => file.Refresh(fresh, time, length, keepUnsent: true));
            if (result == EditedFile.RefreshResult.Conflict && !_disposed && ConfirmDialog.Confirm(_owner, new ConfirmRequest
                {
                    Title = Strings.FileEdit.Replace("_", ""),
                    Heading = Text.Format(Strings.EditReloadHeading, file.DisplayName),
                    Subject = $"{_session.Label}:{file.DisplayPath}",
                    Message = Strings.EditReloadMessage,
                    Kind = ConfirmKind.Warning,
                    Actions = [Strings.EditReloadAction],
                    DangerAction = 0,
                    CancelLabel = Strings.EditKeepMine,
                }) && !_disposed && _files.Contains(file))
            {
                result = await Task.Run(() => file.Refresh(fresh, time, length, keepUnsent: false));
            }

            _status(Text.Format(result == EditedFile.RefreshResult.Replaced ? Strings.EditRefreshed : Strings.EditOpened, file.DisplayName), false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Serveur injoignable, fichier supprimé, copie verrouillée par l'éditeur : l'ancienne copie est rouverte.
            _status(Text.Format(Strings.EditStaleCopy, file.DisplayName, ErrorText.Describe(ex)), true);
        }
        finally
        {
            if (fresh is not null)
            {
                try
                {
                    File.Delete(fresh);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Supprimé avec le dossier de la session.
                }
            }
        }
    }

    /// <summary>Copie différente de l'original ou non vérifiée : jamais ouverte, son enregistrement remplacerait le fichier du serveur.</summary>
    private static IOException CopyRejected(TransferCheck check) =>
        new(check.Verified ? Text.Format(Strings.TransferMismatch, 1) : $"{Text.Format(Strings.TransferNotVerified, 1)} ({check.Error})");

    /// <summary>Vrai si le fichier est assez petit, ou si l'utilisateur accepte de l'ouvrir quand même.</summary>
    private bool ConfirmSize(RemoteEntry entry, long length) =>
        length <= LargeFile || ConfirmDialog.Confirm(_owner, new ConfirmRequest
        {
            Title = Strings.FileEdit.Replace("_", ""),
            Heading = Strings.EditLargeHeading,
            Subject = $"{entry.DisplayName} — {RemotePath.FormatSize(length)}",
            Message = Strings.EditLargeMessage,
            Actions = [Strings.ActionOpen],
        });

    private async Task OpenNewAsync(RemoteEntry entry)
    {
        if (!ConfirmSize(entry, entry.Length))
        {
            return;
        }

        _status(Text.Format(Strings.EditOpening, entry.DisplayName), false);
        EditedFile file;
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var browser = await _session.GetBrowserAsync();
            Directory.CreateDirectory(folder);
            var local = Path.Combine(folder, EditedFile.LocalCopyName(entry.Name));
            // Date et taille relevées avant la copie : un changement sur le serveur pendant la copie sera signalé au renvoi.
            var (time, length) = await browser.GetStatAsync(entry.FullPath, CancellationToken.None);
            // Taille réelle (lien symbolique suivi) : celle de la liste peut être celle du lien, ou dater.
            if (length > entry.Length && entry.Length <= LargeFile && !ConfirmSize(entry, length))
            {
                TryDeleteFolder(folder);
                _status("", false);
                return;
            }

            var check = await browser.DownloadAsync(entry, local, null, CancellationToken.None);
            if (!check.Matches)
            {
                throw CopyRejected(check);
            }

            // Copie lue et hachée hors du fil de l'interface (fichier volumineux accepté plus haut).
            file = await Task.Run(() => new EditedFile(entry.FullPath, local, time, length));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDeleteFolder(folder);
            _status(Text.Format(Strings.CannotOpen, entry.DisplayName, ErrorText.Describe(ex)), true);
            return;
        }

        if (_disposed)
        {
            file.Dispose();
            return;
        }

        file.Saved += f => _owner.Dispatcher.BeginInvoke(() => { _ = OnSavedAsync(f); });
        _files.Add(file);
        if (LaunchEditor(file.LocalPath))
        {
            _status(Text.Format(Strings.EditOpened, entry.DisplayName), false);
        }
    }

    /// <summary>Faux seulement si le fichier du serveur a encore la date et la taille relevées à l'ouverture (ou au dernier envoi).</summary>
    private async Task<bool> MayHaveWrittenAsync(EditedFile file)
    {
        // Connexion perdue : pas de nouvelle connexion (ni question MFA) pour ce contrôle, on suppose le pire.
        if (_session.OpenedBrowser is not { } browser)
        {
            return true;
        }

        try
        {
            var (time, length) = await browser.GetStatAsync(file.RemotePath, CancellationToken.None);
            return time != file.RemoteWriteTime || length != file.RemoteLength;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    private static void TryDeleteFolder(string folder)
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
            // Nettoyé au prochain démarrage.
        }
    }

    /// <summary>Demande à l'utilisateur s'il faut renvoyer ce qui n'a pas été envoyé ; vrai si l'on peut fermer.</summary>
    /// <param name="unsent">Fichiers pas encore renvoyés (<see cref="UnsentFiles"/>).</param>
    public static bool ConfirmClose(Window owner, IReadOnlyList<string> unsent) =>
        unsent.Count == 0
        || ConfirmDialog.Confirm(owner, new ConfirmRequest
        {
            Title = Strings.EditPendingTitle,
            Heading = Strings.EditPendingHeading,
            Message = Strings.EditPendingMessage,
            Items = unsent,
            Kind = ConfirmKind.Warning,
            Actions = [Strings.EditCloseWithoutSending],
            DangerAction = 0,
        });

    /// <summary>Fichiers modifiés et pas encore renvoyés, sous la forme « serveur:chemin ».</summary>
    public static List<string> UnsentFiles(IEnumerable<RemoteEditor> editors) =>
        editors.SelectMany(e => e.Unsent.Select(f => $"{e._session.Label}:{f.DisplayPath}")).ToList();

    /// <summary>Supprime les copies laissées par une session qui n'a pas pu se fermer normalement (plus d'un jour).</summary>
    public static void CleanupStale()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var file in _files)
        {
            file.Dispose();
        }

        _files.Clear();
        try
        {
            if (!_keepCopies && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fichier encore ouvert par l'éditeur : supprimé au prochain démarrage (CleanupStale).
        }
    }

    private bool LaunchEditor(string path)
    {
        var editor = _settings.TextEditor.Trim().Trim('"');
        if (editor.Length == 0)
        {
            // Chemin complet, comme pour mstsc : pas un « notepad.exe » du dossier courant.
            editor = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(_owner, Text.Format(Strings.EditorLaunchFailed, editor, ex.Message), Strings.FileEdit.Replace("_", ""),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task OnSavedAsync(EditedFile file)
    {
        if (_disposed || !_files.Contains(file))
        {
            return;
        }

        if (!_busy.Add(file))
        {
            // Question ou envoi déjà en cours pour ce fichier : on y reviendra ensuite.
            _savedAgain.Add(file);
            return;
        }

        try
        {
            do
            {
                _savedAgain.Remove(file);
                // Copie relue (si besoin) et hachée hors du fil de l'interface.
                if (!await file.HasUnsentChangesAsync() || _disposed)
                {
                    break;
                }

                // Après un enregistrement dans l'éditeur, le renvoi est attendu : Entrée renvoie.
                if (!ConfirmDialog.Confirm(_owner, new ConfirmRequest
                    {
                        Title = Strings.EditUploadTitle,
                        Heading = Text.Format(Strings.EditUploadHeading, file.DisplayName),
                        Subject = $"{_session.Label}:{file.DisplayPath}",
                        Message = Strings.EditUploadMessage,
                        Actions = [Strings.EditUploadAction],
                        DefaultAction = 0,
                        CancelLabel = Strings.NotNow,
                    }))
                {
                    break;
                }

                if (!await UploadAsync(file))
                {
                    break;
                }
            }
            while (_savedAgain.Contains(file) && !_disposed);
        }
        finally
        {
            _busy.Remove(file);
            _savedAgain.Remove(file);
        }
    }

    /// <summary>Renvoie la copie sur le serveur ; faux si l'utilisateur y renonce ou si l'envoi échoue (message affiché).</summary>
    internal async Task<bool> UploadAsync(EditedFile file)
    {
        bool writing = false;
        try
        {
            var browser = await _session.GetBrowserAsync();
            bool changed;
            try
            {
                var (time, length) = await browser.GetStatAsync(file.RemotePath, CancellationToken.None);
                changed = time != file.RemoteWriteTime || length != file.RemoteLength;
            }
            catch (Renci.SshNet.Common.SftpPathNotFoundException)
            {
                changed = true;
            }

            // Après un envoi coupé, c'est notre propre écriture partielle qui a changé le fichier : on le remplace.
            if (changed && !file.WriteInterrupted && !ConfirmDialog.Confirm(_owner, new ConfirmRequest
                {
                    Title = Strings.EditUploadTitle,
                    Heading = Text.Format(Strings.EditRemoteChangedHeading, file.DisplayName),
                    Subject = $"{_session.Label}:{file.DisplayPath}",
                    Message = Strings.EditRemoteChangedMessage,
                    Kind = ConfirmKind.Warning,
                    Actions = [Strings.EditReplaceAnyway],
                    DangerAction = 0,
                }))
            {
                return false;
            }

            _status(Text.Format(Strings.EditUploading, file.DisplayName), false);
            // Lecture et hachage de la copie hors du fil de l'interface.
            var content = await Task.Run(() => EditedFile.ReadAllBytesShared(file.LocalPath));
            // Fermer l'onglet, se déconnecter ou quitter attend la fin de l'écriture, et du message d'un échec (WritesDone) :
            // la connexion coupée en pleine écriture laisserait le fichier du serveur tronqué.
            _writing.Add(file);
            writing = true;
            var (newTime, newLength) = await browser.WriteFileAsync(file.RemotePath, content, CancellationToken.None);
            writing = false;
            await Task.Run(() => file.MarkSent(content, newTime, newLength));
            _status(Text.Format(Strings.EditUploaded, file.DisplayName), false);
            _remoteChanged(RemotePath.Parent(file.RemotePath));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = ex switch
            {
                Renci.SshNet.Common.SftpPermissionDeniedException => Strings.PermissionDenied,
                _ => ErrorText.Describe(ex),
            };
            // Écriture refusée avant d'avoir commencé (droits, dossier absent) : le fichier du serveur n'a pas bougé, et le
            // contrôle « modifié sur le serveur » reste actif pour le prochain envoi.
            writing = writing && await MayHaveWrittenAsync(file);
            if (writing)
            {
                file.MarkWriteInterrupted();
            }

            _status(Text.Format(Strings.UploadFailed, file.DisplayName, message), true);
            // Onglet fermé pendant l'écriture (la fermeture l'attendait) : plus de renvoi possible depuis cette session.
            var text = !writing ? Strings.EditUploadFailed : _disposed ? Strings.EditWriteInterruptedClosed : Strings.EditWriteInterrupted;
            MessageBox.Show(_owner, Text.Format(text, file.DisplayName, message), Strings.EditUploadTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            if (_writing.Remove(file) && _writing.Count == 0)
            {
                _writesDone?.TrySetResult();
                _writesDone = null;
            }
        }
    }
}
