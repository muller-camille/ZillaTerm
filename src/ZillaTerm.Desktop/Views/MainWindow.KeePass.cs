using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.IO;
using System.Text;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Rdp;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Coffres KeePass dans « Mes serveurs » : accès d'urgence aux serveurs hors CyberArk (SSH et bureau à distance
/// directs), avec lecture et écriture des entrées. Chaque ouverture, connexion et modification est journalisée.
/// </summary>
public partial class MainWindow
{
    private readonly KeePassManager _keePass;
    private readonly SshInteraction _directUi;
    private readonly HashSet<string> _collapsedKeePass = new(StringComparer.Ordinal);
    private KeePassEntryNode? _currentKeePass;

    // ===================== Arbre =====================

    /// <summary>
    /// Coffres de « Mes serveurs ». Avec une recherche (<paramref name="filter"/>), seuls les coffres déverrouillés
    /// qui ont des entrées correspondantes restent, dépliés sur ces entrées.
    /// </summary>
    private List<object> KeePassNodes(string? filter = null)
    {
        var nodes = new List<object>();
        foreach (var folder in _settings.KeePassFolders)
        {
            var database = _keePass.Get(folder.Id)?.Database;
            if (filter is not null)
            {
                var matches = database?.Entries.Where(e => KeePassTarget.Matches(e, filter)).ToList() ?? [];
                if (matches.Count > 0)
                {
                    nodes.Add(new KeePassFolderNode(folder, database, KeePassChildren(folder, database!.Groups, matches, "", expandAll: true), true));
                }

                continue;
            }

            var children = database is null
                ? [new KeePassHintNode(folder, Strings.KeePassUnlockHint)]
                : KeePassChildren(folder, database.Groups, database.Entries, "");
            nodes.Add(new KeePassFolderNode(folder, database, children, !_collapsedKeePass.Contains(folder.Id)));
        }

        return nodes;
    }

    /// <summary>Sous-dossiers non vides puis entrées du dossier <paramref name="path"/> du coffre.</summary>
    private List<object> KeePassChildren(KeePassFolder folder, IReadOnlyList<string> groups, IReadOnlyList<KeePassEntry> entries, string path,
        bool expandAll = false)
    {
        var items = new List<object>();
        foreach (var group in groups.Where(g => g.Length > 0 && KeePassGroupPath.Parent(g) == path).Order(StringComparer.OrdinalIgnoreCase))
        {
            int count = entries.Count(e => KeePassGroupPath.IsWithin(e.Group, group));
            if (count > 0)
            {
                items.Add(new KeePassGroupNode(folder, group, KeePassChildren(folder, groups, entries, group, expandAll),
                    expandAll || !_collapsedKeePass.Contains($"{folder.Id}/{group}")) { Count = count });
            }
        }

        items.AddRange(entries.Where(e => e.Group == path).OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Select(e => new KeePassEntryNode(folder, e)));
        return items;
    }

    private void RememberKeePassExpansion(object node)
    {
        var (key, expanded, children) = node switch
        {
            KeePassFolderNode f => (f.Folder.Id, f.IsExpanded, f.Children),
            KeePassGroupNode g => ($"{g.Folder.Id}/{g.Path}", g.IsExpanded, g.Children),
            _ => (null, false, null),
        };
        if (key is null)
        {
            return;
        }

        if (expanded)
        {
            _collapsedKeePass.Remove(key);
        }
        else
        {
            _collapsedKeePass.Add(key);
        }

        foreach (var child in children!)
        {
            RememberKeePassExpansion(child);
        }
    }

    private KeePassFolder? SelectedKeePassFolder => SavedTree.SelectedItem switch
    {
        KeePassFolderNode f => f.Folder,
        KeePassGroupNode g => g.Folder,
        KeePassEntryNode e => e.Folder,
        KeePassHintNode h => h.Folder,
        _ => null,
    };

    /// <summary>Double-clic ou Entrée sur un élément KeePass ; vrai si l'élément en était un.</summary>
    private bool ActivateKeePassNode(object? node)
    {
        switch (node)
        {
            case KeePassEntryNode entry:
                _ = ConnectKeePassAsync(entry, null);
                return true;
            case KeePassHintNode hint:
                _ = UnlockKeePassAsync(hint.Folder);
                return true;
            case KeePassFolderNode { IsUnlocked: false } folder:
                _ = UnlockKeePassAsync(folder.Folder);
                return true;
            default:
                return node is KeePassFolderNode or KeePassGroupNode;
        }
    }

    // ===================== Ajout, déverrouillage, verrouillage =====================

    private async void OnAddKeePass(object? sender, RoutedEventArgs e)
    {
        var folder = new KeePassFolder();
        if (!await new KeePassFolderDialog(folder).ShowDialogAsync(this))
        {
            return;
        }

        _settings.KeePassFolders.Add(folder);
        SaveSettings();
        SideTabs.SelectedItem = CurrentTab;
        RefreshSaved();
        await UnlockKeePassAsync(folder);
    }

    /// <summary>
    /// Déverrouille le coffre : avec le mot de passe mémorisé dans le coffre local s'il y en a un, sinon (ou s'il est
    /// refusé) en le demandant. Vrai si le coffre est ouvert.
    /// </summary>
    private async Task<bool> UnlockKeePassAsync(KeePassFolder folder)
    {
        if (_keePass.IsOpen(folder.Id))
        {
            return true;
        }

        string? message = null;
        if (folder.RememberPassword && folder.UsesPassword)
        {
            if (!_keePass.Store.IsUnlocked && _keePass.Store.Exists && await PrepareLocalStoreProtectionAsync())
            {
                await new LocalStoreDialog(_keePass.Store, LocalStoreDialog.Mode.Unlock, SettingsDialog.RememberedFolderIds(_settings)).ShowDialogAsync(this);
            }

            if (_keePass.StoredPassword(folder) is { } stored)
            {
                SetStatus(Strings.KeePassUnlocking);
                try
                {
                    await _keePass.UnlockAsync(folder, stored, folder.KeyFilePath, _lifetime.Token);
                    OnKeePassUnlocked(folder);
                    return true;
                }
                catch (Exception ex) when (ex is KeePassException or IOException or UnauthorizedAccessException)
                {
                    // Mot de passe mémorisé périmé, fichier clé déplacé... : on le demande.
                    message = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    // Fenêtre fermée, ou session Windows verrouillée pendant l'ouverture : le coffre reste verrouillé.
                    SetStatus("");
                    return false;
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored);
                }
            }
        }

        var dialog = new KeePassUnlockDialog(folder, _keePass, EnsureLocalStoreAsync, message);
        if (!await dialog.ShowDialogAsync(this))
        {
            SetStatus("");
            return false;
        }

        SaveSettings();
        OnKeePassUnlocked(folder);
        if (dialog.NotRemembered)
        {
            SetStatus(Text.Format(Strings.KeePassNotRemembered, folder.DisplayName), isError: true);
        }

        return true;
    }

    private void OnKeePassUnlocked(KeePassFolder folder)
    {
        _collapsedKeePass.Remove(folder.Id);
        RefreshSaved();
        SetStatus(Text.Format(Strings.KeePassUnlocked, folder.DisplayName, _keePass.Get(folder.Id)?.Database.Entries.Count ?? 0));
    }

    /// <summary>Coffre local déverrouillé (ou créé à la demande) ; faux si l'utilisateur renonce.</summary>
    private async Task<bool> EnsureLocalStoreAsync()
    {
        var store = _keePass.Store;
        if (store.IsUnlocked)
        {
            return true;
        }

        if (!await PrepareLocalStoreProtectionAsync())
        {
            return false;
        }

        var owner = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
            .Windows.FirstOrDefault(w => w.IsActive) ?? this;
        var mode = store.Exists ? LocalStoreDialog.Mode.Unlock : LocalStoreDialog.Mode.Create;
        return await new LocalStoreDialog(store, mode, SettingsDialog.RememberedFolderIds(_settings)).ShowDialogAsync(owner);
    }

    /// <summary>
    /// Paramètres, page KeePass : créer, déverrouiller le coffre local ou changer son mot de passe (la protection du
    /// fichier est préparée d'abord, trousseau compris hors de Windows).
    /// </summary>
    private async Task ShowLocalStoreFromSettingsAsync(Window owner, SettingsDialog.StoreAction action)
    {
        if (!await PrepareLocalStoreProtectionAsync())
        {
            return;
        }

        var mode = action switch
        {
            SettingsDialog.StoreAction.Create => LocalStoreDialog.Mode.Create,
            SettingsDialog.StoreAction.Unlock => LocalStoreDialog.Mode.Unlock,
            _ => LocalStoreDialog.Mode.ChangePassword,
        };
        await new LocalStoreDialog(_keePass.Store, mode, SettingsDialog.RememberedFolderIds(_settings)).ShowDialogAsync(owner);
        RefreshSaved();
    }

    /// <summary>
    /// Hors de Windows, la protection du coffre local passe par le trousseau du bureau : il est ouvert (et déverrouillé
    /// au besoin, par sa propre fenêtre) avant de créer ou de déverrouiller le coffre, sans bloquer l'interface. Faux,
    /// avec la raison dans la barre d'état, si le trousseau manque ou refuse.
    /// </summary>
    private async Task<bool> PrepareLocalStoreProtectionAsync()
    {
        try
        {
            await Services.Secrets.SecretProtectors.PrepareAsync(_keePass.Protector, create: !_keePass.Store.Exists, _lifetime.Token);
            return true;
        }
        catch (Services.Secrets.SecretProtectorUnavailableException ex)
        {
            SetStatus(ex.Message, isError: true);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void OnKeePassFolderMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || SelectedKeePassFolder is not { } folder)
        {
            return;
        }

        bool open = _keePass.IsOpen(folder.Id);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsVisible = item.Tag switch
            {
                "unlock" => !open,
                "lock" or "open" => open,
                _ => true,
            };
        }
    }

    private async void OnKeePassUnlock(object? sender, RoutedEventArgs e)
    {
        if (SelectedKeePassFolder is { } folder)
        {
            await UnlockKeePassAsync(folder);
        }
    }

    private void OnKeePassLock(object? sender, RoutedEventArgs e)
    {
        if (SelectedKeePassFolder is { } folder)
        {
            _keePass.Lock(folder.Id);
            SetStatus(Text.Format(Strings.KeePassLockedStatus, folder.DisplayName));
        }
    }

    private async void OnKeePassReload(object? sender, RoutedEventArgs e)
    {
        if (SelectedKeePassFolder is { } folder && _keePass.Get(folder.Id) is { } vault)
        {
            try
            {
                await vault.ReloadAsync(_lifetime.Token);
                RefreshSaved();
                SetStatus(Text.Format(Strings.KeePassReloaded, folder.DisplayName));
            }
            catch (ObjectDisposedException)
            {
                // Coffre verrouillé entre-temps (Windows verrouillé) : l'arbre le montre verrouillé.
                RefreshSaved();
            }
            catch (Exception ex) when (ex is KeePassException or IOException or UnauthorizedAccessException)
            {
                SetStatus(ex.Message, isError: true);
            }
        }
    }

    private async void OnKeePassProperties(object? sender, RoutedEventArgs e)
    {
        if (SelectedKeePassFolder is not { } folder)
        {
            return;
        }

        var before = (folder.FilePath, folder.KeyFilePath, folder.UsesPassword);
        if (await new KeePassFolderDialog(folder).ShowDialogAsync(this))
        {
            if (before != (folder.FilePath, folder.KeyFilePath, folder.UsesPassword))
            {
                // Autre fichier ou autre clé : le coffre ouvert ne correspond plus, ni le mot de passe mémorisé (oublié
                // tout de suite si le coffre local est ouvert, sinon à son prochain déverrouillage).
                _keePass.Lock(folder.Id);
                if (folder.RememberPassword)
                {
                    folder.RememberPassword = false;
                    if (_keePass.Store.IsUnlocked)
                    {
                        _keePass.Store.Remove(folder.Id);
                    }
                }
            }

            SaveSettings();
            RefreshSaved();
        }
    }

    private async void OnKeePassRemove(object? sender, RoutedEventArgs e)
    {
        if (SelectedKeePassFolder is not { } folder
            || !await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
            {
                Title = Strings.KeePassFolderTitle,
                Heading = Text.Format(Strings.KeePassRemoveHeading, folder.DisplayName),
                Message = Strings.KeePassRemoveMessage,
                Actions = [Strings.ActionRemove],
            }))
        {
            return;
        }

        _keePass.Lock(folder.Id);
        // Coffre local verrouillé : le mot de passe mémorisé sera oublié à son prochain déverrouillage.
        if (_keePass.Store.IsUnlocked)
        {
            _keePass.Store.Remove(folder.Id);
        }

        _settings.KeePassFolders.Remove(folder);
        SaveSettings();
        RefreshSaved();
    }

    // ===================== Entrées =====================

    private async void OnKeePassNewEntry(object? sender, RoutedEventArgs e)
    {
        if (SelectedKeePassFolder is not { } folder || _keePass.Get(folder.Id) is not { } vault)
        {
            return;
        }

        var group = SavedTree.SelectedItem switch
        {
            KeePassGroupNode g => g.Path,
            KeePassEntryNode n => n.Entry.Group,
            _ => "",
        };
        var dialog = new KeePassEntryDialog(folder.DisplayName, vault.Database.Groups, null, group);
        if (await dialog.ShowDialogAsync(this) && dialog.Result is { } data)
        {
            await SaveKeePassAsync(folder, db => db.AddEntry(data, dialog.Group), "entry-add", data.Title);
        }
    }

    private async void OnKeePassEditEntry(object? sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is KeePassEntryNode node)
        {
            await EditKeePassEntryAsync(node);
        }
    }

    private async Task EditKeePassEntryAsync(KeePassEntryNode node)
    {
        if (_keePass.Get(node.Folder.Id) is not { } vault)
        {
            return;
        }

        var entry = node.Entry;
        var dialog = new KeePassEntryDialog(node.Folder.DisplayName, vault.Database.Groups, entry, entry.Group);
        if (await dialog.ShowDialogAsync(this) && dialog.Result is { } data)
        {
            bool unchanged = data.Password is null && data.Title == entry.Title && data.UserName == entry.UserName
                             && data.Url == entry.Url && data.Notes == entry.Notes && dialog.Group == entry.Group;
            if (unchanged)
            {
                // Rien de modifié : ni enregistrement ni nouvelle version dans l'historique.
                return;
            }

            await SaveKeePassAsync(node.Folder, db =>
            {
                db.UpdateEntry(entry.Id, data, entry.Modified);
                if (!string.Equals(dialog.Group, entry.Group, StringComparison.Ordinal))
                {
                    db.MoveEntry(entry.Id, dialog.Group);
                }
            }, "entry-edit", data.Title);
        }
    }

    private async void OnKeePassDeleteEntry(object? sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is KeePassEntryNode node)
        {
            await DeleteKeePassEntryAsync(node);
        }
    }

    private async Task DeleteKeePassEntryAsync(KeePassEntryNode node)
    {
        if (await ConfirmDialog.DestructiveAsync(this, Strings.KeePassFolderTitle, Text.Format(Strings.KeePassDeleteEntryHeading, node.Title),
                Strings.ActionDelete, subject: node.Folder.DisplayName, message: Strings.KeePassDeleteEntryMessage))
        {
            await SaveKeePassAsync(node.Folder, db => db.DeleteEntry(node.Entry.Id, node.Entry.Modified), "entry-delete", node.Entry.Title);
        }
    }

    /// <summary>Modifie le coffre et l'enregistre (relecture, vérification, copie .bak, remplacement atomique).</summary>
    private async Task SaveKeePassAsync(KeePassFolder folder, Action<KeePassDatabase> change, string action, string entryTitle)
    {
        if (_keePass.Get(folder.Id) is not { } vault)
        {
            return;
        }

        SetStatus(Text.Format(Strings.KeePassSaving, folder.DisplayName));
        try
        {
            await vault.SaveAsync(change, _lifetime.Token);
        }
        catch (ObjectDisposedException)
        {
            // Coffre verrouillé (Windows verrouillé, déconnexion) avant le début de l'enregistrement : rien n'est écrit.
            SetStatus(Text.Format(Strings.KeePassLockedStatus, folder.DisplayName), isError: true);
            RefreshSaved();
            return;
        }
        catch (KeePassException ex) when (ex.Kind == KeePassError.Conflict)
        {
            SetStatus(ex.Message, isError: true);
            await ConfirmDialog.ShowMessageAsync(this, ex.Message, ConfirmKind.Warning, title: Strings.KeePassFolderTitle);
            try
            {
                await vault.ReloadAsync(_lifetime.Token);
            }
            catch (Exception reload) when (reload is KeePassException or IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
            }

            RefreshSaved();
            return;
        }
        catch (Exception ex) when (ex is KeePassException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(ex.Message, isError: true);
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.KeePassSaveFailed, folder.DisplayName, ex.Message), ConfirmKind.Danger, title: Strings.KeePassFolderTitle);
            return;
        }

        // Le coffre est enregistré : un journal momentanément inaccessible ne doit pas le faire passer pour un échec
        // (on recommencerait, et l'entrée serait ajoutée deux fois).
        RefreshSaved();
        bool logged = _keePass.Log.TryWrite(action, ("vault", folder.FilePath), ("entry", entryTitle));
        SetStatus(logged
                ? Text.Format(Strings.KeePassSaved, folder.DisplayName)
                : Text.Format(Strings.KeePassSavedNoLog, folder.DisplayName),
            isError: !logged);
    }

    // ===================== Connexions directes =====================

    private void OnKeePassConnect(object? sender, RoutedEventArgs e) => ConnectSelectedKeePass(null);

    private void OnKeePassConnectSsh(object? sender, RoutedEventArgs e) => ConnectSelectedKeePass(RemoteProtocol.Ssh);

    private void OnKeePassConnectRdp(object? sender, RoutedEventArgs e) => ConnectSelectedKeePass(RemoteProtocol.Rdp);

    private void OnKeePassConnectVnc(object? sender, RoutedEventArgs e) => ConnectSelectedKeePass(RemoteProtocol.Vnc);

    /// <summary>Fichiers de l'entrée : son protocole de fichiers (FTP, FTPS…) s'il en a un, sinon SFTP.</summary>
    private void OnKeePassOpenFiles(object? sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is KeePassEntryNode node)
        {
            ConnectSelectedKeePass(KeePassTarget.IsFileTransfer(node.Target.Protocol) ? node.Target.Protocol : RemoteProtocol.Sftp);
        }
    }

    private void ConnectSelectedKeePass(RemoteProtocol? protocol)
    {
        if (SavedTree.SelectedItem is KeePassEntryNode node)
        {
            _ = ConnectKeePassAsync(node, protocol);
        }
    }

    /// <summary>
    /// Connexion directe au serveur d'une entrée KeePass : SSH (terminal + Fichiers) ou bureau à distance, avec le mot
    /// de passe de l'entrée lu au moment de la connexion. Protocole inconnu : l'utilisateur choisit.
    /// </summary>
    private async Task ConnectKeePassAsync(KeePassEntryNode node, RemoteProtocol? protocol)
    {
        var target = protocol is { } chosen ? node.Target.WithProtocol(chosen) : node.Target;
        if (target.Host.Length == 0)
        {
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.KeePassNoHostError, node.Title), ConfirmKind.Warning, title: Strings.KeePassFolderTitle);
            return;
        }

        // Un retour à la ligne dans l'hôte ou l'utilisateur ajouterait un réglage au fichier .rdp donné à mstsc
        // (partage des disques…) : une entrée qui en contient est refusée, quel que soit le protocole.
        if (target.Host.Any(char.IsControl) || target.UserName.Any(char.IsControl))
        {
            await ConfirmDialog.ShowMessageAsync(this, Strings.KeePassTargetInvalid, ConfirmKind.Warning, title: Strings.KeePassFolderTitle);
            return;
        }

        if (target.Protocol == RemoteProtocol.Unknown)
        {
            AskProtocol(node);
            return;
        }

        if (target.Protocol is RemoteProtocol.Ssh or RemoteProtocol.Sftp && target.UserName.Length == 0)
        {
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.KeePassNoUserError, node.Title), ConfirmKind.Warning, title: Strings.KeePassFolderTitle);
            return;
        }

        var folder = node.Folder;
        if (!await UnlockKeePassAsync(folder))
        {
            return;
        }

        var entryId = node.Entry.Id;
        var entryTitle = node.Entry.Title;
        int reveals = 0;

        // Lu à chaque connexion (terminal, puis SFTP et SCP de l'onglet Fichiers, reconnexions) ; rien si le coffre a été
        // verrouillé. La première lecture suit la ligne du journal écrite ci-dessous ; chaque lecture suivante est notée
        // à son tour, et sans journal, pas de mot de passe.
        string? Password()
        {
            if (Interlocked.Increment(ref reveals) > 1
                && !_keePass.Log.TryWrite("password-reuse", ("vault", folder.FilePath), ("entry", entryTitle),
                    ("user", target.UserName), ("target", target.Address)))
            {
                return null;
            }

            return _keePass.Get(folder.Id)?.RevealPassword(entryId);
        }

        var label = Text.Format(Strings.KeePassTabLabel, node.Title);
        try
        {
            _keePass.Log.Write(KeePassTarget.Name(target.Protocol).ToLowerInvariant(), ("vault", folder.FilePath), ("entry", entryTitle),
                ("user", target.UserName), ("target", target.Address));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(Text.Format(Strings.KeePassConnectNoLog, ex.Message), isError: true);
            return;
        }

        SetStatus(Text.Format(Strings.KeePassConnecting, node.Title, target.Address));
        try
        {
            if (target.Protocol == RemoteProtocol.Vnc)
            {
                OpenVncTab(label, target.Host, target.Port, Password, () => ConnectKeePassAsync(node, target.Protocol));
            }
            else if (KeePassTarget.IsFileTransfer(target.Protocol))
            {
                OpenKeePassFiles(target, label, Password, () => ConnectKeePassAsync(node, target.Protocol));
            }
            else if (target.Protocol == RemoteProtocol.Ssh)
            {
                var connector = new Core.Ssh.SshConnector(target.Host, target.Port, target.UserName, _directUi.For(label),
                    password: Password, addressWhat: CoreStrings.ServerAddressWhat);
                var session = new SshSession(null, label, connector, Dispatcher.UIThread, _settings.FollowTerminalFolder, null);
                ShowSshTab(session, $"{target.UserName}@{target.Address}", Strings.ConnectingDirect, "IconKeePass",
                    Text.Format(Strings.KeePassSshOpened, node.Title, target.Address), () => ConnectKeePassAsync(node, target.Protocol));
            }
            else if (FreeRdpClient.IsAvailable || RdpClientHost.IsAvailable)
            {
                var settings = RdpConnectionSettings.Direct(target.Host, target.Port, target.UserName);
                await OpenRdpTabAsync(label, async ct =>
                {
                    // Certificat du serveur vérifié (épinglé au premier usage) avant que le mot de passe soit lu ; à chaque
                    // connexion, reconnexions comprises.
                    await CheckRdpCertificateAsync(target.Address, settings, ct);
                    return DirectRdpRequest(target.Address, settings, Password(), FreeRdpClient.IsAvailable);
                }, duplicate: () => ConnectKeePassAsync(node, target.Protocol));
            }
            else
            {
                // Sans client Bureau à distance dans ZillaTerm, mstsc demandera le mot de passe : il n'est écrit nulle part.
                // Pas de cache d'images sur le disque (l'écran d'une session d'administration n'y reste pas), comme dans l'onglet.
                var address = target.Host.Contains(':') ? $"[{target.Host}]:{target.Port}" : $"{target.Host}:{target.Port}";
                var file = $"full address:s:{address}\r\nusername:s:{target.UserName}\r\nprompt for credentials:i:1\r\n"
                           + "bitmapcachepersistenable:i:0\r\n";
                _launcher.LaunchRdp([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(file)], label);
                SetStatus(Text.Format(Strings.KeePassRdpNoControl, node.Title));
                InterventionRecord(Core.Interventions.InterventionKind.Connection, label, Text.Format(Strings.InterventionRdpExternal, address));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(Text.Format(Strings.ConnectFailed, ErrorText.Describe(ex)), isError: true);
        }
    }

    /// <summary>Protocole non précisé dans l'entrée : petit menu SSH / bureau à distance sous la souris.</summary>
    private void AskProtocol(KeePassEntryNode node)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        menu.Items.Add(MenuEntry(Strings.MenuKeePassSsh.Replace("_", ""), () => _ = ConnectKeePassAsync(node, RemoteProtocol.Ssh)));
        menu.Items.Add(MenuEntry(Strings.MenuKeePassRdp.Replace("_", ""), () => _ = ConnectKeePassAsync(node, RemoteProtocol.Rdp)));
        menu.Items.Add(MenuEntry(Strings.MenuKeePassVnc.Replace("_", ""), () => _ = ConnectKeePassAsync(node, RemoteProtocol.Vnc)));
        menu.Items.Add(MenuEntry(Strings.MenuKeePassSftp.Replace("_", ""), () => _ = ConnectKeePassAsync(node, RemoteProtocol.Sftp)));
        menu.Items.Add(MenuEntry(Strings.MenuKeePassFtp.Replace("_", ""), () => _ = ConnectKeePassAsync(node, RemoteProtocol.Ftp)));
        // Ouvert après le double-clic : sinon le relâchement du bouton le refermerait aussitôt.
        Dispatcher.UIThread.Post(() => menu.Open(SavedTree), DispatcherPriority.ApplicationIdle);
    }

    // ===================== Verrouillage automatique =====================

    /// <summary>
    /// Session du poste verrouillée (voir <see cref="SessionLock"/>, levé sur le fil de l'interface) : coffres KeePass et
    /// coffre local verrouillés, presse-papiers vidé, maintien de la session PVWA suspendu.
    /// </summary>
    private void OnWorkstationLocked()
    {
        _windowsLocked = true;
        ClearPasswordClipboard();
        // Mots de passe du PSMP gardés pour les connexions suivantes et clé MFA : oubliés, redemandés au besoin.
        SshAnswerCache.ForgetAll();
        _mfaKey = null;
        if (_settings.KeePassFolders.Any(f => _keePass.IsOpen(f.Id)) || _keePass.Store.IsUnlocked)
        {
            _keePass.LockAll(localStore: true);
            SetStatus(Strings.KeePassAllLocked);
        }
    }

    /// <summary>Session du poste déverrouillée : le maintien de la session PVWA reprend.</summary>
    private void OnWorkstationUnlocked() => _windowsLocked = false;

    private void OnKeePassChanged() => Dispatcher.UIThread.Post(RefreshSaved);
}
