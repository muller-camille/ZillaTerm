using Avalonia.Automation;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;

namespace ZillaTerm.App.Views;

/// <summary>Question posée par le PSMP lors de l'authentification (mot de passe, code MFA...).</summary>
public partial class PromptDialog : DialogWindow
{
    private readonly bool _echo;

    public PromptDialog() => InitializeComponent();

    /// <param name="session">Session qui pose la question (« compte → cible ») ; null si inconnue.</param>
    /// <param name="hint">Aide selon la question (mot de passe CyberArk, code MFA…).</param>
    /// <param name="direct">Serveur joint directement (accès d'urgence) : pas de PSMP en jeu.</param>
    /// <param name="refused">Réponse précédente refusée par le serveur (numéro de l'essai) ; null au premier essai.</param>
    /// <param name="offerShare">Plusieurs sessions s'ouvrent ensemble : proposer de réutiliser ce mot de passe pour elles.</param>
    public PromptDialog(string instruction, string prompt, bool echo, string? session = null, string? hint = null, bool direct = false,
        string? refused = null, bool offerShare = false)
        : this()
    {
        ShareBox.IsVisible = offerShare;
        if (!string.IsNullOrWhiteSpace(refused))
        {
            RefusedText.Text = refused;
            RefusedText.IsVisible = true;
        }

        _echo = echo;
        InstructionText.Text = string.IsNullOrWhiteSpace(instruction)
            ? (direct ? Strings.PromptDefaultInstructionServer : Strings.PromptDefaultInstruction)
            : instruction.Trim();
        PromptText.Text = prompt.Trim();
        if (!string.IsNullOrWhiteSpace(session))
        {
            SessionText.Text = Text.Format(Strings.PromptSession, session);
            SessionText.IsVisible = true;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.IsVisible = true;
        }

        // Nom accessible des champs : la question du serveur. Réponse refusée : le refus (numéro de l'essai) est lu avec le
        // champ, qui prend le focus à l'ouverture ; sinon le lecteur d'écran redirait la même chose qu'au premier essai.
        AutomationProperties.SetName(SecretBox, PromptText.Text);
        AutomationProperties.SetName(TextBox, PromptText.Text);
        if (RefusedText.IsVisible)
        {
            AutomationProperties.SetHelpText(SecretBox, RefusedText.Text);
            AutomationProperties.SetHelpText(TextBox, RefusedText.Text);
        }

        SecretBox.IsVisible = !echo;
        CapsLockWarning.Attach(CapsLockText, SecretBox);
        TextBox.IsVisible = echo;
        Opened += (_, _) =>
        {
            Activate();
            FocusLater(echo ? TextBox : SecretBox);
        };
        // Fenêtre fermée sans envoi (Annuler, Échap, croix) : le mot de passe tapé ne reste pas dans le champ.
        Closed += (_, _) => SecretBox.Clear();
    }

    public string Answer { get; private set; } = "";

    /// <summary>Mot de passe à réutiliser pour les autres sessions ouvertes en même temps.</summary>
    public bool Share { get; private set; }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_echo)
        {
            Answer = TextBox.Text ?? "";
        }
        else
        {
            // La réponse part en chaîne (SshAnswer) ; le champ est vidé dès la lecture et la copie intermédiaire effacée.
            var secret = SecretInput.Read(SecretBox, clear: true);
            Answer = new string(secret);
            SecretInput.Clear(secret);
        }

        Share = ShareBox.IsVisible && ShareBox.IsChecked == true;
        DialogResult = true;
    }
}
