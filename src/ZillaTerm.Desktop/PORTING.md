# Portage de ZillaTerm vers Avalonia

`ZillaTerm.Desktop` est l'interface Avalonia de ZillaTerm (Windows, Linux, macOS). Elle est portée fenêtre par fenêtre
depuis `ZillaTerm.App` (WPF), qui reste l'application publiée pour Windows tant que le portage n'est pas complet.

## Organisation

- Mêmes espaces de noms que `ZillaTerm.App` (`RootNamespace` = `ZillaTerm.App`) : une fenêtre portée garde son nom et
  son espace de noms (`Views/LoginWindow.axaml` remplace `Views/LoginWindow.xaml`).
- Code sans interface de `ZillaTerm.App` : lié depuis le `.csproj` (`<Compile Include="..\ZillaTerm.App\…" Link="Shared\…" />`),
  jamais copié. Un fichier qui n'utilise ni WPF ni Windows se lie tel quel ; sinon il est porté dans ce projet.
- Textes : `Strings*.resx` de `ZillaTerm.App`, liés (mêmes clés, mêmes traductions). Un texte nouveau s'ajoute dans
  les trois `.resx` et dans `Strings.Designer.cs` de `ZillaTerm.App`.
- Thème : `Themes/Palette.axaml` (couleurs et icônes, clair et sombre, mêmes clés que `Theme.xaml`),
  `Themes/Controls.axaml` (thèmes des contrôles à modèle) et `Themes/Styles.axaml` (classes des éléments sans modèle).

## Correspondances WPF → Avalonia

| WPF | Avalonia |
| --- | --- |
| `Style="{StaticResource ToolButton}"` (Button, TabControl, TabItem, TextBox) | `Theme="{StaticResource ToolButton}"` |
| `Style="{StaticResource SectionTitle}"` (TextBlock, Border, Image, Rectangle) | `Classes="SectionTitle"` |
| `Foreground="{DynamicResource MutedBrush}"` | inchangé, ou `Classes="Muted"` |
| `Visibility="Collapsed"` / `Visibility.Visible` | `IsVisible="False"` / `IsVisible = true` |
| `ToolTip="…"` | `ToolTip.Tip="…"` |
| `ToolTipService.ShowOnDisabled` | `ToolTip.ShowOnDisabled` |
| `Target="{Binding ElementName=X}"` | `Target="{Binding #X}"` |
| `PasswordBox` | `views:PasswordBox` (`SecretInput.Read(box, clear: true)`) |
| `ResizeMode="NoResize"` | `CanResize="False"` |
| `Window` de dialogue, `DialogResult`, `IsCancel` | `views:DialogWindow` (même `DialogResult`, « Annuler » ferme) |
| `dialog.ShowDialog() == true` | `await dialog.ShowDialogAsync(owner)` |
| `MessageBox.Show(…)` | `await ConfirmDialog.ShowMessageAsync(owner, texte, ConfirmKind…)` |
| `ConfirmDialog.Ask/Confirm/Destructive` | `AskAsync/ConfirmAsync/DestructiveAsync` |
| `OpenFileDialog`, `SaveFileDialog`, `OpenFolderDialog` | `Pickers.OpenFileAsync/SaveFileAsync/OpenFolderAsync` (filtres WPF acceptés) |
| `Dispatcher.BeginInvoke(…)` / `Dispatcher.Invoke(…)` | `Dispatcher.UIThread.Post(…)` / `Dispatcher.UIThread.Invoke(…)` |
| `Clipboard.SetText(…)` | `await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(…)` |
| `Loaded` (premier affichage d'une fenêtre) | `Opened` |
| `FindResource("X")` | `this.TryFindResource("X", out var value)` |
| `SetResourceReference(prop, "X")` | `control[!prop] = control.GetResourceObservable("X").ToBinding()` |
| `ListView` + `GridView` | `DataGrid` (`Avalonia.Controls.DataGrid`, thème commun `Themes/DataGrid.axaml` chargé par App.axaml ; `Classes="Records"` pour un historique en lecture seule ; colonnes liées en `Mode=OneWay` : le DataGrid lie dans les deux sens par défaut) ou `ListBox` à modèle en colonnes |
| `x:Name` | `x:Name` (champ généré, comme en WPF) |
| `Window.GetWindow(control)` | `TopLevel.GetTopLevel(control) as Window` |
| `ActualWidth` / `ActualHeight` | `Bounds.Width` / `Bounds.Height` |
| `window.Left/Top = …` | `ScreenPlacement.OpenBeside(window, owner)` ou `ScreenPlacement.OpenAt(window, point, dx, dy)` |
| `IsLoaded` (fenêtre) | indicateur posé dans `Opened` (`_opened` de la fenêtre principale) |
| `MouseDown` + `MouseButton.Middle` | `PointerReleased` + `e.InitialPressMouseButton == MouseButton.Middle` |
| `MenuItem.InputGestureText = "Ctrl+Maj+F"` | `Header = MenuHeader.WithGesture(texte, raccourci)` (texte traduit gardé) |
| `menu.PlacementTarget = x; menu.IsOpen = true` | `menu.Open(x)` |
| Rappel synchrone d'une bibliothèque qui pose une question (`Dispatcher.Invoke(() => dialog.ShowDialog())`) | `UiWait.Run(() => QuestionAsync(…))` |
| `InputManager.Current.MostRecentInputDevice` | indicateur tenu par la fenêtre (gestionnaires `KeyDown` / `PointerPressed` en tunnel) |
| `Process.Start("explorer.exe", …)` (WindowsExplorer) | `FileManager.ShowFile` / `FileManager.OpenFolder` (Explorateur, Finder, gestionnaire du bureau) |

Les modèles de données (`DataTemplate`) déclarent leur type (`x:DataType`) : les liaisons sont compilées.

Les questions deviennent asynchrones : une méthode qui en posait une (`bool ConfirmX()`) devient `Task<bool>
ConfirmXAsync()`, et ses appelants attendent la réponse. Après un `await`, l'état a pu changer (onglet fermé entre-temps,
déconnexion) : il est revérifié avant d'agir.

La langue de l'interface (`CultureInfo.CurrentUICulture`) suit le contexte d'exécution : changée dans une méthode `async`,
elle est défaite dès que la méthode rend la main. Elle est donc posée aussi par une opération synchrone du fil de
l'interface (`App.ApplyLanguage`), qu'Avalonia garde pour la suite.

Un élément qui était dans le XAML WPF et que le code porté crée lui-même (panneaux « Fichiers » et « Intervention » de
la fenêtre principale) est créé dès `InitializeComponent`, avant le premier code qui s'en sert.

## Règles

- Aucune fonction ne fait semblant d'exister : ce qui n'est pas encore porté n'apparaît pas, ou dit qu'il ne l'est pas.
- Secrets : jamais écrits sur disque en clair ; un champ de mot de passe est vidé dès que le mot de passe a servi
  (`SecretInput.Read(box, clear: true)`). Sous Linux et macOS, la protection des secrets (DPAPI sous Windows) passe par
  le trousseau du système ; sans trousseau, les fonctions qui en ont besoin restent indisponibles.
- Même comportement que la version WPF (textes, touches d'accès, ordre de tabulation, confirmations, journal de
  débogage) : le code porté reprend les commentaires d'origine.

## Vérifier

```sh
dotnet build src/ZillaTerm.Desktop
# Sous Linux sans écran : Xvfb :99 &, puis
DISPLAY=:99 dotnet src/ZillaTerm.Desktop/bin/Debug/net10.0/ZillaTerm.dll
```
