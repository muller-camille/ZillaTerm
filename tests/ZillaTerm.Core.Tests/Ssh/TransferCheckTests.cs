using System.Security.Cryptography;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

public sealed class TransferCheckTests
{
    private static readonly byte[] Data = [.. Enumerable.Range(0, 300_001).Select(i => (byte)(i * 7))];

    /// <summary>Le flux haché en lecture (envoi) et en écriture (téléchargement, relecture) donne la somme du contenu.</summary>
    [Fact]
    public async Task HashingStreamHashesWhatGoesThrough()
    {
        var expected = SHA256.HashData(Data);

        using (var read = new HashingStream(new MemoryStream(Data)))
        {
            var buffer = new byte[4096];
            while (await read.ReadAsync(buffer) > 0)
            {
            }

            Assert.Equal(expected, read.GetHash());
            Assert.Equal(Data.LongLength, read.Count);
        }

        var target = new MemoryStream();
        using (var write = new HashingStream(target))
        {
            await write.WriteAsync(Data.AsMemory(0, 1000));
            write.Write(Data, 1000, Data.Length - 1000);
            Assert.Equal(expected, write.GetHash());
        }

        Assert.Equal(Data, target.ToArray());

        // Sans flux interne : les octets sont seulement hachés (relecture d'un fichier du serveur).
        using var sink = new HashingStream(null);
        sink.Write(Data);
        Assert.Equal(expected, sink.GetHash());
        Assert.Equal(Data.LongLength, sink.Count);
        Assert.False(sink.CanSeek);
        Assert.Throws<NotSupportedException>(() => sink.ReadByte());
    }

    /// <summary>
    /// Téléchargement plafonné d'après la taille annoncée (le double, ou 64 Mio de plus) : un serveur qui envoie sans fin
    /// est arrêté avant d'écrire au-delà, sans IOException (que FluentFTP prendrait pour une coupure à reprendre).
    /// </summary>
    [Fact]
    public void HashingStreamStopsAServerThatSendsMoreThanAnnounced()
    {
        Assert.Equal(64L * 1024 * 1024 + 2048, TransferCheck.DownloadLimit(2048));
        Assert.Equal(64L * 1024 * 1024, TransferCheck.DownloadLimit(-1));
        Assert.Equal(6L * 1024 * 1024 * 1024, TransferCheck.DownloadLimit(3L * 1024 * 1024 * 1024));
        Assert.Equal(long.MaxValue, TransferCheck.DownloadLimit(long.MaxValue - 1));

        var target = new MemoryStream();
        using var write = new HashingStream(target, limit: 10_000, announced: 2048);
        write.Write(Data, 0, 10_000);
        var error = Assert.Throws<InvalidDataException>(() => write.Write(Data, 10_000, 1));
        Assert.IsNotAssignableFrom<IOException>(error);
        Assert.Contains(RemotePath.FormatSize(2048), error.Message, StringComparison.Ordinal);
        Assert.Equal(10_000, target.Length);
        Assert.Equal(10_000, write.Count);
    }

    [Fact]
    public async Task HashesALocalFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-hash-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(path, Data);
            var (hash, length) = await TransferCheck.HashFileAsync(path, default);
            Assert.Equal(SHA256.HashData(Data), hash);
            Assert.Equal(Data.LongLength, length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Identique seulement si la taille et la somme sont les mêmes des deux côtés, et si la relecture a eu lieu.</summary>
    [Fact]
    public void MatchesOnlyWhenSizeAndChecksumAgree()
    {
        var hash = SHA256.HashData(Data);
        var ok = new TransferCheck("f", @"C:\f", "/srv/f", Data.Length, hash, Data.Length, [.. hash]);
        var other = (byte[])hash.Clone();
        other[^1] ^= 1;

        Assert.True(ok.Matches);
        Assert.Equal(64, ok.LocalHash.Length);
        Assert.Equal(ok.LocalHash, ok.RemoteHash);
        Assert.False((ok with { RemoteSha256 = other }).Matches);
        Assert.False((ok with { RemoteLength = Data.Length - 1 }).Matches);
        var unverified = ok with { RemoteLength = -1, RemoteSha256 = [], Error = "Permission denied" };
        Assert.False(unverified.Verified);
        Assert.False(unverified.Matches);
        Assert.Equal("", unverified.RemoteHash);
    }

    /// <summary>
    /// Ligne pour <c>sha256sum -c</c> sur le serveur : somme de l'original (local pour un envoi, serveur pour un
    /// téléchargement) ; chemin avec « \ » ou saut de ligne échappé comme le fait sha256sum.
    /// </summary>
    [Fact]
    public void WritesSha256SumLines()
    {
        var check = new TransferCheck("f", @"C:\f", "/srv/app/f.tar.gz", 1, [0xAB, 0x01], 1, [0xCD, 0x02]);

        Assert.Equal("ab01  /srv/app/f.tar.gz", (check with { Upload = true }).ToSha256SumLine());
        Assert.Equal("cd02  /srv/app/f.tar.gz", check.ToSha256SumLine());
        Assert.Equal("\\ab01  /srv/a\\\\b\\nc", (check with { Upload = true, RemotePath = "/srv/a\\b\nc" }).ToSha256SumLine());
    }

    /// <summary>
    /// Copie en cours d'un téléchargement : à côté de la destination, « nom.xxxxxxxx.part » ; pour un nom de 242 à 255
    /// caractères, un nom court (le suffixe dépasserait la limite de Windows). Jamais créée par-dessus un fichier existant.
    /// </summary>
    [Fact]
    public void PartialCopyStaysInTheFolderWithinTheNameLimit()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"cat-part-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var normal = TransferCheck.PartialPath(Path.Combine(folder, "app.log"));
            Assert.Matches(@"^app\.log\.[0-9a-f]{8}\.part$", Path.GetFileName(normal));
            Assert.Equal(folder, Path.GetDirectoryName(normal));

            foreach (int length in new[] { 241, 242, 255 })
            {
                var name = new string('x', length - 4) + ".log";
                var partial = TransferCheck.PartialPath(Path.Combine(folder, name));
                Assert.Equal(folder, Path.GetDirectoryName(partial));
                Assert.InRange(Path.GetFileName(partial).Length, 1, WindowsFileName.MaxLength);
                Assert.Equal(length <= 241, Path.GetFileName(partial).StartsWith(name, StringComparison.Ordinal));
                // Créée pour de vrai : le nom tient (255 caractères sous Windows, 255 octets ici).
                using (TransferCheck.CreatePartial(partial))
                {
                }

                Assert.Throws<IOException>(() => TransferCheck.CreatePartial(partial).Dispose());
            }

            Assert.Matches(@"^zillaterm\.[0-9a-f]{8}\.part$", Path.GetFileName(TransferCheck.PartialPath(Path.Combine(folder, new string('x', 250)))));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
