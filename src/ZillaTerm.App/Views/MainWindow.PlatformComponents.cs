using System.Net;
using System.Net.Http;
using System.Text.Json;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Views;

/// <summary>
/// Composants PSM des plateformes, lus sur le PVWA (« Get platform details ») une fois par plateforme et par session :
/// un composant prévu que la plateforme n'a pas est remplacé par le premier qu'elle propose (le PVWA le refuserait), et
/// les listes de composants (« Connexion avancée », propriétés d'un serveur) proposent d'abord les siens.
/// </summary>
public partial class MainWindow
{
    /// <summary>Attente maximale de la lecture : au-delà, la connexion part avec le composant prévu.</summary>
    private static readonly TimeSpan PlatformComponentsWait = TimeSpan.FromSeconds(5);

    // Composants lus, par plateforme ; liste vide : aucun dans la réponse, ou lecture refusée (plus redemandée).
    private readonly Dictionary<string, IReadOnlyList<string>> _platformComponents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Composants de la plateforme déjà lus pendant la session ; null s'ils ne sont pas connus.</summary>
    private IReadOnlyList<string>? KnownPlatformComponents(string? platformId) =>
        platformId is not null && _platformComponents.GetValueOrDefault(platformId.Trim()) is { Count: > 0 } components
            ? components
            : null;

    /// <summary>
    /// Composants PSM de la plateforme du compte, lus sur le PVWA à la première demande (jamais hors ligne), en
    /// <see cref="PlatformComponentsWait"/> au plus ; liste vide s'ils ne sont pas connus. Refus du PVWA : plus redemandé
    /// pour cette plateforme pendant la session. Délai dépassé ou autre échec : rien de mémorisé.
    /// </summary>
    private async Task<IReadOnlyList<string>> PlatformComponentsAsync(PvwaAccount account)
    {
        var platform = account.PlatformId?.Trim();
        if (IsOffline || string.IsNullOrEmpty(platform))
        {
            return [];
        }

        if (_platformComponents.TryGetValue(platform, out var known))
        {
            return known;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        wait.CancelAfter(PlatformComponentsWait);
        try
        {
            var components = await Client.GetPlatformConnectionComponentsAsync(platform, wait.Token);
            _platformComponents[platform] = components;
            DebugLog.Write("psm", components.Count == 0
                ? $"Plateforme {platform} : aucun composant PSM dans la réponse du PVWA"
                : $"Composants PSM de la plateforme {platform} (PVWA) : {string.Join(", ", components)}");
            return components;
        }
        catch (PvwaException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                                           or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            _platformComponents[platform] = [];
            DebugLog.Write("psm",
                $"Composants PSM de la plateforme {platform} refusés par le PVWA ({ex.Message}) : plus demandés pendant la session");
            return [];
        }
        catch (Exception ex) when (ex is PvwaException or HttpRequestException or JsonException or OperationCanceledException
                                       or InvalidOperationException)
        {
            DebugLog.Write("psm", wait.IsCancellationRequested && !_lifetime.IsCancellationRequested
                ? $"Composants PSM de la plateforme {platform} non lus (délai dépassé) : composant prévu"
                : $"Composants PSM de la plateforme {platform} non lus : composant prévu", ex);
            return [];
        }
    }

    /// <summary>
    /// Requête dont le composant est absent de la plateforme (composants lus sur le PVWA) : le premier de la plateforme à
    /// la place, noté au journal. Jamais appliqué au composant saisi dans « Connexion avancée ».
    /// </summary>
    private static ConnectRequest WithPlatformComponent(PvwaAccount account, ConnectRequest request,
        IReadOnlyList<string> platformComponents)
    {
        var component = PlatformComponents.Choose(request.Component, platformComponents);
        if (string.Equals(component, request.Component, StringComparison.Ordinal))
        {
            return request;
        }

        DebugLog.Write("psm", $"Composant {request.Component} absent de la plateforme {account.PlatformId} : {component} à la place");
        return request with { Component = component };
    }
}
