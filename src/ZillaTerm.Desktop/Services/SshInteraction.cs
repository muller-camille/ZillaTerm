using Avalonia.Controls;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Services;

/// <summary>
/// Question du serveur pendant l'authentification, telle que la fenêtre de question (PromptDialog) la reçoit : les
/// paramètres de son constructeur, dans le même ordre, et son titre (null : celui par défaut).
/// </summary>
internal sealed record SshPromptRequest(string Instruction, string Prompt, bool Echo, string? Session, string? Hint, bool Direct,
    string? Refused, bool OfferShare, string? Title);

/// <summary>
/// Questions posées par SSH.NET pendant la connexion (clé d'hôte, mot de passe, code MFA), relayées sur le thread de
/// l'interface. <paramref name="direct"/> : serveur joint directement (accès d'urgence) plutôt que le PSMP.
/// <paramref name="context"/> : session qui pose la question (plusieurs peuvent s'ouvrir ensemble).
/// </summary>
/// <remarks>
/// Les dialogues d'Avalonia sont asynchrones : le fil de SSH.NET attend la réponse (<see cref="UiWait"/>), comme avec
/// <c>Dispatcher.Invoke</c> et <c>ShowDialog</c> en WPF.
/// </remarks>
internal sealed class SshInteraction(Window owner, AppSettings settings, Action saveSettings, bool direct = false, string? context = null)
    : ISshInteraction
{
    /// <summary>
    /// Affiche la question du serveur (PromptDialog, portée avec les autres dialogues) et rend la réponse, ou null si
    /// l'utilisateur annule ; fournie au démarrage par l'application. Sans elle, la question est annulée (la connexion
    /// échoue) : aucune réponse n'est inventée.
    /// </summary>
    internal static Func<Window, SshPromptRequest, Task<SshAnswer?>>? ShowPrompt { get; set; }

    /// <summary>Les mêmes questions, nommant la session <paramref name="session"/> (« compte → cible »).</summary>
    public SshInteraction For(string session) => new(owner, settings, saveSettings, direct, session);

    public bool CheckHostKey(string host, int port, string algorithm, string sha256Fingerprint) =>
        UiWait.Run(async () =>
        {
            // PSMP sans clé acceptée à la connexion : celle de son fichier d'environnement. Jamais pour une connexion
            // directe : le même serveur, déclaré PSMP dans un fichier, ne ferait plus vérifier sa clé.
            var keys = KnownHosts.HostFor(settings.KnownHosts, host, port, psmp: !direct);
            var status = KnownHosts.Check(settings.KnownHosts, keys, port, algorithm, sha256Fingerprint);
            if (status == HostKeyStatus.Trusted)
            {
                return true;
            }

            if (!await AskHostKeyAsync(owner, direct, host, port, algorithm, sha256Fingerprint, status,
                    status == HostKeyStatus.Changed
                        ? [KnownHosts.Known(settings.KnownHosts, keys, port, algorithm)!.Value]
                        : KnownHosts.KnownKeys(settings.KnownHosts, keys, port)))
            {
                return false;
            }

            KnownHosts.Remember(settings.KnownHosts, host, port, algorithm, sha256Fingerprint);
            saveSettings();
            return true;
        });

    // Réglages lus sur le fil de l'interface ; appelé depuis lui (début de connexion), Invoke exécute directement.
    public IReadOnlyCollection<string> KnownHostKeyTypes(string host, int port) =>
        Dispatcher.UIThread.Invoke(() => KnownHosts.KnownKeys(settings.KnownHosts, KnownHosts.HostFor(settings.KnownHosts, host, port, psmp: !direct), port)
            .Select(k => KnownHosts.KeyType(k.Algorithm)).ToList());

    /// <summary>
    /// Clé d'hôte inconnue, changée ou d'un nouveau type : empreinte à comparer (copiable), « Annuler la connexion » par
    /// défaut. Une clé changée montre l'ancienne et la nouvelle empreinte, et ne s'accepte qu'après avoir coché
    /// « J'ai confirmé… » ; une clé d'un type jamais vu pour ce serveur montre aussi celles déjà acceptées, et ne
    /// s'accepte qu'après avoir coché « J'ai vérifié… ».
    /// </summary>
    /// <param name="known">Clé remplacée (clé changée) ou clés déjà acceptées pour ce serveur (nouveau type).</param>
    internal static Task<bool> AskHostKeyAsync(Window owner, bool direct, string host, int port, string algorithm, string sha256,
        HostKeyStatus status, IReadOnlyList<(string Algorithm, string Sha256)> known) =>
        ConfirmDialog.ConfirmAsync(owner, HostKeyQuestion(direct, host, port, algorithm, sha256, status, known));

    /// <summary>Contenu de la question de <see cref="AskHostKeyAsync"/>.</summary>
    internal static ConfirmRequest HostKeyQuestion(bool direct, string host, int port, string algorithm, string sha256, HostKeyStatus status,
        IReadOnlyList<(string Algorithm, string Sha256)> known)
    {
        var receives = direct ? Strings.HostKeyServerReceives : Strings.HostKeyPsmpReceives;
        var title = direct ? Strings.HostKeyTitleServer : Strings.HostKeyTitle;
        var subject = $"{host}:{port}";
        if (status == HostKeyStatus.NewAlgorithm)
        {
            return new ConfirmRequest
            {
                Title = title,
                Heading = direct ? Strings.HostKeyNewTypeServerHeading : Strings.HostKeyNewTypePsmpHeading,
                Subject = subject,
                Message = direct ? Strings.HostKeyNewTypeServerMessage : Strings.HostKeyNewTypePsmpMessage,
                Codes =
                [
                    .. known.Select(k => (Text.Format(Strings.HostKeyOld, k.Algorithm), "SHA256:" + k.Sha256)),
                    (Text.Format(Strings.HostKeyNew, algorithm), "SHA256:" + sha256),
                ],
                Bullets = [receives, Strings.HostKeyBothKept],
                Kind = ConfirmKind.Warning,
                Actions = [Strings.HostKeyAddKey],
                CancelLabel = Strings.HostKeyCancel,
                Acknowledge = direct ? Strings.HostKeyAckNewServer : Strings.HostKeyAckNewPsmp,
            };
        }

        if (status != HostKeyStatus.Changed)
        {
            return new ConfirmRequest
            {
                Title = title,
                Heading = direct ? Strings.HostKeyVerifyServer : Strings.HostKeyVerifyPsmp,
                Subject = subject,
                Message = direct ? Strings.HostKeyFirstServer : Strings.HostKeyFirstPsmp,
                Codes = [(Text.Format(Strings.HostKeyFingerprint, algorithm), "SHA256:" + sha256)],
                Bullets = [receives, Strings.HostKeyRemembered],
                Kind = ConfirmKind.Question,
                Actions = [Strings.HostKeyTrust],
                CancelLabel = Strings.HostKeyCancel,
            };
        }

        return new ConfirmRequest
        {
            Title = title,
            Banner = Strings.HostKeyChangedBanner,
            Heading = direct ? Strings.HostKeyChangedServerHeading : Strings.HostKeyChangedPsmpHeading,
            Subject = subject,
            Message = direct ? Strings.HostKeyChangedServerMessage : Strings.HostKeyChangedPsmpMessage,
            Codes =
            [
                (Text.Format(Strings.HostKeyOld, known[0].Algorithm), "SHA256:" + known[0].Sha256),
                (Text.Format(Strings.HostKeyNew, algorithm), "SHA256:" + sha256),
            ],
            Kind = ConfirmKind.Danger,
            Actions = [Strings.HostKeyReplace],
            DangerAction = 0,
            CancelLabel = Strings.HostKeyCancel,
            Acknowledge = direct ? Strings.HostKeyAckServer : Strings.HostKeyAckPsmp,
        };
    }

    public SshAnswer? Prompt(SshQuestion question) =>
        UiWait.Run(async () =>
        {
            // Aide selon la question : mot de passe (gardé pour les fichiers de l'onglet) ou code MFA (redemandé).
            string? hint = SshConnector.IsPasswordPrompt(question.Prompt) && !question.Echo
                ? (direct ? Strings.PromptHintPassword : Strings.PromptHintVaultPassword)
                : !direct && LooksLikeOneTimeCode(question.Prompt) ? Strings.PromptHintMfa : null;
            if (ShowPrompt is not { } show)
            {
                DebugLog.Write("ssh", "Question du serveur sans fenêtre pour y répondre (PromptDialog non fournie) : annulée");
                return null;
            }

            return await show(owner, new SshPromptRequest(question.Instruction, question.Prompt, question.Echo, context, hint, direct,
                question.Refused, question.OfferShare, direct ? Strings.PromptTitleServer : null));
        });

    private static bool LooksLikeOneTimeCode(string prompt) =>
        new[] { "code", "otp", "token", "passcode", "mfa", "radius" }.Any(w => prompt.Contains(w, StringComparison.OrdinalIgnoreCase));
}
