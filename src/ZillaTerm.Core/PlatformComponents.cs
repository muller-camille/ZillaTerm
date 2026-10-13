using System.Text.Json;

namespace ZillaTerm.Core;

/// <summary>
/// Composants de connexion PSM d'une plateforme, lus dans la réponse de « Get platform details »
/// (<c>GET API/Platforms/{PlatformID}/</c>, lisible par un utilisateur ordinaire), et choix du composant à demander.
/// Le format de cette réponse varie selon la version du PVWA : la lecture cherche les listes de composants à tout niveau.
/// </summary>
public static class PlatformComponents
{
    /// <summary>Imbrication JSON acceptée : la réponse est petite, une imbrication plus profonde est refusée.</summary>
    public const int MaxDepth = 32;

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = MaxDepth };

    // Listes de composants, au pluriel (« Details.ConnectionComponents », « PrivilegedSessionManagement.PSMConnectors »),
    // ou au singulier dans une enveloppe tirée du XML de la plateforme (« ConnectionComponents.ConnectionComponent »).
    private static readonly string[] ListNames = ["ConnectionComponents", "PSMConnectors", "ConnectionComponent", "PSMConnector"];

    // Nom d'un composant décrit par un objet : première de ces propriétés qui porte un texte (casse ignorée).
    private static readonly string[] NameProperties =
        ["PSMConnectorID", "ConnectionComponentID", "ID", "Name", "ConnectionComponent", "PSMConnector"];

    /// <summary>Composants PSM activés, dans leur ordre et sans doublon ; liste vide si la réponse n'en contient pas.</summary>
    /// <exception cref="JsonException">Réponse qui n'est pas du JSON, ou imbriquée au-delà de <see cref="MaxDepth"/>.</exception>
    public static IReadOnlyList<string> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);
        return Read(document.RootElement);
    }

    /// <summary>Composants PSM activés trouvés dans <paramref name="root"/>, dans leur ordre et sans doublon.</summary>
    public static IReadOnlyList<string> Read(JsonElement root)
    {
        var names = new List<string>();
        Search(root, names, 0);
        return names;
    }

    /// <summary>
    /// Composant utilisable pour une connexion PSM : nom valide (règle des Paramètres), sauf ceux du PSMP (« PSMP-… ») et
    /// les modes « SSH » / « SFTP » des connexions récentes, qui ne sont pas des composants PSM.
    /// </summary>
    public static bool IsPsmComponent(string? name)
    {
        var value = name?.Trim() ?? "";
        return AppSettings.IsValidComponentName(value) && !value.StartsWith("PSMP-", StringComparison.OrdinalIgnoreCase)
               && SessionLibrary.RecentMode(value) == ConnectMode.Psm;
    }

    /// <summary>
    /// Composant à demander au PVWA : <paramref name="planned"/> s'il est l'un des composants de la plateforme, ou si
    /// ceux-ci ne sont pas connus (liste vide ou null) ; sinon le premier de la plateforme, car le PVWA refuserait un
    /// composant que la plateforme n'a pas (EPVWA093E).
    /// </summary>
    public static string Choose(string planned, IReadOnlyList<string>? platformComponents) =>
        platformComponents is { Count: > 0 } && !platformComponents.Contains(planned.Trim(), StringComparer.OrdinalIgnoreCase)
            ? platformComponents[0]
            : planned;

    private static void Search(JsonElement element, List<string> names, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsListName(property.Name))
                    {
                        AddList(property.Value, names, depth + 1);
                    }
                    else
                    {
                        Search(property.Value, names, depth + 1);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Search(item, names, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Valeur d'une liste de composants : tableau de noms ou d'objets, objet seul (un composant, ou une enveloppe qui
    /// contient la liste), ou texte « PSM-RDP, PSM-WinSCP ».
    /// </summary>
    private static void AddList(JsonElement value, List<string> names, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        Add(item.GetString(), names);
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        AddObject(item, names, depth + 1);
                    }
                }

                break;
            case JsonValueKind.Object:
                AddObject(value, names, depth);
                break;
            case JsonValueKind.String:
                foreach (var name in (value.GetString() ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                {
                    Add(name, names);
                }

                break;
        }
    }

    /// <summary>
    /// Composant décrit par un objet, ignoré s'il est désactivé ; enveloppe tirée du XML de la plateforme
    /// (« ConnectionComponents »: { « ConnectionComponent »: [...] }) : la liste est cherchée dedans.
    /// </summary>
    private static void AddObject(JsonElement item, List<string> names, int depth)
    {
        if (item.EnumerateObject().Any(p => IsListName(p.Name) && p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
        {
            Search(item, names, depth);
            return;
        }

        foreach (var candidate in NameProperties)
        {
            if (Property(item, candidate) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                if (IsEnabled(Property(item, "Enabled") ?? Property(item, "Enable")))
                {
                    Add(value.GetString(), names);
                }

                return;
            }
        }
    }

    /// <summary>« Enabled » : booléen, ou texte « Yes » / « No » / « true » / « false » ; absent ou autre : activé.</summary>
    private static bool IsEnabled(JsonElement? enabled) => enabled switch
    {
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } text => text.GetString()?.Trim().ToUpperInvariant() is not ("NO" or "FALSE"),
        _ => true,
    };

    private static bool IsListName(string name) => ListNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static JsonElement? Property(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static void Add(string? name, List<string> names)
    {
        var value = name?.Trim() ?? "";
        if (IsPsmComponent(value) && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(value);
        }
    }
}
