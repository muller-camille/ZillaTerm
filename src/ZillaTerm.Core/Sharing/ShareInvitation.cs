using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ZillaTerm.Core.Sharing;

/// <summary>
/// Invitation à regarder une session partagée : à copier dans Teams ou un mail, puis à coller dans « Rejoindre une
/// session partagée… » d'un autre ZillaTerm. Elle donne le moyen de joindre le poste qui partage (adresses et port, ou
/// dossier relais sur un partage réseau) et le secret qui authentifie l'échange ; ni le nom du serveur, ni le compte, ni
/// rien de la session : ils ne circulent qu'une fois le canal chiffré établi.
/// </summary>
/// <remarks>
/// Format : « zillaterm-share:1: » suivi du contenu binaire en base64url. Celui qui a l'invitation peut demander à
/// regarder, rien de plus : chaque demande attend l'accord de celui qui partage, qui voit le nom annoncé et un code de
/// vérification à comparer de vive voix.
/// </remarks>
public sealed record ShareInvitation(
    byte[] ShareId,
    byte[] Secret,
    IReadOnlyList<IPEndPoint> Endpoints,
    string? RelayFolder,
    DateTime ExpiresAt)
{
    public const string Prefix = "zillaterm-share:1:";

    public const int IdLength = 16;

    public const int SecretLength = 32;

    /// <summary>Adresses gardées au plus (cartes réseau du poste, VPN compris).</summary>
    public const int MaxEndpoints = 8;

    /// <summary>Longueur au plus du chemin du dossier relais.</summary>
    public const int MaxRelayFolder = 400;

    private const byte Version = 1;

    /// <summary>Nouvelle invitation : identifiant et secret tirés au hasard.</summary>
    public static ShareInvitation Create(IEnumerable<IPEndPoint> endpoints, string? relayFolder, DateTime expiresAt) =>
        new(RandomNumberGenerator.GetBytes(IdLength), RandomNumberGenerator.GetBytes(SecretLength),
            endpoints.Take(MaxEndpoints).ToList(), string.IsNullOrWhiteSpace(relayFolder) ? null : relayFolder.Trim(), expiresAt);

    /// <summary>Identifiant en hexadécimal (nom du dossier relais, journal de débogage).</summary>
    public string IdText => Convert.ToHexString(ShareId).ToLowerInvariant();

    /// <summary>Texte de l'invitation, à copier.</summary>
    public override string ToString()
    {
        var data = new MemoryStream();
        data.WriteByte(Version);
        data.Write(ShareId);
        data.Write(Secret);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, new DateTimeOffset(DateTime.SpecifyKind(ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds());
        data.Write(number);
        data.WriteByte((byte)Endpoints.Count);
        foreach (var endpoint in Endpoints)
        {
            var address = endpoint.Address.GetAddressBytes();
            data.WriteByte((byte)address.Length);
            data.Write(address);
            BinaryPrimitives.WriteUInt16BigEndian(number, (ushort)endpoint.Port);
            data.Write(number[..2]);
        }

        var relay = Encoding.UTF8.GetBytes(RelayFolder ?? "");
        BinaryPrimitives.WriteUInt16BigEndian(number, (ushort)relay.Length);
        data.Write(number[..2]);
        data.Write(relay);
        return Prefix + Base64Url(data.ToArray());
    }

    /// <summary>
    /// Lit une invitation collée (espaces, retours à la ligne et texte autour tolérés : elle est cherchée par son préfixe).
    /// </summary>
    public static bool TryParse(string? text, out ShareInvitation invitation)
    {
        invitation = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var encoded = new StringBuilder();
        foreach (char c in text.AsSpan(start + Prefix.Length))
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                encoded.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                break;
            }
        }

        if (encoded.Length > 4096 || FromBase64Url(encoded.ToString()) is not { } data)
        {
            return false;
        }

        try
        {
            var reader = new SpanReader(data);
            if (reader.Byte() != Version)
            {
                return false;
            }

            var id = reader.Bytes(IdLength).ToArray();
            var secret = reader.Bytes(SecretLength).ToArray();
            long expires = BinaryPrimitives.ReadInt64BigEndian(reader.Bytes(8));
            int count = reader.Byte();
            if (count > MaxEndpoints)
            {
                return false;
            }

            var endpoints = new List<IPEndPoint>(count);
            for (int i = 0; i < count; i++)
            {
                int length = reader.Byte();
                if (length is not (4 or 16))
                {
                    return false;
                }

                var address = new IPAddress(reader.Bytes(length));
                int port = BinaryPrimitives.ReadUInt16BigEndian(reader.Bytes(2));
                if (port == 0)
                {
                    return false;
                }

                endpoints.Add(new IPEndPoint(address, port));
            }

            int relayLength = BinaryPrimitives.ReadUInt16BigEndian(reader.Bytes(2));
            if (relayLength > MaxRelayFolder * 4)
            {
                return false;
            }

            var relay = Encoding.UTF8.GetString(reader.Bytes(relayLength));
            if (!reader.End || (relay.Length > 0 && !IsRelayFolder(relay)) || (endpoints.Count == 0 && relay.Length == 0)
                || expires is < 0 or > 253402300799)
            {
                return false;
            }

            invitation = new ShareInvitation(id, secret, endpoints, relay.Length == 0 ? null : relay,
                DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Dossier relais acceptable : chemin complet (« \\serveur\partage\… » ou « C:\… »), sans caractère de contrôle ni
    /// invisible, ni « .. » (il vient d'une invitation reçue).
    /// </summary>
    public static bool IsRelayFolder(string? path) =>
        path is { Length: > 0 and <= MaxRelayFolder }
        && !path.Split('\\', '/').Any(part => part == "..")
        && path.EnumerateRunes().All(r => !Rune.IsControl(r) && Rune.GetUnicodeCategory(r) != System.Globalization.UnicodeCategory.Format)
        // Mêmes règles qu'un fichier de ce dossier : ni périphérique (« \\.\ »), ni canal nommé, ni nom réservé. Hors de
        // Windows (tests), un chemin absolu.
        && (OperatingSystem.IsWindows()
            ? DutyReminderFile.IsValidPath(path.TrimEnd('\\', '/') + "\\zillaterm")
            : Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Lecture bornée : une longueur qui dépasse lève <see cref="ArgumentOutOfRangeException"/>.</summary>
    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public readonly bool End => _at == _data.Length;

        public byte Byte() => Bytes(1)[0];

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || count > _data.Length - _at)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var slice = _data.Slice(_at, count);
            _at += count;
            return slice;
        }
    }
}
