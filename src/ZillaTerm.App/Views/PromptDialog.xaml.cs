using System.Windows;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Views;

/// <summary>Question posée par le PSMP lors de l'authentification (mot de passe, code MFA...).</summary>
public partial class PromptDialog : Window
{
    private readonly bool _echo;

    /// <param name="session">Session qui pose la question (« compte → cible ») ; null si inconnue.</param>
    /// <param name="hint">Aide selon la question (mot de passe CyberArk, code MFA…).</param>
    /// <param name="direct">Serveur joint directement (accès d'urgence) : pas de PSMP en jeu.</param>
    /// <param name="refused">Réponse précédente refusée par le serveur (numéro de l'essai) ; null au premier essai.</param>
    /// <param name="offerShare">Plusieurs sessions s'ouvrent ensemble : proposer de réutiliser ce mot de passe pour elles.</param>
    public PromptDialog(string instruction, string prompt, bool echo, string? session = null, string? hint = null, bool direct = false,
        string? refused = null, bool offerShare = false)
    {
        InitializeComponent();
        ShareBox.Visibility = offerShare ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(refused))
        {
            RefusedText.Text = refused;
            RefusedText.Visibility = Visibility.Visible;
        }

        _echo = echo;
        InstructionText.Text = string.IsNullOrWhiteSpace(instruction)
            ? (direct ? Strings.PromptDefaultInstructionServer : Strings.PromptDefaultInstruction)
            : instruction.Trim();
        PromptText.Text = prompt.Trim();
        if (!string.IsNullOrWhiteSpace(session))
        {
            SessionText.Text = Text.Format(Strings.PromptSession, session);
            SessionText.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        // Nom accessible des champs : la question du serveur. Réponse refusée : le refus (numéro de l'essai) est lu avec le
        // champ, qui prend le focus à l'ouverture ; sinon le lecteur d'écran redirait la même chose qu'au premier essai.
        System.Windows.Automation.AutomationProperties.SetName(SecretBox, PromptText.Text);
        System.Windows.Automation.AutomationProperties.SetName(TextBox, PromptText.Text);
        if (RefusedText.Visibility == Visibility.Visible)
        {
            System.Windows.Automation.AutomationProperties.SetHelpText(SecretBox, RefusedText.Text);
            System.Windows.Automation.AutomationProperties.SetHelpText(TextBox, RefusedText.Text);
        }
        SecretBox.Visibility = echo ? Visibility.Collapsed : Visibility.Visible;
        CapsLockWarning.Attach(CapsLockText, SecretBox);
        TextBox.Visibility = echo ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            Activate();
            if (echo)
            {
                TextBox.Focus();
            }
            else
            {
                SecretBox.Focus();
            }
        };
    }

    public string Answer { get; private set; } = "";

    /// <summary>Mot de passe à réutiliser pour les autres sessions ouvertes en même temps.</summary>
    public bool Share { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Answer = _echo ? TextBox.Text : SecretBox.Password;
        Share = ShareBox.Visibility == Visibility.Visible && ShareBox.IsChecked == true;
        DialogResult = true;
    }
}
