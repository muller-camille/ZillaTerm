namespace ZillaTerm.Core.Ssh;

public enum HostKeyStatus
{
    Trusted,
    Unknown,
    Changed,

    /// <summary>Clé d'un type jamais vu pour ce serveur, qui en a déjà d'un autre type : à vérifier, jamais acceptée d'office.</summary>
    NewAlgorithm,
}

/// <summary>
/// Empreintes des clés d'hôte déjà acceptées (« premier usage de confiance », comme known_hosts), par serveur et par type
/// de clé : un serveur peut en présenter plusieurs (RSA, puis ed25519), chacune vérifiée une fois.
/// Clé « hôte:port » → « algorithme SHA256:empreinte » pour la première clé acceptée (format des versions précédentes),
/// « hôte:port type » pour les clés d'un autre type.
/// </summary>
public static class KnownHosts
{
    public static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public static string Format(string algorithm, string sha256) => $"{algorithm} SHA256:{sha256}";

    /// <summary>
    /// Type de la clé : les signatures RSA (ssh-rsa, rsa-sha2-256, rsa-sha2-512) portent la même clé, dont l'empreinte
    /// ne dépend pas de l'algorithme négocié.
    /// </summary>
    public static string KeyType(string algorithm) => algorithm switch
    {
        "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
        "rsa-sha2-256-cert-v01@openssh.com" or "rsa-sha2-512-cert-v01@openssh.com" => "ssh-rsa-cert-v01@openssh.com",
        _ => algorithm,
    };

    /// <summary>Serveur (« hôte:port ») d'une clé des réglages.</summary>
    public static string Server(string entry)
    {
        int space = entry.IndexOf(' ');
        return space > 0 ? entry[..space] : entry;
    }

    /// <summary>Empreintes acceptées pour ce serveur, tous types confondus (algorithme, empreinte SHA256 sans préfixe).</summary>
    public static IReadOnlyList<(string Algorithm, string Sha256)> KnownKeys(IReadOnlyDictionary<string, string> store, string host, int port) =>
        KnownKeys(store, Key(host, port));

    /// <param name="server">« hôte:port », comme le donne <see cref="Key"/>.</param>
    public static IReadOnlyList<(string Algorithm, string Sha256)> KnownKeys(IReadOnlyDictionary<string, string> store, string server)
    {
        var keys = new List<(string, string)>();
        if (store.TryGetValue(server, out var first))
        {
            keys.Add(Parse(first));
        }

        foreach (var (entry, value) in store)
        {
            if (entry.Length > server.Length + 1 && entry[server.Length] == ' ' && entry.StartsWith(server, StringComparison.Ordinal))
            {
                keys.Add(Parse(value));
            }
        }

        return keys;
    }

    public static HostKeyStatus Check(IReadOnlyDictionary<string, string> store, string host, int port, string algorithm, string sha256)
    {
        var keys = KnownKeys(store, host, port);
        if (keys.Count == 0)
        {
            return HostKeyStatus.Unknown;
        }

        // Une empreinte notée sans algorithme (fichier modifié à la main) vaut pour tous les types : jamais « nouveau type ».
        var sameType = keys.Where(k => k.Algorithm.Length == 0 || KeyType(k.Algorithm) == KeyType(algorithm)).ToList();
        if (sameType.Count == 0)
        {
            return HostKeyStatus.NewAlgorithm;
        }

        return sameType.Any(k => string.Equals(k.Sha256, sha256, StringComparison.Ordinal)) ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
    }

    /// <summary>Empreinte déjà acceptée pour ce serveur et ce type de clé (algorithme, empreinte SHA256 sans préfixe), ou null.</summary>
    public static (string Algorithm, string Sha256)? Known(IReadOnlyDictionary<string, string> store, string host, int port, string algorithm)
    {
        foreach (var key in KnownKeys(store, host, port))
        {
            if (key.Algorithm.Length == 0 || KeyType(key.Algorithm) == KeyType(algorithm))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Mémorise la clé de ce type pour ce serveur, à la place de celle du même type (clé changée et confirmée) ; les
    /// clés des autres types restent acceptées.
    /// </summary>
    public static void Remember(IDictionary<string, string> store, string host, int port, string algorithm, string sha256)
    {
        var server = Key(host, port);
        var type = KeyType(algorithm);
        // Ancienne clé de ce type retirée où qu'elle soit : elle ne doit plus être acceptée.
        foreach (var entry in store.Keys.Where(e => Server(e) == server && e != server).ToList())
        {
            if (KeyType(Parse(store[entry]).Algorithm) == type)
            {
                store.Remove(entry);
            }
        }

        if (store.TryGetValue(server, out var first) && Parse(first).Algorithm is { Length: > 0 } firstAlgorithm && KeyType(firstAlgorithm) != type)
        {
            store[$"{server} {type}"] = Format(algorithm, sha256);
        }
        else
        {
            store[server] = Format(algorithm, sha256);
        }
    }

    /// <summary>
    /// Ajoute une clé de ce type pour ce serveur sans retirer les autres : plusieurs serveurs derrière une même adresse
    /// (PSM en répartition de charge), chacun avec sa clé. Clé « hôte:port type empreinte ».
    /// </summary>
    public static void RememberAnother(IDictionary<string, string> store, string host, int port, string algorithm, string sha256) =>
        store[$"{Key(host, port)} {KeyType(algorithm)} {sha256[..Math.Min(16, sha256.Length)]}"] = Format(algorithm, sha256);

    /// <summary>Nombre de clés de ce type acceptées pour ce serveur.</summary>
    public static int CountKeys(IReadOnlyDictionary<string, string> store, string host, int port, string algorithm) =>
        KnownKeys(store, host, port).Count(k => KeyType(k.Algorithm) == KeyType(algorithm));

    private static (string Algorithm, string Sha256) Parse(string value)
    {
        int space = value.IndexOf(' ');
        var algorithm = space > 0 ? value[..space] : "";
        var fingerprint = space > 0 ? value[(space + 1)..] : value;
        return (algorithm, fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? fingerprint["SHA256:".Length..] : fingerprint);
    }
}
