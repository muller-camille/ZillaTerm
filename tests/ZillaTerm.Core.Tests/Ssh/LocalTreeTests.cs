using System.Formats.Tar;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

/// <summary>Liens symboliques de test (une jonction se comporte de même, mais ne se crée pas depuis .NET).</summary>
internal static class LocalLinks
{
    /// <summary>Crée un lien vers un dossier ; faux si le système le refuse (Windows sans le droit de créer des liens).</summary>
    public static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Dossier de ce poste envoyé ou archivé : les liens vers des dossiers (boucle vers un dossier parent, dossier interdit)
/// ne sont pas suivis ; un lien vers un fichier est envoyé avec le contenu de sa cible.
/// </summary>
public sealed class LocalTreeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cat-tree-" + Guid.NewGuid().ToString("N"));

    public LocalTreeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// deploy/ : app.conf, .env (caché), current.conf (lien vers app.conf), conf/site.conf, et deux liens vers des
    /// dossiers : « loop » vers deploy/ lui-même, « outside » vers un dossier à côté.
    /// </summary>
    private string? MakeTree()
    {
        var deploy = Path.Combine(_dir, "deploy");
        Directory.CreateDirectory(Path.Combine(deploy, "conf"));
        Directory.CreateDirectory(Path.Combine(_dir, "outside"));
        File.WriteAllText(Path.Combine(deploy, "app.conf"), "a=1");
        File.WriteAllText(Path.Combine(deploy, ".env"), "b=2");
        File.WriteAllText(Path.Combine(deploy, "conf", "site.conf"), "c=3");
        File.WriteAllText(Path.Combine(_dir, "outside", "secret.txt"), "pas envoyé");
        if (!LocalLinks.TryLinkDirectory(Path.Combine(deploy, "loop"), deploy)
            || !LocalLinks.TryLinkDirectory(Path.Combine(deploy, "conf", "outside"), Path.Combine(_dir, "outside")))
        {
            return null;
        }

        try
        {
            File.CreateSymbolicLink(Path.Combine(deploy, "current.conf"), Path.Combine(deploy, "app.conf"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return deploy;
    }

    [Fact]
    public void LinksToFoldersAreNotFollowed()
    {
        if (MakeTree() is not { } deploy)
        {
            // Windows sans le droit de créer des liens symboliques.
            return;
        }

        var root = new DirectoryInfo(deploy);
        Assert.Equal([".env", "app.conf", "current.conf"], LocalTree.Files(root).Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["conf"], LocalTree.Directories(root).Select(d => d.Name));
        Assert.Equal(["loop"], LocalTree.Links(root).Select(d => d.Name));
        Assert.Equal(["outside"], LocalTree.Links(new DirectoryInfo(Path.Combine(deploy, "conf"))).Select(d => d.Name));
        Assert.Equal([".env", "app.conf", "current.conf", "site.conf"],
            LocalTree.AllFiles(root).Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(4, RemoteFileBrowser.CountFiles(deploy));
        Assert.True(LocalTree.IsLink(new DirectoryInfo(Path.Combine(deploy, "loop"))));
        Assert.False(LocalTree.IsLink(new DirectoryInfo(Path.Combine(deploy, "conf"))));
    }

    /// <summary>Archive .tar.gz : les liens vers des dossiers sont laissés de côté et signalés, le reste est archivé.</summary>
    [Fact]
    public async Task ArchiveSkipsLinksToFolders()
    {
        if (MakeTree() is not { } deploy)
        {
            return;
        }

        Assert.Equal(4, TarGzPacker.Measure([deploy]).Files);
        Assert.Null(TarGzPacker.UstarProblem([deploy]));
        var skipped = new List<string>();
        var archive = Path.Combine(_dir, "deploy.tar");

        int files = await TarGzPacker.CreateAsync([deploy], archive, compress: false, null, default, (link, name) => skipped.Add(name));

        Assert.Equal(4, files);
        Assert.Equal(["deploy/conf/outside", "deploy/loop"], skipped.Order(StringComparer.Ordinal));
        var names = new List<string>();
        await using (var stream = File.OpenRead(archive))
        using (var reader = new TarReader(stream))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                names.Add(entry.Name);
            }
        }

        // Pas d'entrée pour les dossiers qui ont du contenu : tar les crée sans toucher à ceux qui existent.
        Assert.Equal(["deploy/.env", "deploy/app.conf", "deploy/conf/site.conf", "deploy/current.conf"],
            names.Order(StringComparer.Ordinal));
    }

    /// <summary>Lien non suivi : noté à part, ni vérifié ni « non vérifié ».</summary>
    [Fact]
    public void SkippedLinkIsItsOwnKindOfCheck()
    {
        if (MakeTree() is not { } deploy)
        {
            return;
        }

        var check = TransferCheck.SkippedLink(new DirectoryInfo(Path.Combine(deploy, "loop")), "/srv/deploy/loop");

        Assert.Equal(("loop", "/srv/deploy/loop", deploy), (check.Name, check.RemotePath, check.Error));
        Assert.True(check.Skipped);
        Assert.True(check.Upload);
        Assert.False(check.Verified);
        Assert.False(check.Unverified);
        Assert.False(check.Matches);
    }
}
