using System.Security.Cryptography;
using Tmds.DBus.Protocol;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services.Secrets;

/// <summary>
/// Client minimal de l'API D-Bus Secret Service (org.freedesktop.secrets : GNOME Keyring, KWallet récent, KeePassXC…), sur
/// le bus de session de l'utilisateur : session chiffrée (<see cref="SecretTransport"/>, sinon « plain »), recherche,
/// déverrouillage avec la fenêtre du trousseau (Prompt), lecture et création d'éléments. Chaque appel a un délai maximal ;
/// tout est asynchrone, rien ne bloque le fil de l'interface.
/// </summary>
/// <remarks>
/// Les erreurs sortent telles quelles (<see cref="DBusErrorReplyException"/>, <see cref="TimeoutException"/>…) :
/// <see cref="KeyringProtector"/> les traduit en <see cref="SecretProtectorUnavailableException"/>. Un client sert à une
/// seule opération (lecture ou création de clé), puis est fermé : le trousseau ferme alors la session.
/// </remarks>
internal sealed class SecretServiceClient : IAsyncDisposable
{
    public const string ServiceName = "org.freedesktop.secrets";

    /// <summary>Chemin « aucun objet » de l'API (pas de fenêtre à montrer, alias inconnu).</summary>
    public const string NoObject = "/";

    public const string CollectionInterface = "org.freedesktop.Secret.Collection";
    public const string ItemInterface = "org.freedesktop.Secret.Item";

    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string SessionInterface = "org.freedesktop.Secret.Session";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string NotSupportedError = "org.freedesktop.DBus.Error.NotSupported";

    private readonly DBusConnection _connection;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _promptTimeout;
    private SecretTransport? _transport;
    private string? _session;
    private string? _owner;

    private SecretServiceClient(DBusConnection connection, TimeSpan callTimeout, TimeSpan promptTimeout)
    {
        _connection = connection;
        _callTimeout = callTimeout;
        _promptTimeout = promptTimeout;
    }

    /// <summary>Secrets chiffrés pendant le transport (sinon « plain » : trousseau sans l'algorithme Diffie-Hellman).</summary>
    public bool IsEncrypted => _transport is not null;

    /// <summary>Adresse du bus de session (DBUS_SESSION_BUS_ADDRESS), ou null s'il n'y en a pas.</summary>
    public static string? SessionBusAddress => DBusAddress.Session is { Length: > 0 } address ? address : null;

    /// <summary>Se connecte au bus de session.</summary>
    /// <exception cref="SecretProtectorUnavailableException">Pas de bus de session.</exception>
    public static async Task<SecretServiceClient> ConnectAsync(TimeSpan callTimeout, TimeSpan promptTimeout, CancellationToken cancellation)
    {
        var address = SessionBusAddress ?? throw KeyringProtector.Unavailable(SecretProtectorFailure.NoKeyring, null);
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(callTimeout, cancellation).ConfigureAwait(false);
            return new SecretServiceClient(connection, callTimeout, promptTimeout);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Le service Secret Service tourne sur le bus, ou peut y être démarré à la demande.</summary>
    public async Task<bool> IsServiceRegisteredAsync(CancellationToken cancellation)
    {
        var running = await _connection.ListServicesAsync().WaitAsync(_callTimeout, cancellation).ConfigureAwait(false);
        if (running.Contains(ServiceName, StringComparer.Ordinal))
        {
            return true;
        }

        var activatable = await _connection.ListActivatableServicesAsync().WaitAsync(_callTimeout, cancellation).ConfigureAwait(false);
        return activatable.Contains(ServiceName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Ouvre une session avec le trousseau (le démarre au besoin) : chiffrée si possible, « plain » sinon. Retient aussi le
    /// nom unique du service, seul accepté comme émetteur des signaux des fenêtres de déverrouillage.
    /// </summary>
    public async Task OpenSessionAsync(CancellationToken cancellation)
    {
        var transport = SecretTransport.Create();
        try
        {
            var (output, session) = await CallAsync(
                CreateOpenSession(SecretTransport.Algorithm, VariantValue.Array(transport.PublicKey)),
                static (m, _) => ReadVariantAndPath(m), cancellation).ConfigureAwait(false);
            _session = session;
            transport.Complete(output.GetArray<byte>());
            _transport = transport;
        }
        catch (DBusErrorReplyException e) when (e.ErrorName == NotSupportedError)
        {
            transport.Dispose();
            DebugLog.Write("secrets", "Trousseau : chiffrement Diffie-Hellman non pris en charge, session « plain » (bus de session local).");
            var (_, session) = await CallAsync(CreateOpenSession("plain", VariantValue.String("")),
                static (m, _) => ReadVariantAndPath(m), cancellation).ConfigureAwait(false);
            _session = session;
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        _owner = await CallAsync(CreateCall(BusName, BusPath, BusName, "GetNameOwner", "s", (ref MessageWriter w) => w.WriteString(ServiceName)),
            static (m, _) => m.GetBodyReader().ReadString(), cancellation).ConfigureAwait(false);
    }

    /// <summary>Éléments de tous les trousseaux qui ont exactement ces attributs.</summary>
    /// <exception cref="SecretProtectorUnavailableException">Pas de réponse avant le délai d'une fenêtre de déverrouillage.</exception>
    public async Task<(string[] Unlocked, string[] Locked)> SearchItemsAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken cancellation)
    {
        try
        {
            // Un trousseau peut montrer sa fenêtre de déverrouillage pendant la recherche et ne répondre qu'après (KeePassXC,
            // « déverrouiller la base avant de chercher », son réglage par défaut) : même délai que pour cette fenêtre.
            return await CallAsync(
                CreateCall(ServiceName, ServicePath, ServiceInterface, "SearchItems", "a{ss}", (ref MessageWriter w) => WriteAttributes(ref w, attributes)),
                static (m, _) =>
                {
                    var reader = m.GetBodyReader();
                    return (ToStrings(reader.ReadArrayOfObjectPath()), ToStrings(reader.ReadArrayOfObjectPath()));
                }, cancellation, _promptTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            DebugLog.Write("secrets", "Trousseau : pas de réponse à la recherche (déverrouillage attendu), abandon.");
            throw KeyringProtector.Unavailable(SecretProtectorFailure.UnlockRefused, e);
        }
    }

    /// <summary>
    /// Déverrouille ces objets (trousseaux ou éléments), en montrant au besoin la fenêtre du trousseau et en attendant sa
    /// réponse ; rend ceux qui sont déverrouillés.
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">Déverrouillage refusé ou laissé sans réponse jusqu'au délai.</exception>
    public async Task<string[]> UnlockAsync(IReadOnlyList<string> objects, CancellationToken cancellation)
    {
        var (unlocked, prompt) = await CallAsync(
            CreateCall(ServiceName, ServicePath, ServiceInterface, "Unlock", "ao", (ref MessageWriter w) => w.WriteArray(objects.Select(o => new ObjectPath(o)))),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (ToStrings(reader.ReadArrayOfObjectPath()), reader.ReadObjectPathAsString());
            }, cancellation).ConfigureAwait(false);
        if (prompt == NoObject)
        {
            return unlocked;
        }

        var result = await PromptAsync(prompt, cancellation).ConfigureAwait(false);
        return [.. unlocked, .. result.Type == VariantValueType.Array ? ToStrings(result.GetArray<ObjectPath>()) : []];
    }

    /// <summary>Valeur du secret d'un élément déverrouillé, dans un tableau épinglé que l'appelant efface.</summary>
    public async Task<byte[]> GetSecretAsync(string item, CancellationToken cancellation)
    {
        var session = _session ?? throw new InvalidOperationException("No Secret Service session.");
        var (parameters, value) = await CallAsync(
            CreateCall(ServiceName, item, ItemInterface, "GetSecret", "o", (ref MessageWriter w) => w.WriteObjectPath(session)),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPath();
                return (reader.ReadArrayOfByte(), reader.ReadArrayOfByte());
            }, cancellation).ConfigureAwait(false);
        try
        {
            if (_transport is { } transport)
            {
                return transport.Decrypt(parameters, value);
            }

            var plain = GC.AllocateArray<byte>(value.Length, pinned: true);
            value.CopyTo(plain, 0);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    /// <summary>Trousseau désigné par cet alias (« default »), ou <see cref="NoObject"/>.</summary>
    public Task<string> ReadAliasAsync(string alias, CancellationToken cancellation) =>
        CallAsync(CreateCall(ServiceName, ServicePath, ServiceInterface, "ReadAlias", "s", (ref MessageWriter w) => w.WriteString(alias)),
            static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), cancellation);

    /// <summary>Crée un trousseau sous cet alias (le trousseau demande alors son mot de passe) ; rend son chemin.</summary>
    public async Task<string> CreateCollectionAsync(string label, string alias, CancellationToken cancellation)
    {
        var (collection, prompt) = await CallAsync(
            CreateCall(ServiceName, ServicePath, ServiceInterface, "CreateCollection", "a{sv}s", (ref MessageWriter w) =>
            {
                var properties = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString(CollectionInterface + ".Label");
                w.WriteVariantString(label);
                w.WriteDictionaryEnd(properties);
                w.WriteString(alias);
            }),
            static (m, _) => ReadPathAndPrompt(m), cancellation).ConfigureAwait(false);
        if (prompt != NoObject)
        {
            var result = await PromptAsync(prompt, cancellation).ConfigureAwait(false);
            collection = result.Type == VariantValueType.ObjectPath ? result.GetObjectPathAsString() : NoObject;
        }

        return collection;
    }

    /// <summary>Crée un élément (sans remplacer un élément existant) ; rend son chemin.</summary>
    public async Task<string> CreateItemAsync(string collection, string label, IReadOnlyDictionary<string, string> attributes,
        byte[] secret, CancellationToken cancellation)
    {
        var session = _session ?? throw new InvalidOperationException("No Secret Service session.");
        var (parameters, value) = _transport is { } transport ? transport.Encrypt(secret) : ([], secret);
        var (item, prompt) = await CallAsync(
            CreateCall(ServiceName, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b", (ref MessageWriter w) =>
            {
                var properties = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString(ItemInterface + ".Label");
                w.WriteVariantString(label);
                w.WriteDictionaryEntryStart();
                w.WriteString(ItemInterface + ".Attributes");
                w.WriteSignature("a{ss}");
                WriteAttributes(ref w, attributes);
                w.WriteDictionaryEnd(properties);
                w.WriteStructureStart();
                w.WriteObjectPath(session);
                w.WriteArray(parameters);
                w.WriteArray(value);
                w.WriteString("text/plain");
                w.WriteBool(false);
            }),
            static (m, _) => ReadPathAndPrompt(m), cancellation).ConfigureAwait(false);
        if (prompt != NoObject)
        {
            var result = await PromptAsync(prompt, cancellation).ConfigureAwait(false);
            item = result.Type == VariantValueType.ObjectPath ? result.GetObjectPathAsString() : NoObject;
        }

        return item;
    }

    /// <summary>Trousseau ou élément verrouillé.</summary>
    public async Task<bool> IsLockedAsync(string path, string @interface, CancellationToken cancellation)
    {
        var value = await GetPropertyAsync(path, @interface, "Locked", cancellation).ConfigureAwait(false);
        return value.Type != VariantValueType.Bool || value.GetBool();
    }

    /// <summary>Date de création d'un élément (secondes depuis 1970), 0 si le trousseau ne la donne pas.</summary>
    public async Task<ulong> GetCreatedAsync(string item, CancellationToken cancellation)
    {
        try
        {
            var value = await GetPropertyAsync(item, ItemInterface, "Created", cancellation).ConfigureAwait(false);
            return value.Type == VariantValueType.UInt64 ? value.GetUInt64() : 0;
        }
        catch (DBusErrorReplyException)
        {
            return 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            try
            {
                // Le trousseau ferme aussi la session quand la connexion se ferme : ceci n'est que la politesse.
                await CallAsync(CreateCall(ServiceName, session, SessionInterface, "Close", "", null), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DBusExceptionBase or TimeoutException)
            {
                // Connexion fermée juste après.
            }
        }

        _transport?.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Montre la fenêtre du trousseau et attend son signal Completed (au plus <see cref="_promptTimeout"/> : au-delà, la
    /// fenêtre est retirée) ; rend le résultat de l'opération.
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">Fenêtre refusée ou laissée sans réponse.</exception>
    private async Task<VariantValue> PromptAsync(string prompt, CancellationToken cancellation)
    {
        var completed = new TaskCompletionSource<(bool Dismissed, VariantValue Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Abonné avant d'afficher la fenêtre : le signal ne peut pas arriver avant. Seul le service lui-même est écouté.
        using var watch = await _connection.WatchSignalAsync(_owner ?? ServiceName, prompt, PromptInterface, "Completed",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadBool(), reader.ReadVariantValue());
            },
            (Notification<(bool, VariantValue)> notification) =>
            {
                if (notification.HasValue)
                {
                    completed.TrySetResult(notification.Value);
                }
                else if (notification.Exception is { } e)
                {
                    completed.TrySetException(e);
                }
            },
            flags: ObserverFlags.None, emitOnCapturedContext: false, state: null).AsTask().WaitAsync(_callTimeout, cancellation).ConfigureAwait(false);

        // Pas de fenêtre parente connue : le trousseau place la sienne lui-même.
        await CallAsync(CreateCall(ServiceName, prompt, PromptInterface, "Prompt", "s", (ref MessageWriter w) => w.WriteString("")), cancellation).ConfigureAwait(false);
        try
        {
            var (dismissed, result) = await completed.Task.WaitAsync(_promptTimeout, cancellation).ConfigureAwait(false);
            if (dismissed)
            {
                DebugLog.Write("secrets", "Trousseau : déverrouillage refusé.");
                throw KeyringProtector.Unavailable(SecretProtectorFailure.UnlockRefused, null);
            }

            return result;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // Délai dépassé ou opération abandonnée : la fenêtre du trousseau ne doit pas rester ouverte pour rien.
            DebugLog.Write("secrets", "Trousseau : pas de réponse à la fenêtre de déverrouillage, fenêtre retirée.");
            try
            {
                await CallAsync(CreateCall(ServiceName, prompt, PromptInterface, "Dismiss", "", null), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception dismiss) when (dismiss is DBusExceptionBase or TimeoutException)
            {
                // Fenêtre déjà fermée, ou trousseau qui ne répond plus.
                DebugLog.Write("secrets", $"Trousseau : fenêtre de déverrouillage non retirée ({dismiss.GetType().Name}).");
            }

            if (e is OperationCanceledException)
            {
                throw;
            }

            throw KeyringProtector.Unavailable(SecretProtectorFailure.UnlockRefused, e);
        }
    }

    private Task<VariantValue> GetPropertyAsync(string path, string @interface, string name, CancellationToken cancellation) =>
        CallAsync(CreateCall(ServiceName, path, PropertiesInterface, "Get", "ss", (ref MessageWriter w) =>
            {
                w.WriteString(@interface);
                w.WriteString(name);
            }),
            static (m, _) => m.GetBodyReader().ReadVariantValue(), cancellation);

    private Task<T> CallAsync<T>(MessageBuffer message, MessageValueReader<T> reader, CancellationToken cancellation, TimeSpan? timeout = null) =>
        _connection.CallMethodAsync(message, reader, null).WaitAsync(timeout ?? _callTimeout, cancellation);

    private Task CallAsync(MessageBuffer message, CancellationToken cancellation) =>
        _connection.CallMethodAsync(message).WaitAsync(_callTimeout, cancellation);

    private delegate void BodyWriter(ref MessageWriter writer);

    private MessageBuffer CreateCall(string destination, string path, string @interface, string member, string signature, BodyWriter? body)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature.Length > 0 ? signature : null, MessageFlags.None);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private MessageBuffer CreateOpenSession(string algorithm, VariantValue input) =>
        CreateCall(ServiceName, ServicePath, ServiceInterface, "OpenSession", "sv", (ref MessageWriter w) =>
        {
            w.WriteString(algorithm);
            w.WriteVariant(input);
        });

    private static void WriteAttributes(ref MessageWriter writer, IReadOnlyDictionary<string, string> attributes)
    {
        var start = writer.WriteDictionaryStart();
        foreach (var (key, value) in attributes)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteString(value);
        }

        writer.WriteDictionaryEnd(start);
    }

    private static (VariantValue, string) ReadVariantAndPath(Message message)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadVariantValue(), reader.ReadObjectPathAsString());
    }

    private static (string, string) ReadPathAndPrompt(Message message)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
    }

    private static string[] ToStrings(ObjectPath[] paths) => [.. paths.Select(p => p.ToString())];
}
