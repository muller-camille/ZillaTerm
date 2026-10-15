using System.Buffers.Binary;
using System.Text;

namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Fichier Xauthority (~/.Xauthority, ou $XAUTHORITY) : cookies du serveur X de la session, lus pour le transfert X11
/// sous Linux et les autres Unix. Seuls les cookies MIT-MAGIC-COOKIE-1 sont pris en charge.
/// </summary>
/// <remarks>
/// Format de libXau : une suite d'entrées, chacune faite d'une famille (16 bits gros-boutiste), puis de quatre champs
/// « longueur sur 16 bits gros-boutiste + octets » : adresse, numéro d'affichage, nom du protocole, cookie.
/// </remarks>
public static class XAuthority
{
    public const string MagicCookie = "MIT-MAGIC-COOKIE-1";

    /// <summary>Famille « ce poste » : l'adresse est le nom de la machine.</summary>
    private const ushort FamilyLocal = 256;

    /// <summary>Famille « tout poste » : l'entrée vaut pour n'importe quelle adresse.</summary>
    private const ushort FamilyWild = 65535;

    /// <summary>Taille maximale lue : un fichier Xauthority fait quelques centaines d'octets.</summary>
    private const int MaxFileSize = 1 << 20;

    /// <summary>Fichier de la session : $XAUTHORITY, sinon ~/.Xauthority.</summary>
    public static string DefaultPath() =>
        Environment.GetEnvironmentVariable("XAUTHORITY") is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
                ".Xauthority");

    /// <summary>
    /// Cookie de l'affichage <paramref name="displayNumber"/> de ce poste (<paramref name="hostName"/>) dans le fichier
    /// <paramref name="path"/> ; null s'il n'y en a pas (fichier absent, ou serveur X qui n'en demande pas).
    /// </summary>
    public static byte[]? ReadCookie(string path, string hostName, int displayNumber)
    {
        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileSize)
            {
                return null;
            }

            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return FindCookie(data, hostName, displayNumber);
    }

    /// <summary>
    /// Premier cookie MIT-MAGIC-COOKIE-1 qui vaut pour l'affichage <paramref name="displayNumber"/> de ce poste : entrée de
    /// famille « ce poste » au nom <paramref name="hostName"/>, ou « tout poste » ; numéro égal, ou vide (tout affichage).
    /// Comme libXau, la première entrée du fichier qui convient l'emporte. Null si aucune, ou si le fichier est tronqué.
    /// </summary>
    public static byte[]? FindCookie(ReadOnlySpan<byte> file, string hostName, int displayNumber)
    {
        var host = Encoding.UTF8.GetBytes(hostName);
        var number = Encoding.ASCII.GetBytes(displayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var magic = Encoding.ASCII.GetBytes(MagicCookie);
        int offset = 0;
        while (offset < file.Length)
        {
            if (!TryReadUInt16(file, ref offset, out ushort family)
                || !TryReadField(file, ref offset, out var address)
                || !TryReadField(file, ref offset, out var entryNumber)
                || !TryReadField(file, ref offset, out var name)
                || !TryReadField(file, ref offset, out var cookie))
            {
                return null;
            }

            bool forThisHost = family == FamilyWild || (family == FamilyLocal && address.SequenceEqual(host));
            bool forThisDisplay = entryNumber.Length == 0 || entryNumber.SequenceEqual(number);
            if (forThisHost && forThisDisplay && name.SequenceEqual(magic) && cookie.Length > 0)
            {
                return cookie.ToArray();
            }
        }

        return null;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, ref int offset, out ushort value)
    {
        if (data.Length - offset < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        return true;
    }

    private static bool TryReadField(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> field)
    {
        field = default;
        if (!TryReadUInt16(data, ref offset, out ushort length) || data.Length - offset < length)
        {
            return false;
        }

        field = data.Slice(offset, length);
        offset += length;
        return true;
    }
}
