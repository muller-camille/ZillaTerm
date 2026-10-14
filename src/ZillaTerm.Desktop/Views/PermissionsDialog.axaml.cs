using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Droits Unix (chmod) d'un ou plusieurs éléments : cases rwx, bits spéciaux et valeur octale synchronisés,
/// propagation au contenu des dossiers. Quand les éléments n'ont pas les mêmes droits, une case peut rester à l'état
/// intermédiaire : ce droit ne change alors sur aucun élément (comme « chmod g+w » au lieu de « chmod 664 »).
/// </summary>
public partial class PermissionsDialog : DialogWindow
{
    private readonly CheckBox[] _boxes = new CheckBox[12];

    /// <summary>Bit de chaque case : rwx (propriétaire, groupe, autres) puis setuid, setgid, sticky.</summary>
    private static readonly int[] BoxBits =
        [.. UnixPermissions.Bits, UnixPermissions.SetUid, UnixPermissions.SetGid, UnixPermissions.Sticky];

    private readonly bool?[] _initial = new bool?[12];
    private bool _updating;

    public PermissionsDialog() => InitializeComponent();

    /// <param name="target">Nom de l'élément, ou « N éléments ».</param>
    /// <param name="location">« serveur:dossier ».</param>
    /// <param name="modes">Droits actuels de chaque élément sélectionné (bits spéciaux compris).</param>
    /// <param name="hasDirectory">Un dossier fait partie de la sélection : la propagation au contenu est proposée.</param>
    public PermissionsDialog(string target, string location, IReadOnlyList<int> modes, bool hasDirectory)
        : this()
    {
        TargetText.Text = target;
        PathText.Text = location;
        RecursivePanel.IsVisible = hasDirectory;
        string[] who = [Strings.PermissionsOwner, Strings.PermissionsGroup, Strings.PermissionsOthers];
        string[] what = [Strings.PermissionsRead, Strings.PermissionsWrite, Strings.PermissionsExecute];
        for (int i = 0; i < 9; i++)
        {
            var box = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, $"{who[i / 3]} – {what[i % 3]}");
            Grid.SetRow(box, 1 + i / 3);
            Grid.SetColumn(box, 1 + i % 3);
            box.Click += OnBoxClick;
            BitsGrid.Children.Add(box);
            _boxes[i] = box;
        }

        _boxes[9] = SetUidBox;
        _boxes[10] = SetGidBox;
        _boxes[11] = StickyBox;
        bool mixed = false;
        for (int i = 0; i < 12; i++)
        {
            int set = modes.Count(m => (m & BoxBits[i]) != 0);
            _initial[i] = set == modes.Count ? true : set == 0 ? false : null;
            // Droit qui diffère d'un élément à l'autre : la case garde un troisième état, « inchangé ».
            _boxes[i].IsThreeState = _initial[i] is null;
            mixed |= _initial[i] is null;
        }

        MixedText.IsVisible = mixed;
        ShowStates(_initial, updateOctal: true);
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OctalBox.Focus();
            OctalBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Changement choisi, valable quand <see cref="DialogWindow.DialogResult"/> vaut <c>true</c>.</summary>
    public PermissionChange Change { get; private set; }

    /// <summary>Changement que donneraient les cases telles qu'elles sont (tests).</summary>
    internal PermissionChange CurrentChange => ChangeFromBoxes();

    /// <summary>Appliquer aussi au contenu des dossiers (sous-dossiers et fichiers).</summary>
    public bool Recursive => RecursivePanel.IsVisible && RecursiveBox.IsChecked == true;

    /// <summary>Dans le contenu, x seulement pour les dossiers et les fichiers déjà exécutables.</summary>
    public bool ExecuteOnlyIfAlready => SmartExecuteBox.IsChecked == true;

    /// <summary>
    /// Bits rwx cochés ou décochés : ajoutés ou retirés ; à l'état intermédiaire : inchangés. Les bits spéciaux ne font
    /// partie du changement que si leur case a été modifiée.
    /// </summary>
    private PermissionChange ChangeFromBoxes()
    {
        int set = 0, clear = 0;
        for (int i = 0; i < 12; i++)
        {
            var state = _boxes[i].IsChecked;
            if (i >= 9 && state == _initial[i])
            {
                continue;
            }

            if (state == true)
            {
                set |= BoxBits[i];
            }
            else if (state == false)
            {
                clear |= BoxBits[i];
            }
        }

        return new PermissionChange(set, clear);
    }

    private void ShowStates(IReadOnlyList<bool?> states, bool updateOctal)
    {
        _updating = true;
        for (int i = 0; i < 12; i++)
        {
            _boxes[i].IsChecked = states[i];
        }

        bool complete = states.All(s => s is not null);
        int mode = Enumerable.Range(0, 12).Where(i => states[i] == true).Sum(i => BoxBits[i]);
        if (updateOctal)
        {
            // Droits incomplets (cases intermédiaires) : pas de valeur octale, qui fixerait tout.
            OctalBox.Text = complete ? UnixPermissions.ToOctal(mode) : "";
        }

        var symbolic = UnixPermissions.ToSymbolic(mode).ToCharArray();
        for (int i = 0; i < 9; i++)
        {
            if (states[i] is null)
            {
                symbolic[i] = '?';
            }
        }

        SymbolicText.Text = new string(symbolic);
        ErrorText.IsVisible = false;
        _updating = false;
    }

    private bool?[] CurrentStates() => _boxes.Select(b => b.IsChecked).ToArray();

    private void OnBoxClick(object? sender, RoutedEventArgs e) => ShowStates(CurrentStates(), updateOctal: true);

    private void OnRecursiveClick(object? sender, RoutedEventArgs e)
    {
        SmartExecuteBox.IsEnabled = RecursiveBox.IsChecked == true;
        OkButton.Content = RecursiveBox.IsChecked == true ? Strings.PermissionsApplyRecursive : Strings.PermissionsApply;
    }

    private void OnOctalChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating && UnixPermissions.TryParseOctal(OctalBox.Text ?? "", out var mode))
        {
            // Valeur octale saisie : des droits complets, pour tous les éléments.
            ShowStates(BoxBits.Select(bit => (bool?)((mode & bit) != 0)).ToArray(), updateOctal: false);
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        bool complete = _boxes.All(b => b.IsChecked is not null);
        var octal = OctalBox.Text ?? "";
        if ((complete && !UnixPermissions.TryParseOctal(octal, out _)) || (!complete && octal.Trim().Length > 0))
        {
            ErrorText.Text = Strings.PermissionsInvalid;
            ErrorText.IsVisible = true;
            OctalBox.Focus();
            return;
        }

        Change = ChangeFromBoxes();
        DialogResult = true;
    }
}
