using System.Net;

namespace ZillaTerm.Core.Migration;

/// <summary>Compte du PVWA proposé pour une session importée, et la machine cible s'il s'agit d'un compte de domaine.</summary>
public sealed record ImportCandidate(PvwaAccount Account, string? RemoteMachine)
{
    /// <summary>« root@srv01 », ou « admin@corp.local → srv01 » pour un compte de domaine.</summary>
    public string Display => RemoteMachine is null
        ? $"{Account.UserName}@{Account.Address}"
        : $"{Account.UserName}@{Account.Address} → {RemoteMachine}";

    /// <summary>Plateforme et safe du compte, pour distinguer deux comptes au même nom.</summary>
    public string Details => string.Join(" · ", new[] { Account.PlatformId, Account.SafeName }.Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>
    /// Compte de domaine proposé faute de mieux, d'un autre domaine que le serveur (administrator@corp.local pour
    /// web01.dmz.local) : une supposition, à vérifier, jamais cochée d'office.
    /// </summary>
    public bool OtherDomain { get; init; }

    public override string ToString() => Display;
}

/// <summary>
/// Recherche, parmi les comptes du PVWA, ceux qui correspondent à une session d'un autre logiciel :
/// <list type="number">
/// <item>comptes du serveur lui-même : même adresse, ou même nom court (srv01 et srv01.corp.local) ;</item>
/// <item>avec le même utilisateur s'il est indiqué (domaine compris : CORP\admin ne désigne pas le compte local admin) ;</item>
/// <item>d'un type adapté (Windows pour le Bureau à distance, Unix ou réseau pour SSH), sinon de tout type sauf base de données ;</item>
/// <item>à défaut, comptes de domaine du même utilisateur (domaine indiqué, ou session Bureau à distance), autorisés sur ce
/// serveur, avec le serveur pour machine cible.</item>
/// </list>
/// Les comptes trouvés sont classés du plus probable au moins probable. Aucune résolution DNS : seuls les noms comptent.
/// </summary>
public sealed class SessionMatcher
{
    private readonly KnownDomains? _domains;
    private readonly Dictionary<string, List<PvwaAccount>> _byAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PvwaAccount>> _byShortName = new(StringComparer.Ordinal);
    private readonly List<PvwaAccount> _domainAccounts = [];

    public SessionMatcher(IEnumerable<PvwaAccount> accounts, KnownDomains? domains = null)
    {
        _domains = domains;
        foreach (var account in accounts)
        {
            if (AccountClassifier.Classify(account) == AccountKind.Database)
            {
                continue;
            }

            if (AccountClassifier.IsDomainAccount(account, domains))
            {
                _domainAccounts.Add(account);
                continue;
            }

            var address = PsmpRouting.NormalizeHost(account.Address);
            if (address.Length == 0)
            {
                continue;
            }

            Add(_byAddress, address, account);
            if (!IPAddress.TryParse(address, out _))
            {
                Add(_byShortName, ShortName(address), account);
            }
        }
    }

    public IReadOnlyList<ImportCandidate> Candidates(ImportedSession session)
    {
        var host = PsmpRouting.NormalizeHost(session.Host);
        if (session.Protocol == ImportProtocol.Other || host.Length == 0)
        {
            return [];
        }

        bool isIp = IPAddress.TryParse(host, out _);
        var user = session.User;
        var domain = session.Domain is { } d && (d == "." || PsmpRouting.NormalizeHost(d) == ShortName(host)) ? null : session.Domain;
        bool local = session.Domain is not null && domain is null;

        // Comptes du serveur : même adresse (rang 3), ou même nom court quand l'un des deux noms est court (rang 2).
        var direct = new List<(PvwaAccount Account, int Rank)>();
        direct.AddRange(_byAddress.GetValueOrDefault(host, []).Select(a => (a, 3)));
        if (!isIp)
        {
            direct.AddRange(_byShortName.GetValueOrDefault(ShortName(host), [])
                .Where(a => PsmpRouting.NormalizeHost(a.Address) is var address && address != host
                            && ((!host.Contains('.') && address.StartsWith(host + ".", StringComparison.Ordinal))
                                || (!address.Contains('.') && host.StartsWith(address + ".", StringComparison.Ordinal))))
                .Select(a => (a, 2)));
        }

        if (user is not null)
        {
            // Domaine indiqué : celui du compte. Compte local indiqué (.\admin, SRV01\admin) : un compte sans domaine.
            direct = direct.Where(c => SameUser(c.Account, user)
                                       && (domain is not null ? SameDomain(AccountDomain(c.Account), domain)
                                           : !local || AccountDomain(c.Account) is not { } own || own == "."
                                             || PsmpRouting.NormalizeHost(own) == ShortName(host)))
                .ToList();
        }

        var preferred = Preferred(session.Protocol);
        var kinds = direct.Where(c => preferred.Contains(AccountClassifier.Classify(c.Account))).ToList();
        var found = (kinds.Count > 0 ? kinds : direct)
            .Select(c => (Candidate: new ImportCandidate(c.Account, NeedsMachine(c.Account) ? session.Host.Trim() : null),
                Score: c.Rank * 10 + (preferred.Contains(AccountClassifier.Classify(c.Account)) ? 1 : 0)))
            .ToList();

        // Comptes de domaine : utilisateur indiqué, sans compte du serveur lui-même (ou domaine explicitement indiqué).
        // Sans domaine indiqué, seulement pour le Bureau à distance : « admin » sur un équipement SSH ou Telnet n'est pas
        // le compte de domaine « admin ». Sans domaine indiqué non plus, un compte d'un autre domaine que celui du nom du
        // serveur (« administrator » de web01.dmz.local : souvent son compte local, absent du coffre) n'est qu'une
        // supposition : proposé après ceux du domaine du serveur, et à vérifier (sauf s'il est limité à ce serveur).
        if (user is not null && !local && (found.Count == 0 || domain is not null)
            && (domain is not null || session.Protocol == ImportProtocol.Rdp))
        {
            var serverDomain = domain is null ? PsmpRouting.DomainOf(host) : "";
            found.AddRange(_domainAccounts
                .Where(a => SameUser(a, user) && (domain is null || SameDomain(AccountDomain(a), domain)) && AllowedOn(a, host))
                .Select(a => serverDomain.Length > 0 && !AccountClassifier.IsRestrictedToRemoteMachines(a)
                             && !SameDomain(AccountDomain(a), serverDomain)
                    ? (new ImportCandidate(a, session.Host.Trim()) { OtherDomain = true }, 9)
                    : (new ImportCandidate(a, session.Host.Trim()), 10)));
        }

        return found
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Candidate.Account.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Candidate.Account.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Candidate.Account.Id, StringComparer.Ordinal)
            .Select(c => c.Candidate)
            .DistinctBy(c => c.Account.Id)
            .ToList();
    }

    private static IReadOnlyList<AccountKind> Preferred(ImportProtocol protocol) => protocol == ImportProtocol.Rdp
        ? [AccountKind.Windows, AccountKind.Other]
        : [AccountKind.Unix, AccountKind.Network, AccountKind.Other];

    /// <summary>Compte limité à des machines, ou compte de domaine : la session vise le serveur importé.</summary>
    private bool NeedsMachine(PvwaAccount account) => AccountClassifier.NeedsRemoteMachine(account, _domains);

    private static bool AllowedOn(PvwaAccount account, string host)
    {
        if (!AccountClassifier.IsRestrictedToRemoteMachines(account))
        {
            return true;
        }

        return AccountClassifier.RemoteMachineList(account)
            .Select(PsmpRouting.NormalizeHost)
            .Any(m => m == host || ShortName(m) == host || m == ShortName(host));
    }

    private static bool SameUser(PvwaAccount account, string user) =>
        string.Equals(ImportedSession.SplitUser(account.UserName).User, user, StringComparison.OrdinalIgnoreCase);

    /// <summary>Domaine d'un compte : domaine de connexion, celui de son nom d'utilisateur, ou son adresse (compte de domaine).</summary>
    private string? AccountDomain(PvwaAccount account)
    {
        if (account.LogonDomain.Trim() is { Length: > 0 } logon)
        {
            return logon;
        }

        if (ImportedSession.SplitUser(account.UserName).Domain is { } fromUser)
        {
            return fromUser;
        }

        return AccountClassifier.IsDomainAccount(account, _domains) ? account.Address : null;
    }

    /// <summary>CORP, corp.local et CORP.LOCAL désignent le même domaine (nom NetBIOS = première partie du nom DNS).</summary>
    private static bool SameDomain(string? accountDomain, string domain)
    {
        var a = PsmpRouting.NormalizeDomain(accountDomain);
        var b = PsmpRouting.NormalizeDomain(domain);
        return a.Length > 0 && (a == b || ShortName(a) == b || a == ShortName(b));
    }

    private static string ShortName(string host)
    {
        int dot = host.IndexOf('.');
        return dot > 0 && !IPAddress.TryParse(host, out _) ? host[..dot] : host;
    }

    private static void Add(Dictionary<string, List<PvwaAccount>> index, string key, PvwaAccount account)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(account);
    }
}
