using System.IO;
using System.Security.Cryptography;
using ZillaTerm.Core;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Services.KeePass;

/// <summary>
/// Coffres KeePass ouverts (un par dossier de « Mes serveurs »), coffre local des mots de passe maîtres et
/// journal des accès d'urgence. Vit le temps de l'application ; tout est verrouillé à la fermeture de la fenêtre
/// principale et au verrouillage de la session Windows.
/// </summary>
internal sealed class KeePassManager : IDisposable
{
    private readonly Dictionary<string, KeePassVault> _open = new(StringComparer.Ordinal);

    // Augmenté par LockAll (sous le verrou de _open) : un coffre dont l'ouverture a commencé avant n'est pas gardé.
    private long _generation;

    public KeePassManager()
        : this(new DpapiProtector())
    {
    }

    /// <param name="protector">
    /// Protection du fichier du coffre local liée au compte de l'utilisateur : DPAPI sous Windows, trousseau du système
    /// ailleurs (application multiplateforme).
    /// </param>
    public KeePassManager(ISecretProtector protector)
    {
        var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        Protector = protector;
        Store = new LocalSecretStore(Path.Combine(directory, "coffre-local.dat"), protector);
        Log = new EmergencyLog(Path.Combine(directory, "urgence.log"));
    }

    /// <summary>Protection du fichier du coffre local (à préparer avant de le créer ou de le déverrouiller hors de Windows).</summary>
    public ISecretProtector Protector { get; }

    /// <summary>Un coffre a été déverrouillé ou verrouillé.</summary>
    public event Action? Changed;

    public LocalSecretStore Store { get; }

    public EmergencyLog Log { get; }

    /// <summary>Coffre ouvert pour ce dossier ; utilisable depuis n'importe quel thread (connexions SSH).</summary>
    public KeePassVault? Get(string folderId)
    {
        lock (_open)
        {
            return _open.GetValueOrDefault(folderId);
        }
    }

    public bool IsOpen(string folderId) => Get(folderId) is not null;

    /// <summary>Mot de passe maître mémorisé pour ce dossier (UTF-8, à effacer), si le coffre local est déverrouillé.</summary>
    public byte[]? StoredPassword(KeePassFolder folder) =>
        folder.RememberPassword && Store.IsUnlocked ? Store.Get(folder.Id) : null;

    /// <summary>Ouvre le coffre avec ce mot de passe (UTF-8) et ce fichier clé ; l'action est journalisée.</summary>
    /// <exception cref="OperationCanceledException">
    /// Annulé, ou tout a été verrouillé pendant l'ouverture (session Windows verrouillée) : le coffre n'est pas gardé.
    /// </exception>
    public async Task<KeePassVault> UnlockAsync(KeePassFolder folder, byte[]? password, string? keyFilePath, CancellationToken cancellation)
    {
        long generation;
        lock (_open)
        {
            generation = _generation;
        }

        KeePassVault vault;
        try
        {
            var keyFile = KeePassKey.ReadKeyFile(keyFilePath);
            KeePassKey key;
            try
            {
                key = KeePassKey.CreateFromUtf8(folder.UsesPassword ? password ?? [] : null, keyFile);
            }
            finally
            {
                if (keyFile is not null)
                {
                    CryptographicOperations.ZeroMemory(keyFile);
                }
            }

            var opening = KeePassVault.OpenAsync(folder.FilePath, key, cancellation);
            try
            {
                vault = await opening.WaitAsync(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Abandon pendant la dérivation de clé (Argon2 ne s'interrompt pas) : la fenêtre n'attend plus, et le coffre
                // éventuellement ouvert à la fin du calcul est refermé aussitôt.
                _ = opening.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result.Dispose();
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                throw;
            }

            if (cancellation.IsCancellationRequested)
            {
                vault.Dispose();
                cancellation.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is KeePassException or IOException or UnauthorizedAccessException)
        {
            Log.TryWrite("keepass-open-failed", ("vault", folder.FilePath), ("reason", ex is KeePassException k ? k.Kind.ToString() : ex.GetType().Name));
            throw;
        }

        try
        {
            // Pas d'accès d'urgence sans trace : si le journal ne peut pas être écrit, le coffre n'est pas ouvert.
            Log.Write("keepass-open", ("vault", folder.FilePath), ("format", vault.Database.FormatName),
                ("keyfile", string.IsNullOrEmpty(keyFilePath) ? "no" : "yes"),
                ("entries", vault.Database.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch
        {
            vault.Dispose();
            throw;
        }

        Lock(folder.Id, notify: false);
        bool kept;
        lock (_open)
        {
            kept = generation == _generation;
            if (kept)
            {
                _open[folder.Id] = vault;
            }
        }

        if (!kept)
        {
            vault.Dispose();
            throw new OperationCanceledException();
        }

        Changed?.Invoke();
        return vault;
    }

    public void Lock(string folderId) => Lock(folderId, notify: true);

    /// <summary>Verrouille tous les coffres KeePass (le coffre local aussi si <paramref name="localStore"/>).</summary>
    public void LockAll(bool localStore = false)
    {
        List<string> ids;
        lock (_open)
        {
            _generation++;
            ids = [.. _open.Keys];
        }

        foreach (var id in ids)
        {
            Lock(id, notify: false);
        }

        if (localStore)
        {
            Store.Lock();
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        LockAll(localStore: true);
        Store.Dispose();
    }

    private void Lock(string folderId, bool notify)
    {
        KeePassVault? vault;
        lock (_open)
        {
            _open.Remove(folderId, out vault);
        }

        if (vault is not null)
        {
            // D'abord effacer les secrets : un journal inaccessible ne doit jamais laisser un coffre ouvert.
            vault.Dispose();
            Log.TryWrite("keepass-lock", ("vault", vault.FilePath));
            if (notify)
            {
                Changed?.Invoke();
            }
        }
    }
}
