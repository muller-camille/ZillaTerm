using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core.Tests;

/// <summary>
/// Copie chiffrée de la dernière liste des comptes : relue telle quelle pour le même PVWA et le même utilisateur ;
/// ignorée et supprimée dès qu'elle est illisible, d'un autre, ou trop ancienne.
/// </summary>
public sealed class AccountListCopyTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zt-accounts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static List<PvwaAccount> Accounts(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new PvwaAccount
        {
            Id = $"12_{i}",
            Name = $"Operating System-WinDomain-srv{i}-admin",
            Address = $"srv{i}.corp.example.com",
            UserName = "admin",
            PlatformId = "WinDomain",
            SafeName = "WIN-SRV",
            PlatformAccountProperties = new() { ["LogonDomain"] = JsonSerializer.SerializeToElement("CORP") },
            RemoteMachinesAccess = new RemoteMachinesAccess { RemoteMachines = "jump01;jump02", AccessRestrictedToRemoteMachines = true },
            SecretManagement = new SecretManagement { AutomaticManagementEnabled = true, Status = "failure", LastModifiedTime = 1_700_000_000 },
            CreatedTime = 1_600_000_000,
        }),
    ];

    [Fact]
    public void ListComesBackWholeForTheSamePvwaAndUser()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        var accounts = Accounts(3);
        copy.Write("pvwa.corp.example.com", "jdoe", accounts, Now.AddHours(-2));

        // Même PVWA et même utilisateur, sans tenir compte de la casse.
        var saved = copy.Read("PVWA.corp.example.com", "JDoe", Now);

        Assert.NotNull(saved);
        Assert.Equal(Now.AddHours(-2), saved.SavedUtc);
        Assert.Equal(DateTimeKind.Utc, saved.SavedUtc.Kind);
        Assert.Equal(accounts.Select(a => a.Id), saved.Accounts.Select(a => a.Id));
        var first = saved.Accounts[0];
        Assert.Equal(("srv0.corp.example.com", "admin", "WinDomain", "WIN-SRV", "CORP"),
            (first.Address, first.UserName, first.PlatformId, first.SafeName, first.LogonDomain));
        Assert.Equal("jump01;jump02", first.RemoteMachines);
        Assert.True(first.RemoteMachinesAccess!.AccessRestrictedToRemoteMachines);
        Assert.True(first.SecretManagement!.Failed);
        Assert.Equal(1_600_000_000, first.CreatedTime);
    }

    [Fact]
    public void FileIsEncryptedAndNamedWithoutPvwaOrUser()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector(xor: true));
        copy.Write("pvwa.corp.example.com", "jdoe", Accounts(2), Now);

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(copy.PathFor("pvwa.corp.example.com", "jdoe"), file);
        Assert.DoesNotContain("pvwa", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jdoe", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
        // Ni le nom d'un serveur ni celui d'un compte en clair dans le fichier.
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
        Assert.DoesNotContain("srv0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", text, StringComparison.Ordinal);
        // Pas de copie temporaire ni de sauvegarde laissée à côté.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void OtherPvwaOrUserHasNoCopy()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa.corp.example.com", "jdoe", Accounts(1), Now);

        Assert.Null(copy.Read("pvwa.corp.example.com", "asmith", Now));
        Assert.Null(copy.Read("pvwa-recette.corp.example.com", "jdoe", Now));
        Assert.NotNull(copy.Read("pvwa.corp.example.com", "jdoe", Now));
    }

    [Fact]
    public void CopyOfAnotherUserPutInPlaceIsDiscarded()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa.corp.example.com", "asmith", Accounts(1), Now);
        // Fichier d'un autre utilisateur mis à la place : le contenu le dit, il n'est pas montré.
        File.Copy(copy.PathFor("pvwa.corp.example.com", "asmith"), copy.PathFor("pvwa.corp.example.com", "jdoe"));

        Assert.Null(copy.Read("pvwa.corp.example.com", "jdoe", Now));
        Assert.False(File.Exists(copy.PathFor("pvwa.corp.example.com", "jdoe")));
    }

    [Fact]
    public void OldCopyIsDiscarded()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa", "jdoe", Accounts(1), Now - AccountListCopy.MaxAge - TimeSpan.FromMinutes(1));

        Assert.Null(copy.Read("pvwa", "jdoe", Now));
        Assert.False(File.Exists(copy.PathFor("pvwa", "jdoe")));

        // Juste avant la limite : gardée.
        copy.Write("pvwa", "jdoe", Accounts(1), Now - AccountListCopy.MaxAge + TimeSpan.FromMinutes(1));
        Assert.NotNull(copy.Read("pvwa", "jdoe", Now));
    }

    [Fact]
    public void CopyFromTheFutureIsDiscarded()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa", "jdoe", Accounts(1), Now.AddDays(2));

        Assert.Null(copy.Read("pvwa", "jdoe", Now));
    }

    [Fact]
    public void DamagedOrForeignCopyIsDiscarded()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        var path = copy.PathFor("pvwa", "jdoe");

        // Octet changé : le déchiffrement échoue (autre compte Windows, fichier abîmé).
        copy.Write("pvwa", "jdoe", Accounts(1), Now);
        var data = File.ReadAllBytes(path);
        data[^1] ^= 1;
        File.WriteAllBytes(path, data);
        Assert.Null(copy.Read("pvwa", "jdoe", Now));
        Assert.False(File.Exists(path));

        // Autre format.
        File.WriteAllBytes(path, "PK\u0003\u0004 pas une liste"u8.ToArray());
        Assert.Null(copy.Read("pvwa", "jdoe", Now));
        Assert.False(File.Exists(path));

        // Fichier vide.
        File.WriteAllBytes(path, []);
        Assert.Null(copy.Read("pvwa", "jdoe", Now));
    }

    [Fact]
    public void ProtectorRefusalMeansNoCopy()
    {
        new AccountListCopy(_dir, new ChecksumProtector()).Write("pvwa", "jdoe", Accounts(1), Now);

        Assert.Null(new AccountListCopy(_dir, new RefusingProtector()).Read("pvwa", "jdoe", Now));
    }

    [Fact]
    public void EmptyListRemovesTheCopy()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa", "jdoe", Accounts(2), Now);
        copy.Write("pvwa", "jdoe", [], Now);

        Assert.False(File.Exists(copy.PathFor("pvwa", "jdoe")));
        Assert.Null(copy.Read("pvwa", "jdoe", Now));
    }

    [Fact]
    public void DeleteAllRemovesEveryCopy()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa", "jdoe", Accounts(1), Now);
        copy.Write("pvwa", "asmith", Accounts(1), Now);
        File.WriteAllText(Path.Combine(_dir, "autre.txt"), "gardé");

        copy.DeleteAll();

        Assert.Equal(["autre.txt"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        // Sans dossier : rien à faire.
        new AccountListCopy(Path.Combine(_dir, "absent"), new ChecksumProtector()).DeleteAll();
    }

    [Fact]
    public void LargeListStaysSmallOnDisk()
    {
        var copy = new AccountListCopy(_dir, new ChecksumProtector());
        copy.Write("pvwa", "jdoe", Accounts(20_000), Now);

        Assert.True(new FileInfo(copy.PathFor("pvwa", "jdoe")).Length < 2_000_000);
        Assert.Equal(20_000, copy.Read("pvwa", "jdoe", Now)!.Accounts.Count);
    }

    [Fact]
    public void DifferenceCountsNewAndRemovedAccounts()
    {
        var before = Accounts(5);
        var after = Accounts(7).Skip(2).ToList();

        Assert.Equal((2, 2), AccountListCopy.Difference(before, after));
        Assert.Equal((0, 0), AccountListCopy.Difference(before, Accounts(5)));
    }

    /// <summary>Protection de test : somme de contrôle (un octet changé est refusé), contenu éventuellement brouillé.</summary>
    private sealed class ChecksumProtector(bool xor = false) : ISecretProtector
    {
        public byte[] Protect(byte[] data) => [.. SHA256.HashData(data), .. Scramble(data)];

        public byte[] Unprotect(byte[] data)
        {
            var plain = Scramble(data[32..]);
            if (!SHA256.HashData(plain).AsSpan().SequenceEqual(data.AsSpan(0, 32)))
            {
                throw new CryptographicException("altered");
            }

            return plain;
        }

        private byte[] Scramble(byte[] data) => xor ? data.Select(b => (byte)(b ^ 0x5A)).ToArray() : data;
    }

    private sealed class RefusingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => throw new CryptographicException("refused");

        public byte[] Unprotect(byte[] data) => throw new CryptographicException("refused");
    }
}
