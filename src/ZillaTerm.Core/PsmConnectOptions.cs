namespace ZillaTerm.Core;

/// <summary>Paramètres d'une demande de connexion PSM (<c>POST API/Accounts/{id}/PSMConnect</c>).</summary>
public sealed class PsmConnectOptions
{
    /// <summary>Composant de connexion PSM, par ex. <c>PSM-RDP</c>, <c>PSM-SSH</c>, <c>PSM-WinSCP</c>.</summary>
    public required string ConnectionComponent { get; init; }

    /// <summary>Motif d'accès, obligatoire si la plateforme l'exige.</summary>
    public string? Reason { get; init; }

    public string? TicketingSystemName { get; init; }

    public string? TicketId { get; init; }

    /// <summary>Machine cible pour un compte de domaine (paramètre <c>PSMRemoteMachine</c>).</summary>
    public string? RemoteMachine { get; init; }

    /// <summary>
    /// Disques du poste partagés avec la session (paramètre <c>AllowMappingLocalDrives</c> : case « Map local drives » de
    /// la fenêtre de connexion du PVWA). Le PVWA l'écrit dans le fichier RDP si le composant de connexion le permet.
    /// </summary>
    public bool MapLocalDrives { get; init; }

    /// <summary>La même demande sans <see cref="MapLocalDrives"/>.</summary>
    internal PsmConnectOptions WithoutLocalDrives() => new()
    {
        ConnectionComponent = ConnectionComponent,
        Reason = Reason,
        TicketingSystemName = TicketingSystemName,
        TicketId = TicketId,
        RemoteMachine = RemoteMachine,
    };

    internal Dictionary<string, object> ToRequestBody()
    {
        var body = new Dictionary<string, object> { ["ConnectionComponent"] = ConnectionComponent.Trim() };
        if (!string.IsNullOrWhiteSpace(Reason))
        {
            body["reason"] = Reason.Trim();
        }

        if (!string.IsNullOrWhiteSpace(TicketingSystemName))
        {
            body["TicketingSystemName"] = TicketingSystemName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(TicketId))
        {
            body["TicketId"] = TicketId.Trim();
        }

        var parameters = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(RemoteMachine))
        {
            parameters["PSMRemoteMachine"] = new Dictionary<string, object> { ["value"] = RemoteMachine.Trim(), ["ShouldSave"] = false };
        }

        if (MapLocalDrives)
        {
            parameters["AllowMappingLocalDrives"] = new Dictionary<string, object> { ["value"] = "Yes", ["ShouldSave"] = false };
        }

        if (parameters.Count > 0)
        {
            body["ConnectionParams"] = parameters;
        }

        return body;
    }
}
