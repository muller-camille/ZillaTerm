using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZillaTerm.Core.Sharing;

/// <summary>
/// Lien d'un échange de partage : des messages entiers, dans l'ordre, dans les deux sens. Par connexion directe
/// (<see cref="TcpShareLink"/>) ou par fichiers dans un dossier relais (<see cref="FolderShareLink"/>) ; ce qui y passe
/// après l'ouverture est chiffré (<see cref="ShareCipher"/>).
/// </summary>
public interface IShareLink : IAsyncDisposable
{
    /// <summary>Par où passe le lien, pour l'afficher (« 10.1.2.3 », « dossier relais »).</summary>
    string Route { get; }

    /// <summary>Le lien passe par le dossier relais.</summary>
    bool IsRelay { get; }

    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>Message suivant ; null si l'autre côté a fermé le lien.</summary>
    /// <exception cref="InvalidDataException">Message plus long que <paramref name="maxLength"/>.</exception>
    Task<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken);
}

/// <summary>Messages précédés de leur longueur (4 octets), sur une connexion TCP.</summary>
public sealed class TcpShareLink : IShareLink
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TcpShareLink(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        Route = client.Client.RemoteEndPoint is IPEndPoint remote
            ? (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString()
            : "?";
    }

    public string Route { get; }

    public bool IsRelay => false;

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, message.Length);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        if (await _stream.ReadAtLeastAsync(length, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < 4)
        {
            return null;
        }

        int size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size < 0 || size > maxLength)
        {
            throw new InvalidDataException("Share message too long.");
        }

        var data = new byte[size];
        return await _stream.ReadAtLeastAsync(data, size, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < size
            ? null
            : data;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Lien par un dossier relais (partage réseau de l'équipe), quand le poste qui partage ne peut pas être joint
/// directement : chaque sens est un fichier où les messages (chiffrés après l'ouverture) s'ajoutent, précédés de leur
/// longueur ; l'autre côté le relit régulièrement depuis là où il s'était arrêté. Rien n'écoute sur les postes.
/// </summary>
/// <remarks>
/// Noms des fichiers : l'identifiant de la connexion tiré au hasard, « .g » (de l'invité vers celui qui partage) et
/// « .h » (dans l'autre sens) ; aucun nom de serveur ni de personne. Chaque lecture rouvre le fichier : le client SMB
/// ne garde pas une vue périmée de sa taille.
/// </remarks>
public sealed class FolderShareLink : IShareLink
{
    /// <summary>Intervalle des relectures.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Taille au plus d'un fichier : au-delà, le lien s'arrête (un écran partagé des heures durant).</summary>
    public const long MaxFileLength = 1L << 30;

    private readonly string _inPath;
    private readonly string _outPath;
    private readonly bool _deleteOnClose;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private FileStream? _out;
    private long _offset;

    // Lu et pas encore rendu : _pending à partir de _start (un message rendu avance _start, sans recopier la suite).
    private byte[] _pending = [];
    private int _start;

    /// <param name="inPath">Fichier lu (messages de l'autre côté).</param>
    /// <param name="outPath">Fichier écrit (nos messages), créé s'il n'existe pas.</param>
    /// <param name="deleteOnClose">Effacer les deux fichiers à la fermeture (l'invité ; celui qui partage efface le dossier).</param>
    public FolderShareLink(string inPath, string outPath, bool deleteOnClose)
    {
        _inPath = inPath;
        _outPath = outPath;
        _deleteOnClose = deleteOnClose;
    }

    public string Route => "relay";

    public bool IsRelay => true;

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _out ??= new FileStream(_outPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.WriteThrough);
            if (_out.Length + message.Length + 4 > MaxFileLength)
            {
                throw new IOException("Relay file too large.");
            }

            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, message.Length);
            await _out.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await _out.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _out.FlushAsync(cancellationToken).ConfigureAwait(false);
            _out.Flush(flushToDisk: true);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TakeMessage(maxLength) is { } message)
            {
                return message;
            }

            if (!Read())
            {
                return null;
            }

            if (TakeMessage(maxLength) is { } read)
            {
                return read;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Message entier en tête de ce qui a été lu, retiré ; null s'il n'est pas encore complet.</summary>
    private byte[]? TakeMessage(int maxLength)
    {
        int available = _pending.Length - _start;
        if (available < 4)
        {
            return null;
        }

        int size = BinaryPrimitives.ReadInt32BigEndian(_pending.AsSpan(_start));
        if (size < 0 || size > maxLength)
        {
            throw new InvalidDataException("Share message too long.");
        }

        if (available < 4 + size)
        {
            return null;
        }

        var message = _pending.AsSpan(_start + 4, size).ToArray();
        _start += 4 + size;
        return message;
    }

    /// <summary>Lit ce qui s'est ajouté au fichier ; faux s'il n'existe plus (l'autre côté a fermé).</summary>
    private bool Read()
    {
        try
        {
            using var file = new FileStream(_inPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length <= _offset)
            {
                return true;
            }

            file.Seek(_offset, SeekOrigin.Begin);
            // Reste non rendu, puis ce qui s'est ajouté au fichier : une seule copie par lecture.
            int rest = _pending.Length - _start;
            var buffer = new byte[rest + (int)Math.Min(file.Length - _offset, 4 << 20)];
            _pending.AsSpan(_start).CopyTo(buffer);
            int read = file.ReadAtLeast(buffer.AsSpan(rest), buffer.Length - rest, throwOnEndOfStream: false);
            _offset += read;
            _pending = rest + read == buffer.Length ? buffer : buffer[..(rest + read)];
            _start = 0;
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_out is not null)
            {
                await _out.DisposeAsync().ConfigureAwait(false);
                _out = null;
            }
        }
        finally
        {
            _sendLock.Release();
        }

        if (_deleteOnClose)
        {
            foreach (var path in new[] { _outPath, _inPath })
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Partage indisponible : le fichier, chiffré, sera effacé avec le dossier du partage.
                }
            }
        }
    }
}

/// <summary>Adresses du poste et dossier relais : comment un autre ZillaTerm peut joindre celui qui partage.</summary>
public static class ShareNetwork
{
    /// <summary>Préfixe du dossier d'un partage dans le dossier relais.</summary>
    public const string RelayFolderPrefix = "zts-";

    /// <summary>
    /// Adresses du poste joignables par un collègue : cartes réseau actives, VPN compris (IPv4, puis IPv6 globale) ; ni
    /// boucle locale, ni adresse automatique (169.254), ni lien local. Seulement celles du réseau de l'entreprise quand on
    /// le reconnaît (<see cref="Select"/>) : l'invitation ne donne pas l'adresse du Wi-Fi de la maison.
    /// </summary>
    public static List<IPAddress> LocalAddresses()
    {
        var candidates = new List<(IPAddress, string)>();
        string domain = "";
        try
        {
            domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    candidates.Add((unicast.Address, properties.DnsSuffix));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Liste des cartes indisponible : seul le dossier relais restera.
        }

        return Select(candidates, domain);
    }

    /// <summary>
    /// Adresses à mettre dans l'invitation, parmi celles des cartes (avec le suffixe DNS de chacune). Poste dans un domaine
    /// et cartes dont le suffixe DNS est celui du domaine (réseau de l'entreprise, VPN compris) : seulement leurs adresses.
    /// Sinon (pas de domaine, suffixes non renseignés), toutes.
    /// </summary>
    public static List<IPAddress> Select(IEnumerable<(IPAddress Address, string DnsSuffix)> candidates, string domain)
    {
        var usable = candidates.Where(c => Usable(c.Address)).ToList();
        var company = usable.Where(c => SameDomain(c.DnsSuffix, domain)).ToList();
        var chosen = (company.Count > 0 ? company : usable).Select(c => c.Address).ToList();
        return [.. chosen.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct(),
            .. chosen.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Distinct()];
    }

    private static bool Usable(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && (address.AddressFamily == AddressFamily.InterNetwork
            ? !(address.GetAddressBytes() is [169, 254, ..])
            : address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast
              && !address.IsIPv6SiteLocal);

    /// <summary>Suffixe DNS d'une carte dans le domaine du poste (« corp.local », « paris.corp.local »).</summary>
    private static bool SameDomain(string suffix, string domain)
    {
        suffix = suffix.Trim().TrimEnd('.');
        domain = domain.Trim().TrimEnd('.');
        return suffix.Length > 0 && domain.Length > 0
               && (suffix.Equals(domain, StringComparison.OrdinalIgnoreCase)
                   || suffix.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
                   || (suffix.Contains('.') && domain.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Dossier d'un partage dans le dossier relais.</summary>
    public static string ShareFolder(string relayFolder, ShareInvitation invitation) =>
        Path.Combine(relayFolder, RelayFolderPrefix + invitation.IdText);
}
