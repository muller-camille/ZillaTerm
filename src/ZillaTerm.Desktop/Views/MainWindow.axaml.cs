using Avalonia.Controls;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    internal MainWindow(PvwaClient? client, AppSettings settings, string sessionUser, string vaultUser, AuthMethod method)
        : this()
    {
    }

    /// <summary>Vrai si la fenêtre a été fermée pour revenir à l'écran de connexion.</summary>
    public bool LogoutRequested { get; private set; }
}
