using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core;

/// <summary>Liste des comptes lue dans sa copie (<see cref="AccountListCopy"/>), avec l'heure de son chargement depuis le PVWA.</summary>
public sealed record SavedAccountList(DateTime SavedUtc, List<PvwaAccount> Accounts);

/// <summary>
/// Copie de la dernière liste complète des comptes d'un utilisateur sur un PVWA : affichée aussitôt à la connexion
/// suivante, pendant que la liste est relue au PVWA. Ni mot de passe ni secret (la liste des comptes n'en contient pas),
/// mais un inventaire des serveurs et des comptes : chiffrée (<see cref="ISecretProtector"/>, DPAPI du compte Windows),
/// un fichier par PVWA et par utilisateur, nommé d'après une empreinte qui ne dit ni l'un ni l'autre.
/// </summary>
/// <remarks>
/// Toute copie illisible (abîmée, d'un autre compte Windows, d'un autre format), d'un autre PVWA ou utilisateur, ou plus
/// ancienne que <see cref="MaxAge"/> est supprimée et ignorée : la liste se charge alors comme sans copie.
/// </remarks>
public sealed class AccountListCopy(string directory, ISecretProtector protector)
{
    /// <summary>Copie plus ancienne : supprimée, la liste se charge comme la première fois.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Copie plus grande (fichier chiffré) : ignorée.</summary>
    internal const long MaxFileSize = 256L * 1024 * 1024;

    /// <summary>« ZTAL » : liste de comptes ZillaTerm, suivie du numéro de format.</summary>
    private static ReadOnlySpan<byte> Magic => "ZTAL"u8;

    private const byte Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Dossier des copies, local au poste (pas dans le profil itinérant).</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZillaTerm", "Accounts");

    /// <summary>Fichier de la copie : empreinte du PVWA et de l'utilisateur (sans la casse).</summary>
    public string PathFor(string pvwaHost, string user)
    {
        var key = Encoding.UTF8.GetBytes(pvwaHost.Trim().ToLowerInvariant() + "\n" + user.Trim().ToLowerInvariant());
        return Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(key))[..32] + ".accounts");
    }

    /// <summary>
    /// Copie de la liste de <paramref name="user"/> sur <paramref name="pvwaHost"/>, ou null (aucune, ou inutilisable :
    /// alors supprimée).
    /// </summary>
    public SavedAccountList? Read(string pvwaHost, string user, DateTime nowUtc)
    {
        var path = PathFor(pvwaHost, user);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxFileSize)
            {
                return Discard(path, "trop grande");
            }

            var data = File.ReadAllBytes(path);
            if (data.Length <= Magic.Length + 1 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic) || data[Magic.Length] != Version)
            {
                return Discard(path, "format inconnu");
            }

            var json = Decompress(protector.Unprotect(data[(Magic.Length + 1)..]));
            var content = JsonSerializer.Deserialize<Content>(json, JsonOptions);
            if (content?.Accounts is not { Count: > 0 } accounts
                || !string.Equals(content.Host, pvwaHost.Trim(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(content.User, user.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Discard(path, "contenu inattendu");
            }

            var saved = DateTime.SpecifyKind(content.SavedUtc, DateTimeKind.Utc);
            if (nowUtc - saved > MaxAge || saved - nowUtc > TimeSpan.FromDays(1))
            {
                return Discard(path, "trop ancienne");
            }

            return new SavedAccountList(saved, accounts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
                                      or JsonException or InvalidDataException or NotSupportedException)
        {
            DebugLog.Write("comptes", "Copie de la liste des comptes illisible", e);
            return Discard(path, "illisible");
        }
    }

    /// <summary>
    /// Remplace la copie par <paramref name="accounts"/> (liste complète, lue au PVWA à <paramref name="savedUtc"/>). Une
    /// liste vide supprime la copie.
    /// </summary>
    public void Write(string pvwaHost, string user, IReadOnlyList<PvwaAccount> accounts, DateTime savedUtc)
    {
        var path = PathFor(pvwaHost, user);
        if (accounts.Count == 0)
        {
            DeleteFile(path);
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new Content(pvwaHost.Trim(), user.Trim(), savedUtc, accounts.ToList()), JsonOptions);
        var sealedData = protector.Protect(Compress(json));
        var data = new byte[Magic.Length + 1 + sealedData.Length];
        Magic.CopyTo(data);
        data[Magic.Length] = Version;
        sealedData.CopyTo(data, Magic.Length + 1);
        DurableFile.Write(path, data);
    }

    /// <summary>Supprime toutes les copies (option désactivée).</summary>
    public void DeleteAll()
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.accounts").Concat(Directory.EnumerateFiles(directory, "*.accounts.tmp")))
        {
            DeleteFile(file);
        }
    }

    /// <summary>Comptes apparus et disparus d'une liste à l'autre (même identifiant du PVWA).</summary>
    public static (int Added, int Removed) Difference(IEnumerable<PvwaAccount> before, IEnumerable<PvwaAccount> after)
    {
        var old = before.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var now = after.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return (now.Count(id => !old.Contains(id)), old.Count(id => !now.Contains(id)));
    }

    private static SavedAccountList? Discard(string path, string why)
    {
        DebugLog.Write("comptes", $"Copie de la liste des comptes ignorée et supprimée ({why}).");
        DeleteFile(path);
        return null;
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("comptes", "Copie de la liste des comptes impossible à supprimer", e);
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        // Longueur décompressée non connue d'avance : bornée pour qu'une copie aberrante n'épuise pas la mémoire.
        using var input = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxFileSize * 8)
            {
                throw new InvalidDataException("Copie de la liste des comptes trop grande une fois décompressée.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private sealed record Content(string Host, string User, DateTime SavedUtc, List<PvwaAccount> Accounts);
}
