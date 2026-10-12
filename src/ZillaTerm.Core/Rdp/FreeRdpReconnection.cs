using System.Globalization;
using System.Text.RegularExpressions;

namespace ZillaTerm.Core.Rdp;

/// <summary>Étape de la reconnexion automatique de FreeRDP, lue dans son journal (voir <see cref="FreeRdpReconnection"/>).</summary>
public enum FreeRdpReconnectionStep
{
    /// <summary>Ligne sans rapport avec la reconnexion.</summary>
    None,

    /// <summary>Connexion perdue : FreeRDP commence à se reconnecter.</summary>
    Lost,

    /// <summary>Nouvelle tentative (numéro et nombre maximal dans <see cref="FreeRdpReconnection.Attempt"/>).</summary>
    Attempt,

    /// <summary>Session reprise.</summary>
    Reconnected,
}

/// <summary>
/// Reconnexion automatique de FreeRDP, d'après les lignes de son journal (com.freerdp.client.common, niveau INFO) :
/// « Network disconnect! », puis « Attempting reconnect (n of m) » à chaque tentative, et « Reconnected » quand la
/// session est reprise (ajouté par le fork). Un abandon termine le client : sa fin est traitée à part.
/// </summary>
public static partial class FreeRdpReconnection
{
    /// <summary>Étape annoncée par <paramref name="line"/>, et pour une tentative son numéro (à partir de 1) et leur nombre maximal.</summary>
    public static (FreeRdpReconnectionStep Step, int Attempt, int Max) Read(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains("[client_auto_reconnect_", StringComparison.Ordinal))
        {
            return (FreeRdpReconnectionStep.None, 0, 0);
        }

        if (line.EndsWith("]: Network disconnect!", StringComparison.Ordinal)
            || line.Contains("]: Disconnected by server hitting a bug or resource limit", StringComparison.Ordinal))
        {
            return (FreeRdpReconnectionStep.Lost, 0, 0);
        }

        if (AttemptLine().Match(line) is { Success: true } attempt
            && int.TryParse(attempt.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int done)
            && int.TryParse(attempt.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int max))
        {
            // « 0 of 20 » : aucune tentative faite, la première commence.
            return (FreeRdpReconnectionStep.Attempt, done + 1, max);
        }

        return line.EndsWith("[client_auto_reconnect_do]: Reconnected", StringComparison.Ordinal)
            ? (FreeRdpReconnectionStep.Reconnected, 0, 0)
            : (FreeRdpReconnectionStep.None, 0, 0);
    }

    [GeneratedRegex(@"\[client_auto_reconnect_do\]: Attempting reconnect \((\d{1,6}) of (\d{1,6})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptLine();
}
