using System.Security.Cryptography;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.KeePass;

/// <summary>
/// Coffre KeePass ouvert depuis un fichier, en lecture et en écriture. Chaque modification relit le fichier, s'applique
/// à son contenu du moment (les changements faits ailleurs entre-temps sont gardés), est vérifiée en relisant le
/// résultat, puis remplace le fichier d'un coup ; la version précédente, gardée dans « fichier.kdbx.bak » le temps du
/// remplacement, est supprimée dès que le fichier en place est relu à l'identique.
/// </summary>
public sealed class KeePassVault : IDisposable
{
    private const int SaveAttempts = 3;

    private readonly KeePassKey _key;
    private readonly TransformCache _cache;

    // Une seule opération sur le fichier à la fois (jamais libéré : sans AvailableWaitHandle, rien à rendre).
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Protège Database, _disposed et _operations : les mots de passe sont lus depuis d'autres threads (connexions
    // SSH) pendant qu'un enregistrement remplace la base, et le coffre peut être verrouillé pendant un enregistrement.
    private readonly object _state = new();
    private bool _disposed;
    private int _operations;

    private KeePassVault(string path, KeePassKey key, TransformCache cache, KeePassDatabase database)
    {
        FilePath = path;
        _key = key;
        _cache = cache;
        Database = database;
    }

    public string FilePath { get; }

    /// <summary>Contenu lu au dernier chargement ou enregistrement (vide une fois le coffre verrouillé).</summary>
    public KeePassDatabase Database { get; private set; }

    /// <summary>
    /// Mot de passe de l'entrée, ou null si elle n'existe plus ou si le coffre a été verrouillé. Utilisable depuis
    /// n'importe quel thread, même pendant un enregistrement.
    /// </summary>
    public string? RevealPassword(string entryId)
    {
        lock (_state)
        {
            return _disposed ? null : Database.RevealPassword(entryId);
        }
    }

    public string BackupPath => FilePath + ".bak";

    /// <summary>Ouvre le coffre ; la clé appartient ensuite au coffre (libérée avec lui), même en cas d'échec.</summary>
    public static async Task<KeePassVault> OpenAsync(string path, KeePassKey key, CancellationToken cancellation = default)
    {
        var cache = new TransformCache();
        try
        {
            var bytes = await ReadSharedAsync(path, cancellation).ConfigureAwait(false);
            try
            {
                var database = await Task.Run(() => KdbxFile.Read(bytes, key, cache, cancellation), cancellation).ConfigureAwait(false);
                return new KeePassVault(path, key, cache, database);
            }
            finally
            {
                // KDBX 3.1 : l'en-tête du fichier contient la clé qui masque les mots de passe en mémoire.
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch
        {
            cache.Dispose();
            key.Dispose();
            throw;
        }
    }

    /// <summary>Relit le fichier (après une modification faite par un autre programme).</summary>
    public async Task ReloadAsync(CancellationToken cancellation = default)
    {
        await BeginAsync(cancellation).ConfigureAwait(false);
        try
        {
            var bytes = await ReadSharedAsync(FilePath, cancellation).ConfigureAwait(false);
            try
            {
                var database = await Task.Run(() => KdbxFile.Read(bytes, _key, _cache, cancellation), cancellation).ConfigureAwait(false);
                Replace(database);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Applique <paramref name="change"/> au contenu actuel du fichier et enregistre. Si le fichier change pendant
    /// l'enregistrement, tout est refait sur la nouvelle version ; rien n'est écrit si la vérification échoue.
    /// </summary>
    public async Task<T> SaveAsync<T>(Func<KeePassDatabase, T> change, CancellationToken cancellation = default)
    {
        await BeginAsync(cancellation).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < SaveAttempts; attempt++)
            {
                var original = await ReadSharedAsync(FilePath, cancellation).ConfigureAwait(false);
                byte[]? output = null;
                try
                {
                    (var database, var result, output) = await Task.Run(() => Prepare(original, change, cancellation), cancellation).ConfigureAwait(false);
                    try
                    {
                        if (!await WriteAsync(original, output, cancellation).ConfigureAwait(false))
                        {
                            // Un autre programme a enregistré entre-temps : on recommence sur sa version.
                            database.Dispose();
                            continue;
                        }
                    }
                    catch
                    {
                        database.Dispose();
                        throw;
                    }

                    Replace(database);
                    return result;
                }
                finally
                {
                    // KDBX 3.1 : l'en-tête du fichier contient la clé qui masque les mots de passe en mémoire.
                    CryptographicOperations.ZeroMemory(original);
                    if (output is not null)
                    {
                        CryptographicOperations.ZeroMemory(output);
                    }
                }
            }

            throw new KeePassException(KeePassError.Busy, CoreStrings.KeePassBusy);
        }
        finally
        {
            End();
        }
    }

    public Task SaveAsync(Action<KeePassDatabase> change, CancellationToken cancellation = default) =>
        SaveAsync(db =>
        {
            change(db);
            return true;
        }, cancellation);

    /// <summary>
    /// Verrouille le coffre : secrets effacés tout de suite s'il est inactif, sinon dès la fin de l'enregistrement en
    /// cours (qui a besoin de la clé pour finir d'écrire le fichier).
    /// </summary>
    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_operations > 0)
            {
                return;
            }
        }

        ReleaseSecrets();
    }

    private async Task BeginAsync(CancellationToken cancellation)
    {
        await _lock.WaitAsync(cancellation).ConfigureAwait(false);
        lock (_state)
        {
            if (!_disposed)
            {
                _operations++;
                return;
            }
        }

        _lock.Release();
        throw new ObjectDisposedException(nameof(KeePassVault));
    }

    private void End()
    {
        bool release;
        lock (_state)
        {
            _operations--;
            release = _disposed && _operations == 0;
        }

        _lock.Release();
        if (release)
        {
            ReleaseSecrets();
        }
    }

    /// <summary>Met en place la base qui vient d'être lue ou écrite ; si le coffre a été verrouillé entre-temps, elle est effacée.</summary>
    private void Replace(KeePassDatabase database)
    {
        KeePassDatabase old;
        lock (_state)
        {
            if (_disposed)
            {
                old = database;
            }
            else
            {
                old = Database;
                Database = database;
            }
        }

        old.Dispose();
    }

    private void ReleaseSecrets()
    {
        lock (_state)
        {
            Database.Dispose();
        }

        _cache.Dispose();
        _key.Dispose();
    }

    private (KeePassDatabase Database, T Result, byte[] Output) Prepare<T>(byte[] original, Func<KeePassDatabase, T> change, CancellationToken cancellation)
    {
        var database = KdbxFile.Read(original, _key, _cache, cancellation);
        try
        {
            var result = change(database);
            KdbxFile.RenewKdfSeed(database, _key, _cache, cancellation);
            var output = KdbxFile.Write(database);
            Verify(database, output, cancellation);
            return (database, result, output);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>Relit le fichier produit : il doit se déchiffrer avec la même clé et contenir les mêmes entrées.</summary>
    private void Verify(KeePassDatabase expected, byte[] output, CancellationToken cancellation)
    {
        try
        {
            using var check = KdbxFile.Read(output, _key, _cache, cancellation);
            var want = expected.Entries.Select(e => (e.Id, e.Title, e.Modified)).Order().ToList();
            var got = check.Entries.Select(e => (e.Id, e.Title, e.Modified)).Order().ToList();
            if (!want.SequenceEqual(got) || check.Groups.Count != expected.Groups.Count)
            {
                throw new KeePassException(KeePassError.Corrupted, CoreStrings.KeePassVerifyFailed);
            }
        }
        catch (KeePassException e) when (e.Message != CoreStrings.KeePassVerifyFailed)
        {
            throw new KeePassException(KeePassError.Corrupted, CoreStrings.KeePassVerifyFailed, e);
        }
    }

    /// <summary>
    /// Écrit dans un fichier temporaire du même dossier puis le substitue à l'original (gardé en .bak le temps du
    /// remplacement) ; faux si l'original a changé depuis <paramref name="original"/>.
    /// </summary>
    private async Task<bool> WriteAsync(byte[] original, byte[] output, CancellationToken cancellation)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        var temp = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(output, cancellation).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var current = await ReadSharedAsync(FilePath, cancellation).ConfigureAwait(false);
            bool unchanged = current.AsSpan().SequenceEqual(original);
            CryptographicOperations.ZeroMemory(current);
            if (!unchanged)
            {
                return false;
            }

            Install(temp, FilePath, BackupPath);
            await RemoveBackupAsync(FilePath, BackupPath, output, original).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Met <paramref name="temp"/> à la place de <paramref name="path"/>, dont la version précédente devient
    /// <paramref name="backup"/>. À aucun moment le coffre ne disparaît : au pire il reste à son ancienne version.
    /// </summary>
    /// <param name="replace">Remplacement atomique (File.Replace) ; remplaçable pour les tests.</param>
    internal static void Install(string temp, string path, string backup, Action<string, string, string>? replace = null)
    {
        try
        {
            (replace ?? ((t, p, b) => File.Replace(t, p, b, ignoreMetadataErrors: true)))(temp, path, backup);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            if (File.Exists(path))
            {
                // Système de fichiers sans remplacement atomique (certains partages réseau) : copie de sauvegarde puis déplacement.
                File.Copy(path, backup, overwrite: true);
                File.Move(temp, path, overwrite: true);
                return;
            }

            // ReplaceFile a renommé l'original en sauvegarde avant d'échouer : il reste à mettre le nouveau en place,
            // ou, si c'est impossible, à remettre l'original.
            try
            {
                File.Move(temp, path);
            }
            catch (Exception) when (File.Exists(backup) && !File.Exists(path))
            {
                File.Copy(backup, path);
                throw;
            }
        }
    }

    /// <summary>
    /// Supprime la copie de sécurité si le fichier en place est bien <paramref name="written"/> (déjà vérifié) et si la
    /// copie est bien <paramref name="original"/>, la version modifiée : elle ne protège alors que d'un remplacement
    /// interrompu, et resterait sinon sur le partage, où elle s'ouvre avec l'ancien mot de passe maître. Fichier en place
    /// différent (modifié entre-temps), copie différente (un autre poste a enregistré juste avant le remplacement : sa
    /// version n'est plus que là) ou illisible : elle est gardée.
    /// </summary>
    internal static async Task RemoveBackupAsync(string path, string backup, byte[] written, byte[] original)
    {
        try
        {
            // Le fichier est déjà en place : la suppression de la copie ne dépend plus de l'annulation.
            var current = await ReadSharedAsync(path, CancellationToken.None).ConfigureAwait(false);
            bool installed = current.AsSpan().SequenceEqual(written);
            CryptographicOperations.ZeroMemory(current);
            if (!installed)
            {
                Diagnostics.DebugLog.Write("keepass", $"{path} relu différent après l'enregistrement : copie de sécurité {backup} gardée");
                return;
            }

            var replaced = await ReadSharedAsync(backup, CancellationToken.None).ConfigureAwait(false);
            bool expected = replaced.AsSpan().SequenceEqual(original);
            CryptographicOperations.ZeroMemory(replaced);
            if (expected)
            {
                File.Delete(backup);
            }
            else
            {
                Diagnostics.DebugLog.Write("keepass",
                    $"{path} modifié par un autre poste juste avant l'enregistrement : sa version est gardée dans {backup}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Enregistrement réussi ; la copie sera remplacée puis supprimée au prochain.
            Diagnostics.DebugLog.Write("keepass", $"Copie de sécurité {backup} non supprimée : {e.Message}");
        }
    }

    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellation).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
