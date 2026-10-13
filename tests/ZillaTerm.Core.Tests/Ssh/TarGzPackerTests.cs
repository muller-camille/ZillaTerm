using System.Formats.Tar;
using System.IO.Compression;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

public sealed class TarGzPackerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cat-targz-" + Guid.NewGuid().ToString("N"));

    public TarGzPackerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Make(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Chaque élément déposé à la racine de l'archive, dossiers parcourus entièrement (fichiers cachés, noms accentués,
    /// chemin long réparti entre préfixe et nom), format ustar lu par tous les tar, droits fixes, propriétaire 0,
    /// contenu intact ; avancement en octets. Seuls les dossiers vides ont une entrée : tar crée les autres sans toucher
    /// à ceux qui existent déjà sur le serveur. Aucun chemin absolu.
    /// </summary>
    [Fact]
    public async Task PacksDroppedItemsAtTheRoot()
    {
        var longName = new string('n', 90) + ".txt";
        Make("deploy/app.conf", "a=1");
        Make("deploy/.env", "secret=dans le fichier, pas dans le journal");
        Make($"deploy/sous dossier/été/{longName}", "long");
        var single = Make("notes.txt", "notes");
        Directory.CreateDirectory(Path.Combine(_dir, "deploy", "vide"));
        var archive = Path.Combine(_dir, "out.tar.gz");
        var reports = new List<TransferProgress>();

        int files = await TarGzPacker.CreateAsync([Path.Combine(_dir, "deploy") + Path.DirectorySeparatorChar, single], archive,
            compress: true, new SyncProgress(reports.Add), default);

        Assert.Equal(4, files);
        Assert.Equal((4, 3L + 43 + 4 + 5), TarGzPacker.Measure([Path.Combine(_dir, "deploy"), single]));
        var formats = new HashSet<(TarEntryFormat, int, int)>();
        var entries = new Dictionary<string, (TarEntryType Type, UnixFileMode Mode, string Content)>();
        await using (var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                var content = entry.DataStream is null ? "" : new StreamReader(entry.DataStream).ReadToEnd();
                entries[entry.Name] = (entry.EntryType, entry.Mode, content);
                formats.Add((entry.Format, entry.Uid, entry.Gid));
            }
        }

        Assert.Equal(TarEntryType.Directory, entries["deploy/vide/"].Type);
        Assert.Equal((UnixFileMode)0b111_101_101, entries["deploy/vide/"].Mode);
        Assert.Equal(["deploy/vide/"], entries.Where(e => e.Value.Type == TarEntryType.Directory).Select(e => e.Key));
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith('/'));
        Assert.Equal(("a=1", (UnixFileMode)0b110_100_100), (entries["deploy/app.conf"].Content, entries["deploy/app.conf"].Mode));
        Assert.Equal("long", entries[$"deploy/sous dossier/été/{longName}"].Content);
        Assert.Contains("deploy/.env", entries.Keys);
        Assert.Equal("notes", entries["notes.txt"].Content);
        Assert.Equal((TarEntryFormat.Ustar, 0, 0), Assert.Single(formats));
        Assert.All(reports, r => Assert.True(r.Packing));
        Assert.Equal(55, reports[^1].Transferred);
    }

    [Fact]
    public async Task CancellationStopsThePacking()
    {
        Make("a/1.txt", "1");
        Make("a/2.txt", "2");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TarGzPacker.CreateAsync([Path.Combine(_dir, "a")], Path.Combine(_dir, "x.tar.gz"), compress: true, null, cts.Token));
    }

    /// <summary>Sans gzip sur le serveur : simple .tar, lisible sans décompression.</summary>
    [Fact]
    public async Task PacksAPlainTarWithoutGzip()
    {
        Make("site/index.html", "<h1>ok</h1>");
        var archive = Path.Combine(_dir, "site.tar");

        Assert.Equal(1, await TarGzPacker.CreateAsync([Path.Combine(_dir, "site")], archive, compress: false, null, default));

        using var reader = new TarReader(File.OpenRead(archive));
        var file = reader.GetNextEntry()!;
        Assert.Equal(("site/index.html", "<h1>ok</h1>"), (file.Name, new StreamReader(file.DataStream!).ReadToEnd()));
        Assert.Null(reader.GetNextEntry());
    }

    /// <summary>
    /// Racine d'un lecteur ou d'un partage (« E:\ ») : refusée, sans rien parcourir ni créer. Sinon son contenu irait
    /// directement dans le dossier du serveur, et l'archive aurait des membres « /fichier ».
    /// </summary>
    [Fact]
    public async Task RefusesTheRootOfADrive()
    {
        var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(_dir)! : "/";
        var archive = Path.Combine(_dir, "root.tar.gz");

        Assert.Equal("deploy", LocalTree.UploadName(Path.Combine(_dir, "deploy") + Path.DirectorySeparatorChar));
        var error = Assert.Throws<ArgumentException>(() => LocalTree.UploadName(root));
        Assert.Contains(root, error.Message, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentException>(() => LocalTree.UploadName(@"E:\"));
            Assert.Throws<ArgumentException>(() => LocalTree.UploadName(@"\\srv01.corp.local\partage\"));
        }

        Assert.Throws<ArgumentException>(() => TarGzPacker.ArchiveName([root], DateTime.Now));
        Assert.Throws<ArgumentException>(() => TarGzPacker.UstarProblem([root]));
        await Assert.ThrowsAsync<ArgumentException>(() => TarGzPacker.CreateAsync([root], archive, compress: true, null, default));
        Assert.False(File.Exists(archive));
    }

    /// <summary>
    /// Ce qui ne tient pas dans le format ustar (lu par tous les tar) est signalé avant l'archivage : nom de fichier de
    /// plus de 100 octets ; un chemin long dont chaque partie tient passe.
    /// </summary>
    [Fact]
    public void FindsWhatDoesNotFitTheStandardFormat()
    {
        Make($"ok/{string.Join("/", Enumerable.Repeat("dossier-de-niveau", 8))}/fichier.txt", "1");
        Assert.Null(TarGzPacker.UstarProblem([Path.Combine(_dir, "ok")]));

        var tooLong = new string('n', 101);
        Make($"app/conf/{tooLong}", "2");
        Assert.Equal($"app/conf/{tooLong}", TarGzPacker.UstarProblem([Path.Combine(_dir, "ok"), Path.Combine(_dir, "app") + Path.DirectorySeparatorChar]));

        Assert.True(TarGzPacker.FitsUstar(new string('a', 100)));
        Assert.False(TarGzPacker.FitsUstar(new string('a', 101)));
        Assert.True(TarGzPacker.FitsUstar(new string('d', 150) + "/" + new string('f', 100)));
        Assert.False(TarGzPacker.FitsUstar(new string('é', 60)));
    }

    /// <summary>gzip cherché aux emplacements des différents Unix ; un emplacement illisible est sauté ; absent : null.</summary>
    [Fact]
    public async Task FindsGzipWhereEachSystemInstallsIt()
    {
        // HP-UX : seulement /usr/contrib/bin/gzip ; /usr/bin illisible.
        var hpux = await TarGzPacker.FindGzipAsync((path, _) => path == "/usr/bin/gzip"
            ? throw new UnauthorizedAccessException()
            : Task.FromResult(path == "/usr/contrib/bin/gzip"), default);
        Assert.Equal("/usr/contrib/bin/gzip", hpux);
        Assert.Equal("/bin/gzip", await TarGzPacker.FindGzipAsync((_, _) => Task.FromResult(true), default));
        Assert.Null(await TarGzPacker.FindGzipAsync((_, _) => Task.FromResult(false), default));
    }

    /// <summary>
    /// Nom de l'archive et commande d'extraction protégée contre l'injection (apostrophes, nom commençant par « - ») ;
    /// « o » : le propriétaire de l'archive n'est jamais appliqué, même par root (lettres-clés sans tiret, lues par tous
    /// les tar).
    /// </summary>
    [Fact]
    public void NamesTheArchiveAndQuotesTheCommand()
    {
        var now = new DateTime(2026, 10, 5, 18, 30, 0);
        Assert.Equal("deploy.tar.gz", TarGzPacker.ArchiveName([Path.Combine(_dir, "deploy") + Path.DirectorySeparatorChar], now));
        Assert.Equal("upload-20261005-183000.tar.gz", TarGzPacker.ArchiveName([Path.Combine(_dir, "a"), Path.Combine(_dir, "b")], now));
        Assert.Equal("upload-20261005-183000.tar", TarGzPacker.ArchiveName([Path.Combine(_dir, "a"), Path.Combine(_dir, "b")], now, compressed: false));
        Assert.Equal("cd '/opt/l'\\''app' && /usr/contrib/bin/gzip -dc './-x.tar.gz' | tar xof - && rm -f './-x.tar.gz'",
            TarGzPacker.ExtractCommand("/opt/l'app", "-x.tar.gz", "/usr/contrib/bin/gzip"));
        // Sans gzip ; « ! » hors des apostrophes, sinon l'historique de csh ou de bash l'interpréterait.
        Assert.Equal("cd '/srv/a'\\!'b' && tar xof './x.tar' && rm -f './x.tar'", TarGzPacker.ExtractCommand("/srv/a!b", "x.tar", null));
        Assert.Null(TarGzPacker.ExtractCommand("/opt/a\nb", "x.tar.gz", "/usr/bin/gzip"));
        // Seuls les emplacements connus de gzip entrent dans la commande.
        Assert.Throws<ArgumentOutOfRangeException>(() => TarGzPacker.ExtractCommand("/opt", "x.tar.gz", "gzip; reboot"));
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
