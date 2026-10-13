using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Vérification d'un fichier transféré : taille et somme SHA-256 du fichier sur ce poste et sur le serveur. Envoi :
/// le fichier local est haché pendant l'envoi, puis le fichier du serveur est relu par SFTP et haché. Téléchargement :
/// les données reçues du serveur sont hachées, puis le fichier écrit sur le disque est relu et haché.
/// </summary>
/// <param name="Error">Relecture impossible (droits, fichier verrouillé...) : le transfert a eu lieu mais n'est pas vérifié.</param>
public sealed record TransferCheck(
    string Name, string LocalPath, string RemotePath, long LocalLength, byte[] LocalSha256, long RemoteLength, byte[] RemoteSha256,
    string? Error = null)
{
    /// <summary>Envoi vers le serveur (sinon téléchargement).</summary>
    public bool Upload { get; init; }

    /// <summary>Transfert annulé pendant ce fichier : la copie incomplète a été supprimée (voir <see cref="Error"/>).</summary>
    public bool Interrupted { get; init; }

    /// <summary>Transfert en échec pendant ce fichier (message dans <see cref="Error"/>).</summary>
    public bool Failed { get; init; }

    /// <summary>
    /// Lien symbolique ou jonction vers un dossier, trouvé dans un dossier envoyé : pas suivi, rien n'a été envoyé
    /// (<see cref="Error"/> : la cible du lien).
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>Fichier transféré mais pas relu (droits, vérification annulée…) : ni en échec, ni interrompu, ni ignoré.</summary>
    public bool Unverified => !Verified && !Failed && !Interrupted && !Skipped;

    /// <summary>Protocole de l'envoi de ce fichier (null pour un téléchargement).</summary>
    public TransferProtocol? Protocol { get; init; }

    /// <summary>Protocole refusé par le serveur pour ce fichier avant que <see cref="Protocol"/> prenne le relais.</summary>
    public TransferProtocol? Refused { get; init; }

    /// <summary>Ce que le serveur a répondu en refusant <see cref="Refused"/> (message, code de sortie…).</summary>
    public string? RefusedReason { get; init; }

    public bool Verified => Error is null;

    public bool Matches => Verified && LocalLength == RemoteLength && LocalSha256.AsSpan().SequenceEqual(RemoteSha256);

    public string LocalHash => Convert.ToHexStringLower(LocalSha256);

    public string RemoteHash => Convert.ToHexStringLower(RemoteSha256);

    /// <summary>
    /// Ligne au format de <c>sha256sum</c> pour le fichier du serveur, vérifiable avec <c>sha256sum -c</c> : somme de
    /// l'original (le fichier local pour un envoi, celui du serveur pour un téléchargement), deux espaces, chemin. Un
    /// chemin contenant « \ » ou un saut de ligne est échappé comme le fait sha256sum (ligne commençant par « \ »).
    /// </summary>
    public string ToSha256SumLine()
    {
        var hash = Upload ? LocalHash : RemoteHash;
        if (RemotePath.IndexOfAny(['\\', '\n', '\r']) < 0)
        {
            return $"{hash}  {RemotePath}";
        }

        var escaped = RemotePath.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
        return $"\\{hash}  {escaped}";
    }

    /// <summary>
    /// Fichier où arrive un téléchargement, à côté de sa destination (même dossier) : il ne la remplace qu'une fois
    /// complet, si bien qu'un transfert coupé ou annulé laisse intact le fichier local qui existait. « nom.1a2b3c4d.part »,
    /// ou « zillaterm.1a2b3c4d.part » quand le nom est si long (242 à 255 caractères) que le suffixe dépasserait la limite de
    /// Windows. À créer sans jamais remplacer un fichier existant (<see cref="CreatePartial"/>).
    /// </summary>
    internal static string PartialPath(string localPath)
    {
        var suffix = $".{Guid.NewGuid().ToString("N")[..8]}.part";
        return Path.GetFileName(localPath).Length + suffix.Length <= WindowsFileName.MaxLength
            ? localPath + suffix
            : Path.Combine(Path.GetDirectoryName(localPath) ?? "", "zillaterm" + suffix);
    }

    /// <summary>
    /// Lien vers un dossier dans un dossier envoyé ou archivé : noté dans le bilan, pas suivi. <paramref name="remotePath"/> :
    /// où il serait arrivé sur le serveur.
    /// </summary>
    public static TransferCheck SkippedLink(DirectoryInfo link, string remotePath)
    {
        string target;
        try
        {
            target = link.LinkTarget ?? link.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            target = link.FullName;
        }

        return new TransferCheck(link.Name, link.FullName, remotePath, -1, [], -1, [], target) { Upload = true, Skipped = true };
    }

    /// <summary>Marge d'un téléchargement au-delà de la taille annoncée, pour un fichier qui grossit pendant la copie (journal).</summary>
    private const long DownloadMargin = 64L * 1024 * 1024;

    /// <summary>
    /// Au plus ce qu'un téléchargement écrit sur le poste pour un fichier annoncé à <paramref name="announced"/> octets :
    /// le double, ou 64 Mio de plus, au plus grand des deux. Un serveur qui annonce peu puis envoie sans fin ne remplit
    /// pas le disque du poste (voir <see cref="HashingStream"/>).
    /// </summary>
    internal static long DownloadLimit(long announced)
    {
        long size = Math.Max(announced, 0);
        return size > long.MaxValue / 2 ? long.MaxValue : Math.Max(size * 2, size + DownloadMargin);
    }

    /// <summary>Crée le fichier d'un téléchargement en cours ; échoue plutôt que de remplacer un fichier du même nom.</summary>
    internal static FileStream CreatePartial(string partialPath) =>
        new(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

    /// <summary>SHA-256 et taille d'un fichier local.</summary>
    internal static async Task<(byte[] Hash, long Length)> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hashing = new HashingStream(file);
        var buffer = new byte[81920];
        while (await hashing.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
        {
        }

        return (hashing.GetHash(), hashing.Count);
    }
}

/// <summary>
/// Flux qui calcule le SHA-256 des octets qui le traversent, lus depuis le flux interne ou écrits vers lui ; sans flux
/// interne, les octets écrits sont seulement hachés. Lecture et écriture séquentielles seulement.
/// </summary>
/// <param name="limit">
/// Octets au plus (téléchargement, voir <see cref="TransferCheck.DownloadLimit"/>) : au-delà, <see cref="InvalidDataException"/>
/// (pas une <see cref="IOException"/>, que FluentFTP traiterait comme une coupure à reprendre), avant toute écriture.
/// </param>
/// <param name="announced">Taille annoncée par le serveur, pour le message.</param>
internal sealed class HashingStream(Stream? inner, long limit = long.MaxValue, long announced = -1) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Nombre d'octets hachés.</summary>
    public long Count { get; private set; }

    public override bool CanRead => inner?.CanRead ?? false;

    public override bool CanWrite => inner?.CanWrite ?? true;

    public override bool CanSeek => false;

    public override long Length => inner?.Length ?? throw new NotSupportedException();

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    private Stream Inner => inner ?? throw new NotSupportedException();

    public byte[] GetHash() => _hash.GetCurrentHash();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Inner.Read(buffer);
        Append(buffer[..n]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Append(buffer);
        inner?.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Append(buffer.Span);
        if (inner is not null)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner?.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Le flux interne reste à fermer par son propriétaire.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length > limit - Count)
        {
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, CoreStrings.DownloadExceedsAnnounced,
                RemotePath.FormatSize(limit), RemotePath.FormatSize(Math.Max(announced, 0))));
        }

        _hash.AppendData(data);
        Count += data.Length;
    }
}
