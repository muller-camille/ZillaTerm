using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.Migration;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Les fenêtres s'ouvrent avec le thème de l'application : une erreur au chargement du XAML (ressource manquante,
/// gestionnaire appelé avant que ses champs existent) les ferait planter chez l'utilisateur.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed partial class DialogTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AccountWindowsOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var create = new AccountDialog(["Prod"], ["WinDomain"], "Prod", "WinDomain", "CORP", (_, _) => Task.FromResult(new PvwaAccount()));
            Assert.Equal(("Prod", "CORP"), (create.SafeBox.Text, create.DomainBox.Text));
            Assert.True(create.CpmBox.IsChecked);
            Assert.False(create.ReasonBox.IsEnabled);
            create.CpmBox.IsChecked = false;
            Assert.True(create.ReasonBox.IsEnabled);
            create.Close();

            var account = JsonSerializer.Deserialize<PvwaAccount>("""
                {"id":"1","name":"Op-srv01","address":"srv01","userName":"svc","platformId":"WinDomain","safeName":"Prod",
                 "secretManagement":{"automaticManagementEnabled":false,"manualManagementReason":"Compte applicatif"}}
                """, Web)!;
            var edit = new AccountDialog(account, ["WinDomain"], (_, _) => Task.FromResult(account));
            Assert.False(edit.SafeBox.IsEnabled);
            Assert.Equal(Visibility.Collapsed, edit.PasswordBox.Visibility);
            Assert.Equal(("srv01", "Compte applicatif"), (edit.AddressBox.Text, edit.ReasonBox.Text));
            Assert.True(edit.ReasonBox.IsEnabled);
            edit.Close();
        });
    }

    [Fact]
    public void PasswordAndSafeMembersWindowsOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var retrieve = new RetrievePasswordDialog("svc@srv01", TimeSpan.FromSeconds(20), "Incident 42",
                (_, _) => Task.FromResult(Array.Empty<char>()), _ => true);
            Assert.Equal("Incident 42", retrieve.ReasonBox.Text);
            retrieve.Close();

            new SafeMembersDialog("Prod", _ => Task.FromResult(new List<SafeMember>())).Close();
        });
    }

    /// <summary>
    /// Confirmations : boutons au verbe explicite, « Annuler » par défaut ; une action irréversible est rouge et reste
    /// grisée tant que la case « J'ai vérifié… » n'est pas cochée.
    /// </summary>
    [Fact]
    public void ConfirmationsDefaultToCancel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var changed = new ConfirmDialog(new ConfirmRequest
            {
                Title = Strings.HostKeyTitle,
                Banner = Strings.HostKeyChangedBanner,
                Heading = Strings.HostKeyChangedPsmpHeading,
                Subject = "psmp.corp:22",
                Codes = [("old", "SHA256:AAA"), ("new", "SHA256:BBB")],
                Kind = ConfirmKind.Danger,
                Actions = [Strings.HostKeyReplace],
                DangerAction = 0,
                Acknowledge = Strings.HostKeyAckPsmp,
            });
            var buttons = changed.ButtonsPanel.Children.OfType<System.Windows.Controls.Button>().ToList();
            Assert.Equal([Strings.HostKeyReplace, Strings.Cancel], buttons.Select(b => (string)b.Content));
            Assert.False(buttons[0].IsEnabled);
            Assert.False(buttons[0].IsDefault);
            Assert.True(buttons[1].IsDefault && buttons[1].IsCancel);
            Assert.Equal(Visibility.Visible, changed.Banner.Visibility);
            changed.AcknowledgeBox.IsChecked = true;
            Assert.True(buttons[0].IsEnabled);
            Assert.Equal(2, changed.CodeList.Items.Count);
            changed.Close();

            // Renvoi d'un fichier enregistré dans l'éditeur : l'action attendue est le bouton par défaut.
            var upload = new ConfirmDialog(new ConfirmRequest
            {
                Title = "t",
                Heading = "h",
                Items = ["a", "b"],
                Actions = [Strings.EditUploadAction],
                DefaultAction = 0,
                CancelLabel = Strings.NotNow,
            });
            var choices = upload.ButtonsPanel.Children.OfType<System.Windows.Controls.Button>().ToList();
            Assert.True(choices[0].IsDefault);
            Assert.False(choices[1].IsDefault);
            Assert.Equal(Strings.NotNow, choices[1].Content);
            Assert.Equal(Visibility.Visible, upload.ItemsPanel.Visibility);
            upload.Close();
        });
    }

    /// <summary>
    /// Clé d'hôte d'un type jamais vu pour un PSMP connu : ni l'alerte rouge de clé changée, ni la simple question du
    /// premier usage ; les clés déjà acceptées sont montrées, et rien ne s'accepte sans cocher « J'ai vérifié… ».
    /// </summary>
    [Fact]
    public void HostKeyOfANewTypeAsksForACheckedFingerprint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var store = new Dictionary<string, string>();
            KnownHosts.Remember(store, "psmp.corp.local", 22, "rsa-sha2-512", "RSA1");
            var status = KnownHosts.Check(store, "psmp.corp.local", 22, "ssh-ed25519", "ED1");
            Assert.Equal(HostKeyStatus.NewAlgorithm, status);

            var dialog = new ConfirmDialog(Services.SshInteraction.HostKeyQuestion(false, "psmp.corp.local", 22, "ssh-ed25519", "ED1", status,
                KnownHosts.KnownKeys(store, "psmp.corp.local", 22)));
            Assert.Equal(Strings.HostKeyNewTypePsmpHeading, dialog.HeadingText.Text);
            Assert.NotEqual(Visibility.Visible, dialog.Banner.Visibility);
            Assert.Equal(2, dialog.CodeList.Items.Count);
            var buttons = dialog.ButtonsPanel.Children.OfType<System.Windows.Controls.Button>().ToList();
            Assert.Equal([Strings.HostKeyAddKey, Strings.HostKeyCancel], buttons.Select(b => (string)b.Content));
            Assert.False(buttons[0].IsEnabled);
            Assert.True(buttons[1].IsDefault);
            dialog.AcknowledgeBox.IsChecked = true;
            Assert.True(buttons[0].IsEnabled);
            dialog.Close();

            KnownHosts.Remember(store, "psmp.corp.local", 22, "ssh-ed25519", "ED1");
            status = KnownHosts.Check(store, "psmp.corp.local", 22, "ssh-ed25519", "ED2");
            var changed = new ConfirmDialog(Services.SshInteraction.HostKeyQuestion(true, "srv01.corp.local", 22, "ssh-ed25519", "ED2", status,
                [KnownHosts.Known(store, "psmp.corp.local", 22, "ssh-ed25519")!.Value]));
            Assert.Equal(Strings.HostKeyChangedServerHeading, changed.HeadingText.Text);
            Assert.Equal(Visibility.Visible, changed.Banner.Visibility);
            changed.Close();
        });
    }

    /// <summary>Droits de plusieurs fichiers (755 et 644) : seuls les droits changés partent, pas ceux du premier.</summary>
    [Fact]
    public void PermissionsOfMixedItemsOnlyChangeWhatIsTicked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var dialog = new PermissionsDialog("2", "root@srv:/opt", [0b111_101_101, 0b110_100_100], hasDirectory: false);
            var boxes = dialog.BitsGrid.Children.OfType<System.Windows.Controls.CheckBox>().ToList();
            Assert.Equal(9, boxes.Count);
            // x du propriétaire, du groupe et des autres : différents d'un fichier à l'autre.
            Assert.All(new[] { 2, 5, 8 }, i => Assert.Null(boxes[i].IsChecked));
            Assert.Equal(Visibility.Visible, dialog.MixedText.Visibility);
            Assert.Equal("", dialog.OctalBox.Text);
            Assert.Equal("rw?r-?r-?", dialog.SymbolicText.Text);

            boxes[4].IsChecked = true;
            var change = dialog.CurrentChange;
            Assert.Equal(0b111_111_101, change.Apply(0b111_101_101));
            Assert.Equal(0b110_110_100, change.Apply(0b110_100_100));
            Assert.Equal(Strings.PermissionsApply, dialog.OkButton.Content);
            dialog.Close();
        });
    }

    [Fact]
    public void SafeMemberWindowsOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var add = new SafeMemberDialog("Prod", (_, _) => Task.CompletedTask);
            // Nouveau membre : se connecter sans voir les mots de passe.
            Assert.Equal(SafeProfile.AccountUser, add.ProfileBox.SelectedValue);
            add.ProfileBox.SelectedValue = SafeProfile.ReadOnly;
            Assert.Equal(SafeProfile.ReadOnly, add.ProfileBox.SelectedValue);
            Assert.Equal(Visibility.Visible, add.SearchInBox.Visibility);
            Assert.Equal("Vault", add.SearchInBox.Text);
            Assert.Equal((2, 3), (add.LeftGroups.Children.Count, add.RightGroups.Children.Count));
            add.Close();

            var member = JsonSerializer.Deserialize<SafeMember>("""
                {"memberName":"Unix Admins","memberType":"Group","membershipExpirationDate":1767225600,
                 "permissions":{"listAccounts":true,"addAccounts":true}}
                """, Web)!;
            var edit = new SafeMemberDialog("Prod", member, (_, _) => Task.CompletedTask);
            Assert.Equal(SafeProfile.Custom, edit.ProfileBox.SelectedValue);
            Assert.Equal(Visibility.Collapsed, edit.ChangesText.Visibility);
            edit.ProfileBox.SelectedValue = SafeProfile.ReadOnly;
            // Lister gardé, ajouter retiré, audit et membres ajoutés.
            Assert.Equal(Text.Format(Strings.SafeMemberChanges, 2, 1), edit.ChangesText.Text);
            Assert.True(edit.NameBox.IsReadOnly);
            Assert.False(edit.TypeBox.IsEnabled);
            Assert.Equal(1, edit.TypeBox.SelectedIndex);
            Assert.Equal(Visibility.Collapsed, edit.SearchInBox.Visibility);
            Assert.NotNull(edit.UntilBox.SelectedDate);
            edit.Close();

            var actions = new SafeMemberActions((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
            new SafeMembersDialog("Prod", _ => Task.FromResult(new List<SafeMember>()), actions).Close();

            // Deux chargements qui se croisent : boutons grisés pendant le chargement, et le plus récent l'emporte même
            // si l'ancien répond après lui.
            UseDispatcherContext();
            var loads = new Queue<TaskCompletionSource<List<SafeMember>>>([new(), new()]);
            var pending = loads.ToArray();
            var members = new SafeMembersDialog("Prod", _ => loads.Dequeue().Task, actions);
            var first = members.LoadAsync();
            Assert.False(members.ManagePanel.IsEnabled);
            var second = members.LoadAsync();
            pending[1].SetResult([new SafeMember { MemberName = "jdoe" }]);
            Pump(second);
            Assert.True(members.ManagePanel.IsEnabled);
            pending[0].SetResult([new SafeMember { MemberName = "ancien" }]);
            Pump(first);
            Assert.Equal(["jdoe"], Assert.IsAssignableFrom<IEnumerable<SafeMembersDialog.SafeMemberRow>>(members.MembersGrid.ItemsSource).Select(r => r.Name));
            Assert.True(members.ManagePanel.IsEnabled);
            members.Close();
        });
    }

    [Fact]
    public void ImportWindowOpens()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var choose = new ImportAccountsDialog(["Prod"], ["WinDomain"], "Prod", "WinDomain");
            Assert.Equal(("Prod", "WinDomain"), (choose.SafeBox.Text, choose.PlatformBox.Text));
            Assert.False(choose.ImportButton.IsEnabled);
            choose.Close();
            Assert.Null(choose.Confirmed);
        });
    }

    /// <summary>
    /// Import sur un PVWA fictif : une ligne créée, une refusée, une incomplète, puis la session expire ; les lignes
    /// suivantes ne sont pas envoyées, les mots de passe sont effacés et le résultat CSV ne les contient pas.
    /// </summary>
    [Fact]
    public void ImportShowsEachLineAndSavesTheResult()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string csv = "safe,platform,address,userName,password\n"
            + "Prod,WinDomain,srv01,adm1,Secret-1\n"
            + "Prod,WinDomain,srv02,dup,\n"
            + "Prod,WinDomain,,adm3,\n"
            + "Prod,WinDomain,srv04,expire,Secret-4\n"
            + "Prod,WinDomain,srv05,adm5,Secret-5\n";
        var import = AccountCsv.Parse(csv, null, null);
        var sent = new List<string>();
        Task<PvwaAccount> Create(NewAccount account, CancellationToken ct)
        {
            sent.Add(account.UserName);
            return account.UserName switch
            {
                "dup" => Task.FromException<PvwaAccount>(new PvwaException(HttpStatusCode.BadRequest, "PASWS027E", "Account already exists")),
                "expire" => Task.FromException<PvwaAccount>(new PvwaException(HttpStatusCode.Unauthorized, null, "Session expired")),
                _ => Task.FromResult(new PvwaAccount { Id = "id-" + account.UserName }),
            };
        }

        var path = Path.Combine(Path.GetTempPath(), $"cat-import-{Guid.NewGuid():N}.csv");
        try
        {
            RunWithTheme(() =>
            {
                string? asked = null;
                var progress = new ImportProgressDialog(import, Create) { AskToSave = summary => { asked = summary; return false; } };
                Assert.Equal(8, progress.RowsGrid.Columns.Count);

                // Les PVWA fictifs répondent tout de suite : l'import se termine sans boucle de messages.
                progress.RunAsync().GetAwaiter().GetResult();

                Assert.Equal(["adm1", "dup", "expire"], sent);
                Assert.Equal(
                    [ImportOutcome.Created, ImportOutcome.Refused, ImportOutcome.NotImported, ImportOutcome.NotSent, ImportOutcome.NotSent],
                    progress.Rows.Select(r => r.Outcome));
                Assert.Equal(("id-adm1", "Account already exists"), (import.Rows[0].AccountId, import.Rows[1].Detail));
                Assert.Equal(1, progress.Created);
                Assert.True(progress.SessionExpired);
                Assert.NotNull(asked);

                // Session expirée : la fenêtre s'est fermée et a effacé tous les mots de passe.
                Assert.All(import.Rows, r => Assert.True(r.Account?.Secret is null || r.Account.Secret.All(c => c == '\0')));

                progress.WriteResult(path);
            });

            var result = File.ReadAllText(path);
            Assert.Equal(6, result.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("id-adm1", result);
            Assert.Contains("Account already exists", result);
            Assert.DoesNotContain("Secret-", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Import des sessions d'un autre logiciel : aperçu (prêt, à vérifier, sans compte, non pris en charge), choix d'un autre
    /// compte, import sous le dossier choisi, résultat de chaque session et export CSV.
    /// </summary>
    [Fact]
    public void SessionImportWindowShowsTheResult()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"zt-sessions-{Guid.NewGuid():N}.csv");
        var missing = Path.Combine(Path.GetTempPath(), $"zt-missing-{Guid.NewGuid():N}.csv");
        try
        {
            RunWithTheme(() =>
            {
                var settings = new AppSettings();
                PvwaAccount[] accounts =
                [
                    new() { Id = "1", UserName = "root", Address = "web01.corp.local", PlatformId = "UnixSSH" },
                    new() { Id = "2", UserName = "deploy", Address = "web01.corp.local", PlatformId = "UnixSSH" },
                ];
                SessionImport? done = null;
                var window = new SessionImportWindow(settings, "pvwa.corp.local", accounts, KnownDomains.From([], []), i => done = i);
                Assert.Equal(SessionImportWindow.Sources.Count, window.SourceBox.Items.Count);
                Assert.Equal(10, window.RowsGrid.Columns.Count);
                Assert.False(window.ImportButton.IsEnabled);

                window.Load(
                [
                    ImportedSession.Terminal("Prod", "web", ImportProtocol.Ssh, "web01.corp.local", null, "root"),
                    ImportedSession.Terminal("Prod", "any", ImportProtocol.Ssh, "web01.corp.local", null, null),
                    ImportedSession.Terminal("", "gone", ImportProtocol.Ssh, "old01", null, "root"),
                    ImportedSession.Unsupported("", "vnc", "VNC", "vnc01"),
                ], "test");
                Assert.Equal([ImportState.Ready, ImportState.Check, ImportState.NoAccount, ImportState.Unsupported], window.Rows.Select(r => r.State));
                // Étiquette proposée d'après le dossier « Prod » de l'autre logiciel, modifiable ; « aucune » en fin de liste.
                Assert.Equal("PROD", window.Rows[0].Tag);
                Assert.Equal(["PROD", "QA", "DEV", ""], window.Rows[0].TagChoices.Select(c => c.Value));
                Assert.False(window.Rows[3].CanTag);
                Assert.True(window.ImportButton.IsEnabled);
                Assert.True(window.Rows[1].CanChoose);
                Assert.False(window.Rows[2].CanInclude);
                // Session à vérifier : pas cochée d'office ; le bouton compte les sessions qui seront ajoutées.
                Assert.False(window.Rows[1].Include);
                Assert.EndsWith("(1)", (string)window.ImportButton.Content);
                // Une session cochée sur deux : case de l'en-tête entre les deux.
                Assert.Null(((System.Windows.Controls.CheckBox)window.IncludeColumn.Header).IsChecked);

                // Même compte que la première session, dans le même dossier : ajouté une seule fois.
                window.Rows[1].Chosen = window.Rows[1].Candidates.Single(c => c.Account.Id == "1");
                Assert.True(window.Rows[1].Include);
                Assert.EndsWith("(2)", (string)window.ImportButton.Content);
                window.FolderBox.Text = "Migration";
                window.OnImport(window, new RoutedEventArgs());

                Assert.NotNull(done);
                Assert.Equal([ImportState.Imported, ImportState.AlreadyPresent, ImportState.NoAccount, ImportState.Unsupported],
                    window.Rows.Select(r => r.State));
                var saved = Assert.Single(settings.Sessions);
                Assert.Equal(("Migration/Prod", "web", "1"), (saved.Folder, saved.Name, saved.AccountId));
                Assert.False(window.ImportButton.IsEnabled);
                Assert.False(window.FolderBox.IsEnabled);
                Assert.True(window.ExportButton.IsEnabled);
                window.WriteResult(path);

                // Serveur sans compte (gone) : un compte à créer, au format de l'import de comptes.
                Assert.True(window.MissingAccountsButton.IsEnabled);
                Assert.Equal(1, window.WriteMissingAccounts(missing));
                window.Close();
            });

            Assert.Equal(5, File.ReadAllLines(path).Length);
            var accounts = File.ReadAllLines(missing);
            Assert.Equal(2, accounts.Length);
            Assert.Contains("old01", accounts[1]);
        }
        finally
        {
            File.Delete(path);
            File.Delete(missing);
        }
    }

    /// <summary>
    /// Case de l'en-tête de la reprise des sessions : avec « Seulement les problèmes », elle ne coche que les lignes affichées
    /// (une session prête décochée, cachée, le reste) ; son état suit les lignes affichées, cochées une à une ou non.
    /// </summary>
    [Fact]
    public void SessionImportHeaderBoxActsOnTheShownRowsOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
            PvwaAccount[] accounts =
            [
                new() { Id = "1", UserName = "root", Address = "web01.corp.local", PlatformId = "UnixSSH" },
                new() { Id = "2", UserName = "deploy", Address = "web01.corp.local", PlatformId = "UnixSSH" },
                new() { Id = "3", UserName = "root", Address = "web02.corp.local", PlatformId = "UnixSSH" },
            ];
            var window = new SessionImportWindow(new AppSettings(), "pvwa.corp.local", accounts, KnownDomains.From([], []), _ => { });
            window.Load(
            [
                ImportedSession.Terminal("", "web", ImportProtocol.Ssh, "web01.corp.local", null, "root"),
                ImportedSession.Terminal("", "any", ImportProtocol.Ssh, "web01.corp.local", null, null),
                ImportedSession.Terminal("", "web2", ImportProtocol.Ssh, "web02.corp.local", null, "root"),
            ], "test");
            var all = (System.Windows.Controls.CheckBox)window.IncludeColumn.Header;
            Assert.Equal([ImportState.Ready, ImportState.Check, ImportState.Ready], window.Rows.Select(r => r.State));
            Assert.Null(all.IsChecked);

            // Session prête décochée, puis seulement les problèmes : la case suit la seule ligne affichée (non cochée).
            window.Rows[0].Include = false;
            window.ProblemsBox.IsChecked = true;
            window.ProblemsBox.RaiseEvent(click);
            Assert.False(all.IsChecked);

            all.RaiseEvent(click);
            Assert.True(window.Rows[1].Include);
            Assert.False(window.Rows[0].Include);
            Assert.True(window.Rows[2].Include);
            Assert.True(all.IsChecked);
            Assert.EndsWith("(2)", (string)window.ImportButton.Content);

            // Toutes les lignes de nouveau : une décochée sur trois ; la cocher à la main coche la case de l'en-tête.
            window.ProblemsBox.IsChecked = false;
            window.ProblemsBox.RaiseEvent(click);
            Assert.Null(all.IsChecked);
            window.Rows[0].Include = true;
            Assert.True(all.IsChecked);
            all.RaiseEvent(click);
            Assert.All(window.Rows, r => Assert.False(r.Include));
            Assert.False(all.IsChecked);
            window.Close();
        });
    }

    /// <summary>
    /// Vérification d'un transfert : fichiers à revoir en premier (différent, puis non vérifié), bilan sur deux lignes,
    /// motif d'un fichier non vérifié.
    /// </summary>
    [Fact]
    public void TransferChecksWindowOpens()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            byte[] hash = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
            var ok = new TransferCheck("a", @"C:\a", "/srv/a", 10, hash, 10, [.. hash]) { Upload = true };
            var different = ok with { Name = "b", RemotePath = "/srv/b", RemoteSha256 = new byte[32] };
            var unverified = ok with { Name = "c", RemotePath = "/srv/c", RemoteLength = -1, RemoteSha256 = [], Error = "Permission denied" };

            var dialog = new TransferChecksDialog([ok, different, unverified]);

            Assert.Equal(6, dialog.ChecksGrid.Columns.Count);
            Assert.Equal(["/srv/b", "/srv/c", "/srv/a"], ((IEnumerable<TransferCheck>)dialog.ChecksGrid.ItemsSource).Select(c => c.RemotePath));
            Assert.Equal(2, dialog.HeadingText.Text.Split(Environment.NewLine).Length);
            Assert.Contains("Permission denied", TransferChecksDialog.Result(unverified));
            Assert.NotEqual(TransferChecksDialog.Result(ok), TransferChecksDialog.Result(different));
            Assert.Single(TransferChecksDialog.Heading([ok]).Split(Environment.NewLine));
            dialog.Close();
        });
    }

    /// <summary>
    /// Panneau de la file des transferts : visible pendant la série, élément en cours dans la barre d'état, élément en
    /// attente retiré, puis bilan unique à la fin.
    /// </summary>
    [Fact]
    public void TransferQueuePanelFollowsTheQueue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            // Sans contexte de synchronisation, la fin d'un transfert se poursuit sur ce fil (pas de boucle de messages ici).
            SynchronizationContext.SetSynchronizationContext(null);
            var panel = new FileBrowserPanel();
            Assert.Equal(Visibility.Collapsed, panel.QueuePanel.Visibility);
            var release = new TaskCompletionSource();
            var running = new TransferItem(true, "deploy/", "/opt/app", async (_, _) => await release.Task) { Protocol = "SCP", FileCount = 12 };
            var waiting = new TransferItem(false, "app.log", @"C:\Temp", (_, _) => Task.CompletedTask);

            panel.Queue.Enqueue(running);
            panel.Queue.Enqueue(waiting);

            Assert.Equal(Visibility.Visible, panel.QueuePanel.Visibility);
            Assert.Equal(2, panel.ActiveTransfers(null));
            Assert.Contains("deploy/", panel.StatusText.Text);
            Assert.Contains("(1/12)", panel.StatusText.Text);
            Assert.Contains(Text.Format(Strings.QueuePending, 1), panel.StatusText.Text);
            Assert.Equal(Strings.QueueStateWaiting, TransferStatusConverter.StateText(waiting));

            panel.Queue.Cancel(waiting);
            // Somme SHA-256 réelle (32 octets) : le bilan en affiche les 12 premiers caractères.
            var hash = System.Security.Cryptography.SHA256.HashData("app.conf"u8);
            running.Checks.Add(new TransferCheck("app.conf", @"C:\Temp\app.conf", "/opt/app/app.conf", 3, hash, 3, [.. hash]));
            release.SetResult();

            Assert.Equal((TransferState.Done, TransferState.Cancelled), (running.State, waiting.State));
            Assert.Equal(0, panel.ActiveTransfers(null));
            Assert.Contains(Text.Format(Strings.QueueSummaryCancelled, 1), panel.StatusText.Text);
            // Les résultats restent dans la file (vérification SHA-256 par ligne) jusqu'à « Effacer les terminés ».
            Assert.Equal(Visibility.Visible, panel.QueuePanel.Visibility);
            Assert.Equal(Text.Format(Strings.QueueStateVerified, 1, 1), TransferStatusConverter.StateText(running));
            Assert.Equal("Ok", TransferStatusConverter.Outcome(running));
            Assert.Equal((Visibility.Collapsed, Visibility.Visible), (panel.CancelAllButton.Visibility, panel.ClearFinishedButton.Visibility));
            Assert.Equal(0, panel.UnseenProblems);
            panel.Queue.ClearFinished();
            Assert.Equal(Visibility.Collapsed, panel.QueuePanel.Visibility);

            // Historique : l'envoi terminé y figure ; l'élément retiré avant d'avoir commencé, non.
            var record = Assert.Single(panel.History.Records);
            Assert.Equal(("deploy/", true, TransferState.Done, "SCP"), (record.Label, record.Upload, record.State, record.Protocol));

            // Un échec est signalé (pastille de l'onglet Fichiers) jusqu'à ce que l'onglet soit affiché.
            panel.Queue.Enqueue(new TransferItem(true, "fail/", "/opt/app", (_, _) => throw new IOException("disque plein")));
            Assert.Equal(1, panel.UnseenProblems);
            Assert.Equal("Error", TransferStatusConverter.Outcome(panel.Queue.Items[^1]));
            panel.MarkTransfersSeen();
            Assert.Equal(0, panel.UnseenProblems);
        });
    }

    /// <summary>Nouveau nom (renommer, nouveau dossier) : ni « / », ni caractère de contrôle, ni « . » ou « .. ».</summary>
    [Fact]
    public void RemoteNamesAreChecked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(FileBrowserPanel.ValidateName("app.conf"));
        Assert.Null(FileBrowserPanel.ValidateName(".bashrc"));
        Assert.Equal(Strings.NameNoSlash, FileBrowserPanel.ValidateName("../etc/passwd"));
        Assert.Equal(Strings.NameNoControl, FileBrowserPanel.ValidateName("a\r\nDELE b"));
        Assert.Equal(Strings.NameReserved, FileBrowserPanel.ValidateName(".."));
        Assert.Equal(Strings.NameReserved, FileBrowserPanel.ValidateName("."));
    }

    /// <summary>
    /// F5 dans l'onglet Fichiers, hors de la liste aussi (chemin, filtre) : relit le dossier, sans atteindre le F5 de la
    /// fenêtre principale qui recharge les comptes du PVWA.
    /// </summary>
    [Fact]
    public void F5InTheFilesTabRefreshesTheFolderNotTheAccounts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var panel = new FileBrowserPanel();
            int reloads = 0;
            var window = new Window { Content = panel };
            window.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.NavigationCommands.Refresh, (_, _) => reloads++));

            System.Windows.Input.NavigationCommands.Refresh.Execute(null, panel.PathBox);
            System.Windows.Input.NavigationCommands.Refresh.Execute(null, panel.FilterBox);
            System.Windows.Input.NavigationCommands.Refresh.Execute(null, panel.FileList);

            Assert.Equal(0, reloads);
            Assert.Contains(panel.InputBindings.OfType<System.Windows.Input.KeyBinding>(),
                b => b.Key == System.Windows.Input.Key.F5 && b.Command == System.Windows.Input.NavigationCommands.Refresh);
            window.Close();
        });
    }

    /// <summary>Onglet Fichiers : tri par colonne (« .. » et dossiers en tête), flèche dans l'en-tête, réglage enregistré.</summary>
    [Fact]
    public void FileListSortsByTheClickedColumn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var settings = new AppSettings();
            int saved = 0;
            var panel = new FileBrowserPanel();
            panel.Initialize(settings, () => saved++, new TransferHistory());
            Assert.Equal(false, (panel.NameColumn.Header as FrameworkElement)?.Tag); // croissant
            RemoteEntry E(string name, bool dir, long size, int day) =>
                new(name, "/opt/" + name, dir, false, size, new DateTime(2026, 10, day), dir ? "drwxr-xr-x" : "-rw-r--r--");
            var big = E("big.tar", false, 9000, 1);
            panel.FileList.ItemsSource = new List<RemoteEntry>
            {
                RemoteEntry.ParentLink("/opt"), E("app", true, 4096, 2), E("a.log", false, 10, 9), big, E("z.conf", false, 200, 5),
            };
            panel.FileList.SelectedItems.Add(big);

            panel.SortBy(RemoteSortColumn.Modified, descending: true);

            Assert.Equal(["..", "app", "a.log", "z.conf", "big.tar"],
                panel.FileList.Items.Cast<RemoteEntry>().Select(e => e.Name));
            Assert.Equal(true, (panel.ModifiedColumn.Header as FrameworkElement)?.Tag); // décroissant
            Assert.Equal(ZillaTerm.Core.Localization.CoreStrings.ColumnName, panel.NameColumn.Header);
            Assert.Same(big, Assert.Single(panel.FileList.SelectedItems.Cast<RemoteEntry>()));
            Assert.Equal((RemoteSortColumn.Modified, true, 1), (settings.FileSortColumn, settings.FileSortDescending, saved));

            panel.SortBy(RemoteSortColumn.Size, descending: false);
            Assert.Equal(["..", "app", "a.log", "z.conf", "big.tar"],
                panel.FileList.Items.Cast<RemoteEntry>().Select(e => e.Name));

            // Propriétaire : dossiers en tête, puis par propriétaire et groupe ; flèche dans l'en-tête de la colonne.
            panel.FileList.ItemsSource = panel.FileList.Items.Cast<RemoteEntry>()
                .Select(e => e.IsParentLink ? e : e.Name == "z.conf" ? e with { Owner = "apache", Group = "www" } : e with { Owner = "root", Group = "root" })
                .ToList();
            panel.SortBy(RemoteSortColumn.Owner, descending: false);
            Assert.Equal(["..", "app", "z.conf", "a.log", "big.tar"],
                panel.FileList.Items.Cast<RemoteEntry>().Select(e => e.Name));
            Assert.Equal(false, (panel.OwnerColumn.Header as FrameworkElement)?.Tag);
            panel.SortBy(RemoteSortColumn.Group, descending: false);
            Assert.Equal(["..", "app", "a.log", "big.tar", "z.conf"],
                panel.FileList.Items.Cast<RemoteEntry>().Select(e => e.Name));
            Assert.Equal(false, (panel.GroupColumn.Header as FrameworkElement)?.Tag);
            Assert.Equal(Strings.ColumnOwner, panel.OwnerColumn.Header);
            Assert.Equal(Strings.ColumnSize, panel.SizeColumn.Header);
            panel.SortBy(RemoteSortColumn.Size, descending: false);

            // Filtre du dossier : « .. » reste affiché, le tri garde le filtre, le vider rend tout le dossier.
            panel.FilterBox.Text = "*.log;app";
            Assert.Equal(["..", "app", "a.log"], panel.FileList.Items.Cast<RemoteEntry>().Select(e => e.Name));
            Assert.Equal(Text.Format(Strings.FilesFilterSummary, 2, 4), panel.StatusText.Text);
            panel.SortBy(RemoteSortColumn.Name, descending: true);
            Assert.Equal(["..", "app", "a.log"], panel.FileList.Items.Cast<RemoteEntry>().Select(e => e.Name));
            panel.FilterBox.Text = "";
            Assert.Equal(5, panel.FileList.Items.Count);
        });
    }

    /// <summary>Onglet Fichiers : une lettre tapée dans la liste sélectionne l'élément qui commence par elle, la même lettre le suivant.</summary>
    [Fact]
    public void TypingInTheFileListSelectsByName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var panel = new FileBrowserPanel();
            panel.Initialize(new AppSettings(), () => { }, new TransferHistory());
            var window = new Window { Content = panel, Width = 600, Height = 400 };
            window.Show();
            RemoteEntry E(string name) => new(name, "/etc/" + name, false, false, 10, new DateTime(2026, 10, 1), "-rw-r--r--");
            panel.FileList.ItemsSource = new List<RemoteEntry> { RemoteEntry.ParentLink("/etc"), E("hosts"), E("passwd"), E("profile"), E("shadow") };
            panel.FileList.SelectedItems.Add(panel.FileList.Items[1]);
            panel.FileList.SelectedItems.Add(panel.FileList.Items[4]);

            string Type(string text)
            {
                var input = System.Windows.Input.InputManager.Current;
                panel.FileList.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    new System.Windows.Input.TextComposition(input, panel.FileList, text))
                {
                    RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent,
                });
                return string.Join(",", panel.FileList.SelectedItems.Cast<RemoteEntry>().Select(e => e.Name));
            }

            Assert.Equal("passwd", Type("p"));
            Assert.Equal("profile", Type("p"));
            Assert.Equal("passwd", Type("P"));
            Assert.Equal("passwd", Type("x"));

            // Espace d'un nom : la liste la prendrait (touche traitée avant TextInput), et « f » chercherait « rapportf ».
            Thread.Sleep(TypeAhead.Pause + TimeSpan.FromMilliseconds(200));
            panel.FileList.ItemsSource = new List<RemoteEntry> { E("rapportfinal-old.txt"), E("rapport final.txt") };
            window.UpdateLayout();
            Assert.Equal("rapportfinal-old.txt", Type("rapport"));
            var space = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0,
                System.Windows.Input.Key.Space) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            panel.FileList.RaiseEvent(space);
            Assert.True(space.Handled);
            Assert.Equal("rapport final.txt", Type("f"));
            window.Close();
        });
    }

    /// <summary>
    /// Panneau à sa largeur par défaut : les colonnes sans la place sont masquées et « +n » le signale (noms dans
    /// l'infobulle) ; le menu de l'en-tête choisit les colonnes (gardé dans les réglages) et propose d'élargir le panneau.
    /// </summary>
    [Fact]
    public void HiddenFileColumnsAreSignalledAndCanBeChosen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var settings = new AppSettings();
            int saved = 0;
            var panel = new FileBrowserPanel();
            panel.Initialize(settings, () => saved++, new TransferHistory());
            void Layout(double width)
            {
                panel.Measure(new Size(width, 600));
                panel.Arrange(new Rect(0, 0, width, 600));
                panel.UpdateLayout();
            }

            Layout(400);
            double available = panel.FileList.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
            Assert.Equal(400, panel.FileList.ActualWidth);
            Assert.Equal((76.0, 112.0), (panel.SizeColumn.Width, panel.ModifiedColumn.Width));
            Assert.Equal((0.0, 0.0, 0.0), (panel.PermissionsColumn.Width, panel.OwnerColumn.Width, panel.GroupColumn.Width));
            Assert.Equal("+3", panel.MoreColumnText.Text);
            Assert.True(panel.MoreColumn.Width > 0);
            Assert.Equal(Text.Format(Strings.FilesColumnsHiddenTip, string.Join(", ", Strings.ColumnPermissions, Strings.ColumnOwner, Strings.ColumnGroup)),
                panel.MoreColumnText.ToolTip);
            Assert.Equal(available - 76 - 112 - panel.MoreColumn.Width, panel.NameColumn.Width, 3);

            // Menu de l'en-tête : une case par colonne ; une colonne cochée sans la place est signalée.
            var menu = (System.Windows.Controls.ContextMenu)panel.FindResource("ColumnsMenu");
            menu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.ContextMenu.OpenedEvent, menu));
            var items = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            Assert.Equal([Strings.ColumnSize, Strings.ColumnModified, Strings.ColumnPermissions, Strings.ColumnOwner, Strings.ColumnGroup],
                items.Select(i => (string)i.Header));
            Assert.All(items, i => Assert.True(i.IsChecked));
            Assert.Equal(["", "", Strings.FilesColumnNoRoom, Strings.FilesColumnNoRoom, Strings.FilesColumnNoRoom], items.Select(i => i.InputGestureText));

            // Taille et Modifié décochées : Droits et Propriétaire prennent leur place, Groupe reste signalé.
            items[0].IsChecked = false;
            items[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            panel.ShowColumn(ZillaTerm.Core.Ssh.RemoteSortColumn.Modified, show: false);
            Assert.Equal([ZillaTerm.Core.Ssh.RemoteSortColumn.Size, ZillaTerm.Core.Ssh.RemoteSortColumn.Modified], settings.HiddenFileColumns);
            Assert.Equal(2, saved);
            Assert.Equal((0.0, 0.0, 84.0, 90.0, 0.0), (panel.SizeColumn.Width, panel.ModifiedColumn.Width, panel.PermissionsColumn.Width,
                panel.OwnerColumn.Width, panel.GroupColumn.Width));
            Assert.Equal("+1", panel.MoreColumnText.Text);
            Assert.Equal(Strings.FilesColumnNoRoom, items[4].InputGestureText);

            // « Élargir le panneau » : ce qu'il manque pour Droits, Propriétaire et Groupe avec la largeur minimale du nom.
            double asked = 0;
            panel.WidenRequested += extra => asked = extra;
            menu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.ContextMenu.OpenedEvent, menu));
            var widen = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => Equals(i.Header, Strings.FilesColumnsWiden));
            Assert.True(widen.IsEnabled);
            widen.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert.Equal(Math.Ceiling(130 + 84 + 90 + 90 - available), asked);

            // Toutes les colonnes réaffichées, plus rien de masqué faute de place dans un panneau assez large.
            panel.ShowColumn(ZillaTerm.Core.Ssh.RemoteSortColumn.Size, show: true);
            panel.ShowColumn(ZillaTerm.Core.Ssh.RemoteSortColumn.Modified, show: true);
            Layout(800);
            Assert.Empty(settings.HiddenFileColumns);
            Assert.Equal((84.0, 90.0, 90.0, 0.0), (panel.PermissionsColumn.Width, panel.OwnerColumn.Width, panel.GroupColumn.Width, panel.MoreColumn.Width));
            Assert.Equal(0, panel.MissingWidth);
        });
    }

    /// <summary>
    /// Opération longue sur un serveur : les actions sont refusées sur ce serveur seulement, et la barre d'état le dit ;
    /// les autres serveurs restent utilisables.
    /// </summary>
    [Fact]
    public void BusyServerDoesNotBlockTheOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var panel = new FileBrowserPanel();
            var first = new FakeFiles("/root", []);
            var second = new FakeFiles("/root", []);

            panel.BeginBusy(first);
            Assert.False(panel.RefuseIfBusy(second));
            Assert.Equal("", panel.StatusText.Text);
            Assert.True(panel.RefuseIfBusy(first));
            Assert.Equal(Strings.FilesBusy, panel.StatusText.Text);

            // Deux opérations sur le même serveur : il reste occupé jusqu'à la fin de la seconde.
            panel.BeginBusy(first);
            panel.EndBusy(first);
            Assert.True(panel.RefuseIfBusy(first));
            panel.EndBusy(first);
            Assert.False(panel.RefuseIfBusy(first));
        });
    }

    /// <summary>
    /// Archive .tar.gz envoyée : encadré bien visible avec la commande d'extraction de sa session (une seule ligne, sans
    /// retour à la ligne), qui reste jusqu'à ce qu'on le ferme ou que la session se ferme.
    /// </summary>
    [Fact]
    public void ArchiveExtractBoxShowsTheCommandOfItsSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var (srv01, _) = NewSshView("root@srv01");
            var (srv02, _) = NewSshView("root@srv02");
            var panel = new FileBrowserPanel();
            panel.Attach(srv01);
            Assert.Equal(Visibility.Collapsed, panel.ExtractPanel.Visibility);

            const string command = "cd '/opt/app' && gzip -dc './deploy.tar.gz' | tar -xf - && rm -f './deploy.tar.gz'";
            var archive = new TransferItem(true, "deploy.tar.gz", "/opt/app", (item, _) =>
            {
                item.ExtractCommand = command;
                return Task.CompletedTask;
            }) { Owner = srv01 };
            panel.Queue.Enqueue(archive);

            Assert.Equal(Visibility.Visible, panel.ExtractPanel.Visibility);
            Assert.Equal(command, panel.ExtractCommandBox.Text);
            Assert.Equal(Text.Format(Strings.ArchiveToExtractOne, "/opt/app", "root@srv01"), panel.ExtractTitle.Text);
            // Session non connectée : rien ne peut être écrit dans son terminal.
            Assert.False(panel.InsertExtractButton.IsEnabled);

            // L'encadré suit la session affichée.
            panel.Attach(srv02);
            Assert.Equal(Visibility.Collapsed, panel.ExtractPanel.Visibility);
            panel.Attach(srv01);
            Assert.Equal(Visibility.Visible, panel.ExtractPanel.Visibility);

            panel.ReleaseTails(srv01);
            Assert.Equal(Visibility.Collapsed, panel.ExtractPanel.Visibility);
            srv01.Dispose();
            srv02.Dispose();
        });
    }

    /// <summary>Historique : filtre par sens, résultat lisible, sommes et dossier disponibles pour un téléchargement.</summary>
    [Fact]
    public void TransferHistoryWindowOpens()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            byte[] hash = [1, 2, 3];
            var history = new TransferHistory();
            history.Add(new TransferRecord
            {
                Upload = false, Label = "app.log", Destination = Path.GetTempPath(), State = TransferState.Done, FileCount = 1,
                Files = [new TransferCheck("app.log", @"C:\Temp\app.log", "/var/log/app.log", 3, hash, 3, [.. hash])],
            });
            history.Add(new TransferRecord { Upload = true, Label = "deploy/", Destination = "/opt/app", State = TransferState.Failed, Error = "permission refusée" });
            int saved = 0;

            var dialog = new TransferHistoryDialog(history, () => saved++);

            Assert.Equal(8, dialog.RecordsGrid.Columns.Count);
            // Colonne « Sens » assez large pour ses deux textes, dans la langue affichée.
            double Width(string text) => new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new System.Windows.Media.Typeface(dialog.RecordsGrid.FontFamily, dialog.RecordsGrid.FontStyle,
                    FontWeights.SemiBold, dialog.RecordsGrid.FontStretch), dialog.RecordsGrid.FontSize, System.Windows.Media.Brushes.Black, 1)
                .WidthIncludingTrailingWhitespace;
            Assert.True(dialog.RecordsGrid.Columns[1].MinWidth > Math.Max(Width(Strings.ChecksUploaded), Width(Strings.ChecksDownloaded)));
            Assert.Contains(dialog.RecordsGrid.Columns, c => Equals(c.Header, Strings.HistoryColProtocol));
            Assert.Equal(2, ((IEnumerable<TransferRecord>)dialog.RecordsGrid.ItemsSource).Count());
            dialog.FilterBox.SelectedIndex = 2;
            var download = Assert.Single((IEnumerable<TransferRecord>)dialog.RecordsGrid.ItemsSource);
            dialog.RecordsGrid.SelectedItem = download;
            Assert.True(dialog.ChecksButton.IsEnabled);
            Assert.True(dialog.OpenFolderButton.IsEnabled);
            Assert.Equal(Text.Format(Strings.QueueStateVerified, 1, 1), TransferHistoryDialog.Result(download));
            Assert.Equal("✗ permission refusée", TransferHistoryDialog.Result(history.Records[0]));
            // Échec en rouge, réussite normale.
            Assert.True(TransferHistoryDialog.HasProblem(history.Records[0]));
            Assert.False(TransferHistoryDialog.HasProblem(download));
            Assert.NotNull(dialog.RecordsGrid.RowStyle);
            Assert.Equal(0, saved);

            // Échecs et non vérifiés seulement : l'envoi en échec et le téléchargement non relu, pas celui vérifié.
            history.Add(new TransferRecord
            {
                Upload = false, Label = "big.iso", Destination = Path.GetTempPath(), State = TransferState.Done, FileCount = 1,
                Files = [new TransferCheck("big.iso", @"C:\Temp\big.iso", "/srv/big.iso", -1, [], 3, [.. hash], "fichier verrouillé")],
            });
            dialog.FilterBox.SelectedIndex = TransferHistoryDialog.ProblemsFilter;
            Assert.Equal(["big.iso", "deploy/"], ((IEnumerable<TransferRecord>)dialog.RecordsGrid.ItemsSource).Select(r => r.Label));
            dialog.FilterBox.SelectedIndex = 1;
            dialog.Close();

            // Ouvert pour un échec signalé : directement sur ce filtre. Rien qui corresponde : c'est dit.
            var problems = new TransferHistoryDialog(history, () => saved++, problemsOnly: true);
            Assert.Equal(TransferHistoryDialog.ProblemsFilter, problems.FilterBox.SelectedIndex);
            Assert.Equal(2, ((IEnumerable<TransferRecord>)problems.RecordsGrid.ItemsSource).Count());
            problems.Close();
            var clean = new TransferHistory();
            clean.Add(history.Records[^1]);
            var none = new TransferHistoryDialog(clean, () => saved++, problemsOnly: true);
            Assert.Empty((IEnumerable<TransferRecord>)none.RecordsGrid.ItemsSource);
            Assert.Equal((Strings.HistoryNoMatch, Visibility.Visible), (none.EmptyText.Text, none.EmptyText.Visibility));
            none.Close();
        });
    }

    /// <summary>
    /// Gros envoi : la proposition d'archive donne le nombre de fichiers, la taille et la destination ; l'option des
    /// Paramètres (case et seuil) est lue et enregistrée ; l'état « archive » s'affiche dans la file.
    /// </summary>
    [Fact]
    public void ArchiveOfferAndSettingOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var offer = new ArchiveOfferDialog(2345, 5L * 1024 * 1024, "/opt/app");
            Assert.Contains("/opt/app", offer.MessageText.Text);
            Assert.Contains(RemotePath.FormatSize(5L * 1024 * 1024), offer.MessageText.Text);
            Assert.False(offer.UseArchive);
            Assert.False(offer.DontOfferAgain);
            Assert.Equal(Text.Format(Strings.ArchiveOfferArchive, "._tar.gz"), offer.ArchiveButton.Content);
            offer.Close();

            // gzip absent du serveur : archive .tar, et la fenêtre le dit.
            var plain = new ArchiveOfferDialog(2345, 5L * 1024 * 1024, "/opt/app", compressed: false);
            Assert.Contains(Strings.ArchiveOfferNoGzip, plain.MessageText.Text);
            Assert.DoesNotContain(".tar.gz", plain.MessageText.Text);
            Assert.Equal(Text.Format(Strings.ArchiveOfferArchive, "._tar"), plain.ArchiveButton.Content);
            plain.Close();

            var settings = new AppSettings { OfferArchive = true, ArchiveThreshold = 500 };
            var dialog = new SettingsDialog(settings);
            Assert.Equal((true, "500"), (dialog.ArchiveBox.IsChecked, dialog.ArchiveThresholdBox.Text));
            dialog.Close();

            var item = new TransferItem(true, "deploy.tar.gz (2345)", "/opt/app", (_, _) => Task.CompletedTask);
            item.Report(new TransferProgress("deploy.tar.gz", 50, 200, Packing: true));
            Assert.True(item.Packing);
        });
    }

    /// <summary>Suivi d'un fichier : nouvelles lignes, filtre, exclusion, contexte, recherche, repère.</summary>
    [Fact]
    public void TailWindowFollowsFiltersAndSearches()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var link = new MemoryLink("root@srv01");
            var file = link.Add("/var/log/app.log");
            file.Append("a\nERROR disque\nb\nc\nd\nerror réseau\n");
            var window = new TailWindow(new AppSettings(), () => { });
            window.AddFeed(link, "/var/log/app.log");
            Assert.Contains("/var/log/app.log", window.Title);

            window.PollAsync().GetAwaiter().GetResult();
            Assert.Equal("a\nERROR disque\nb\nc\nd\nerror réseau\n", window.Shown);
            Assert.Equal(RemotePath.FormatSize(file.Length), window.Feeds[0].Status);

            window.FilterBox.Text = "error";
            Assert.Equal("ERROR disque\nerror réseau\n", window.Shown);
            window.ExcludeBox.Text = "DISQUE";
            Assert.Equal("error réseau\n", window.Shown);
            window.ExcludeBox.Text = "";

            // Contexte d'une ligne, comme grep -C 1.
            window.ContextBox.SelectedItem = 1;
            Assert.Equal("a\nERROR disque\nb\n--\nd\nerror réseau\n", window.Shown);
            window.ContextBox.SelectedItem = 0;

            // Expression régulière ; une expression invalide n'est pas appliquée.
            window.FilterBox.Text = "r(é|e)seau$";
            window.RegexBox.IsChecked = true;
            window.RegexBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("error réseau\n", window.Shown);
            window.FilterBox.Text = "(";
            Assert.Equal(6, window.Shown.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            window.FilterBox.Text = "";

            // Les lignes qui arrivent passent par le filtre.
            window.FilterBox.Text = "error";
            file.Append("info\nERROR base\n");
            window.PollAsync().GetAwaiter().GetResult();
            Assert.EndsWith("error réseau\nERROR base\n", window.Shown);
            window.FilterBox.Text = "";

            // Recherche sans filtrer : sélection de la ligne trouvée, la vue ne suit plus la fin.
            window.SearchBox.Text = "réseau";
            window.FindNext(1);
            Assert.Equal(5, window.LogList.SelectedIndex);
            Assert.False(window.FollowBox.IsChecked);
            Assert.Equal(Text.Format(Strings.TailSearchCount, 1, 1), window.SearchCount.Text);
            window.SearchBox.Text = "introuvable";
            window.FindNext(1);
            Assert.Equal(Strings.TailSearchNone, window.SearchCount.Text);

            window.AddUserMarker();
            Assert.Matches("—— \\d\\d:\\d\\d:\\d\\d ——\n$", window.Shown);
            window.Close();
            Assert.True(link.Disposed);
        });
    }

    /// <summary>Alertes : seulement sur les nouvelles lignes ; la notification ne contient pas la ligne.</summary>
    [Fact]
    public void TailWindowRaisesAlerts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var link = new MemoryLink("root@srv01");
            var file = link.Add("/var/log/app.log");
            file.Append("java.lang.OutOfMemoryError: ancien\n");
            var notes = new List<string>();
            var window = new TailWindow(new AppSettings { TailAlerts = "OutOfMemory, refused" }, () => { }) { Notifier = (_, text) => notes.Add(text) };
            window.AddFeed(link, "/var/log/app.log");
            window.PollAsync().GetAwaiter().GetResult();
            Assert.Equal(0, window.AlertCount);

            file.Append("Connection REFUSED by db01\nok\njava.lang.OutOfMemoryError: Java heap space\n");
            window.PollAsync().GetAwaiter().GetResult();
            Assert.Equal(2, window.AlertCount);
            Assert.Equal(Visibility.Visible, window.AlertButton.Visibility);
            var note = Assert.Single(notes);
            Assert.Contains("app.log", note);
            Assert.DoesNotContain("heap", note);
            Assert.DoesNotContain("db01", note);

            // Une seule notification par période, même si d'autres alertes arrivent.
            file.Append("refused again\n");
            window.PollAsync().GetAwaiter().GetResult();
            Assert.Equal(3, window.AlertCount);
            Assert.Single(notes);
            window.Close();
        });
    }

    /// <summary>Vue combinée, connexion perdue puis reprise, fin de session, enregistrement.</summary>
    [Fact]
    public void TailWindowCombinesResumesAndRecords()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var connector = new ZillaTerm.Core.Ssh.SshConnector("127.0.0.1", 22, "root", new NoInteraction());
            var session = new ZillaTerm.App.Services.SshSession(null, "root@srv02", connector,
                System.Windows.Threading.Dispatcher.CurrentDispatcher, followTerminal: false, saved: null);
            var first = new MemoryLink("root@srv01");
            var second = new MemoryLink("root@srv02", session);
            first.Add("/var/log/app.log").Append("un\n");
            var other = second.Add("/var/log/other.log");
            other.Append("deux\n");
            var window = new TailWindow(new AppSettings(), () => { });
            window.AddFeed(first, "/var/log/app.log");
            window.AddFeed(second, "/var/log/other.log");
            Assert.Equal(Text.Format(Strings.TailTitleMany, 2), window.Title);
            window.PollAsync().GetAwaiter().GetResult();
            Assert.Contains("[root@srv01 app.log] un\n", window.Shown);
            Assert.Contains("[root@srv02 other.log] deux\n", window.Shown);

            var directory = Path.Combine(Path.GetTempPath(), $"cat-tail-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var record = Path.Combine(directory, "record.log");
                window.StartRecording(record);

                // Connexion perdue : repère, attente ; reconnexion : les lignes écrites entre-temps arrivent.
                second.Connected = false;
                window.PollAsync().GetAwaiter().GetResult();
                Assert.Contains("[root@srv02 other.log] " + Strings.TailLostMarker, window.Shown);
                Assert.Equal(Strings.TailWaiting, window.Feeds[1].Status);
                Assert.Equal(Visibility.Visible, window.ReconnectButton.Visibility);
                other.Append("trois\n");
                second.Connected = true;
                second.Generation++;
                window.PollAsync().GetAwaiter().GetResult();
                Assert.EndsWith("[root@srv02 other.log] " + Strings.TailResumedMarker + "\n[root@srv02 other.log] trois\n", window.Shown);

                // Session fermée : le fichier n'est plus suivi, les lignes restent.
                window.EndSession(session);
                Assert.True(second.Disposed);
                Assert.Equal(Strings.TailSessionClosed, window.Feeds[1].Status);
                Assert.Contains(Strings.TailSessionClosedMarker, window.Shown);

                var save = Path.Combine(directory, "save.log");
                window.SaveTo(save);
                Assert.Equal(window.Shown.TrimEnd('\n').Split('\n'), File.ReadAllLines(save));

                window.Close();
                var recorded = File.ReadAllText(record);
                Assert.StartsWith("[root@srv01 app.log] un\r\n[root@srv02 other.log] deux\r\n", recorded);
                Assert.Contains("[root@srv02 other.log] trois\r\n", recorded);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
                session.Dispose();
            }
        });
    }

    /// <summary>
    /// Session fermée : le lien de suivi ne la retient plus (ni son terminal et son historique) ; les lignes reçues
    /// restent, avec le nom du serveur.
    /// </summary>
    [Fact]
    public void TailLinkReleasesAClosedSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var (session, _) = NewSshView("root@srv01");
            var link = new SessionTailLink(session, dedicated: false, shared: null);
            var window = new TailWindow(new AppSettings(), () => { });
            var feed = window.AddFeed(link, "/var/log/app.log");
            Assert.Same(link, window.LinkFor(session));
            Assert.Same(session, link.Session);

            window.EndSession(session);

            Assert.Null(link.Session);
            Assert.Null(window.LinkFor(session));
            Assert.True(feed.Stopped);
            Assert.Equal(("root@srv01", "root@srv01 app.log"), (link.Server, feed.Label));
            Assert.False(link.CheckConnected());
            Assert.False(link.CanReconnect);
            Assert.Contains(Strings.TailSessionClosedMarker, window.Shown);
            window.Close();
            session.Dispose();
        });
    }

    /// <summary>
    /// Nom de fichier piégé (inversion du sens d'écriture) : affiché avec « � » dans le titre, la liste des fichiers
    /// suivis et la notification ; le fichier est suivi quand même. Ligne géante : coupée, avec la mention.
    /// </summary>
    [Fact]
    public void TailWindowShowsSafeNamesAndCutsGiantLines()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            const string path = "/var/log/facture\u202Efdp.log";
            var link = new MemoryLink("root@srv01");
            var file = link.Add(path);
            file.Append("un\n");
            var notes = new List<string>();
            var window = new TailWindow(new AppSettings { TailAlerts = "ERROR" }, () => { }) { Notifier = (_, text) => notes.Add(text) };
            var feed = window.AddFeed(link, path);
            window.PollAsync().GetAwaiter().GetResult();
            Assert.Equal("un\n", window.Shown);
            Assert.Contains("/var/log/facture\uFFFDfdp.log", window.Title);
            // Comparaison ordinale : celle de la culture ignore les caractères invisibles (trouvés partout, en position 0).
            Assert.DoesNotContain("\u202E", window.Title, StringComparison.Ordinal);
            Assert.Equal("root@srv01 facture\uFFFDfdp.log", feed.Label);
            Assert.Equal("root@srv01 : /var/log/facture\uFFFDfdp.log", feed.Tip);

            file.Append("ERROR " + new string('x', 100_000) + "\nfin\n");
            window.PollAsync().GetAwaiter().GetResult();
            Assert.Contains("facture\uFFFDfdp.log", Assert.Single(notes));
            var cut = "ERROR " + new string('x', TailLineSplitter.MaxLineLength - 6) + TailLineSplitter.CutMark;
            Assert.Equal("un\n" + cut + "\nfin\n", window.Shown);
            window.Close();
        });
    }

    /// <summary>
    /// Dossier envoyé avec un lien vers un dossier : le lien, non suivi, est signalé à part dans le bilan, le détail et
    /// l'historique, sans compter comme un fichier non vérifié.
    /// </summary>
    [Fact]
    public void SkippedLinksAreReportedApart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var hash = System.Security.Cryptography.SHA256.HashData("<h1>ok</h1>"u8);
            var sent = new TransferCheck("index.html", @"C:\site\index.html", "/srv/site/index.html", 11, hash, 11, [.. hash]) { Upload = true };
            var link = new TransferCheck("loop", @"C:\site\loop", "/srv/site/loop", -1, [], -1, [], @"C:\site") { Upload = true, Skipped = true };
            var panel = new FileBrowserPanel();
            var item = new TransferItem(true, "site/", "/srv", (i, _) =>
            {
                i.Checks.Add(sent);
                i.Checks.Add(link);
                return Task.CompletedTask;
            }) { Protocol = "SFTP", FileCount = 1 };

            panel.Queue.Enqueue(item);

            Assert.Equal(Text.Format(Strings.QueueStateVerified, 1, 1), TransferStatusConverter.StateText(item));
            Assert.Equal("Ok", TransferStatusConverter.Outcome(item));
            Assert.Contains(Text.Format(Strings.TransferLinksSkipped, 1), panel.StatusText.Text);
            Assert.DoesNotContain(Text.Format(Strings.TransferNotVerified, 1), panel.StatusText.Text);
            Assert.Equal(Text.Format(Strings.ChecksAllOk, 1) + Environment.NewLine + Text.Format(Strings.ChecksSomeSkipped, 1),
                TransferChecksDialog.Heading([sent, link]));
            Assert.Equal(Text.Format(Strings.ChecksSkipped, @"C:\site"), TransferChecksDialog.Result(link));
            var record = Assert.Single(panel.History.Records);
            Assert.Equal(Text.Format(Strings.QueueStateVerified, 1, 1), TransferHistoryDialog.Result(record));
            Assert.False(TransferHistoryDialog.NeedsReview(record));
        });
    }

    /// <summary>Couleur du niveau, mots surlignés et préfixe du fichier dans le texte affiché d'une ligne.</summary>
    [Fact]
    public void TailLinesAreColoured()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var feed = new TailFeed(new MemoryLink("root@srv01"), "/var/log/app.log", 0);
            var style = new TailStyle(["db01"], null, Colors: true, Prefixes: true, Wrap: false);
            var block = new System.Windows.Controls.TextBlock();
            TailRowText.SetRow(block, new TailRow(new TailLine("12:00 ERROR db01 down", feed, TailLevel.Error, false), TailShownKind.Line, style));
            Assert.Same(TailBrushes.Error, block.Foreground);
            var runs = block.Inlines.OfType<System.Windows.Documents.Run>().ToList();
            Assert.Equal("[root@srv01 app.log] ", runs[0].Text);
            Assert.Contains(runs, r => r.Text == "db01" && ReferenceEquals(r.Background, TailBrushes.Highlight));

            TailRowText.SetRow(block, new TailRow(new TailLine("12:00 INFO ok", feed, TailLevel.None, false), TailShownKind.Line, style with { Colors = false }));
            Assert.NotSame(TailBrushes.Error, block.Foreground);

            var settings = new AppSettings { TailIndependentSession = true };
            var dialog = new SettingsDialog(settings);
            Assert.True(dialog.TailSessionBox.IsChecked);
            dialog.Close();
        });
    }

    /// <summary>
    /// Paramètres, page Sécurité : clés acceptées en tableau (serveur, type, empreinte) ; seules les clés choisies sont
    /// oubliées, et seulement à l'enregistrement.
    /// </summary>
    [Fact]
    public void SettingsForgetOnlyTheChosenHostKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var settings = new AppSettings();
            ZillaTerm.Core.Ssh.KnownHosts.Remember(settings.KnownHosts, "psmp.corp.local", 22, "ssh-ed25519", "AAAA");
            ZillaTerm.Core.Ssh.KnownHosts.Remember(settings.KnownHosts, "ftp.corp.local", 990, "X.509", "BBBB");
            // Deuxième clé du PSMP, d'un autre type : une ligne de plus pour le même serveur.
            ZillaTerm.Core.Ssh.KnownHosts.Remember(settings.KnownHosts, "psmp.corp.local", 22, "rsa-sha2-512", "CCCC");
            var dialog = new SettingsDialog(settings);
            Assert.Equal(3, dialog.HostKeyRows.Count);
            Assert.Contains(new SettingsDialog.HostKeyRow("psmp.corp.local:22", "ssh-ed25519", "SHA256:AAAA", "psmp.corp.local:22"), dialog.HostKeyRows);
            Assert.Contains(new SettingsDialog.HostKeyRow("psmp.corp.local:22", "rsa-sha2-512", "SHA256:CCCC", "psmp.corp.local:22 ssh-rsa"),
                dialog.HostKeyRows);
            Assert.False(dialog.ForgetKeysButton.IsEnabled);

            dialog.HostKeysGrid.SelectedItems.Add(dialog.HostKeyRows.Single(r => r.Server == "ftp.corp.local:990"));
            dialog.HostKeysGrid.SelectedItems.Add(dialog.HostKeyRows.Single(r => r.Algorithm == "rsa-sha2-512"));
            dialog.ForgetSelectedKeys();
            Assert.Equal(["psmp.corp.local:22"], dialog.HostKeyRows.Select(r => r.Server));
            Assert.Equal(3, settings.KnownHosts.Count);

            dialog.ApplyForgottenKeys();
            Assert.Equal(["psmp.corp.local:22"], settings.KnownHosts.Keys);
            Assert.Equal("ssh-ed25519 SHA256:AAAA", settings.KnownHosts["psmp.corp.local:22"]);
            dialog.Close();
        });
    }

    /// <summary>
    /// Paramètres, page CyberArk : PSMP par domaine. Le domaine suit l'adresse tant qu'il n'a pas été changé, l'essai
    /// d'un serveur montre le PSMP choisi, deux PSMP pour le même domaine sont refusés.
    /// </summary>
    [Fact]
    public void SettingsListPsmpByDomain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var settings = new AppSettings { PsmpAddress = "psmp.corp.com" };
            settings.PsmpServers.Add(new PsmpServer { Address = "psmp.xxx.ss.com" });
            settings.PsmpServers.Add(new PsmpServer { Address = "psmp01.infra.corp.com", Port = 2022, Domain = "dmz.corp.com" });
            var dialog = new SettingsDialog(settings);
            Assert.Equal(["xxx.ss.com", "dmz.corp.com"], dialog.PsmpRows.Select(r => r.Domain));

            dialog.PsmpTestBox.Text = "blabla.zzz.xxx.ss.com";
            Assert.Equal(Text.Format(Strings.PsmpTestMatch, "psmp.xxx.ss.com", 22, "xxx.ss.com"), dialog.PsmpTestText.Text);
            dialog.PsmpTestBox.Text = "web.dmz.corp.com";
            Assert.Equal(Text.Format(Strings.PsmpTestMatch, "psmp01.infra.corp.com", 2022, "dmz.corp.com"), dialog.PsmpTestText.Text);
            dialog.PsmpTestBox.Text = "srv.other.org";
            Assert.Equal(Text.Format(Strings.PsmpTestFallback, "psmp.corp.com", 22), dialog.PsmpTestText.Text);

            var row = dialog.PsmpRows[0];
            row.Address = "psmp.zzz.xxx.ss.com";
            Assert.Equal("zzz.xxx.ss.com", row.Domain);
            dialog.PsmpRows[1].Address = "psmp02.infra.corp.com";
            Assert.Equal("dmz.corp.com", dialog.PsmpRows[1].Domain);

            var saved = dialog.ReadPsmpServers("psmp.corp.com");
            Assert.NotNull(saved);
            Assert.Equal(("psmp.zzz.xxx.ss.com", 22, ""), (saved[0].Address, saved[0].Port, saved[0].Domain));
            Assert.Equal(("psmp02.infra.corp.com", 2022, "dmz.corp.com"), (saved[1].Address, saved[1].Port, saved[1].Domain));

            // Composant des comptes Windows : proposé parmi les composants usuels, nom vérifié.
            Assert.Equal("", dialog.WindowsComponentBox.Text);
            Assert.Contains("PSM-RDP", dialog.WindowsComponentBox.Items.OfType<string>());

            // Même domaine que le PSMP par défaut : celui de la liste ne servirait jamais.
            row.Domain = "corp.com";
            Assert.Null(dialog.ReadPsmpServers("psmp.corp.com"));
            Assert.Equal(Text.Format(Strings.PsmpDuplicateDomain, "corp.com"), dialog.ErrorText.Text);
            dialog.Close();
        });
    }

    /// <summary>
    /// Composant par plateforme : modifiable dans les Paramètres (modifier, ajouter, enlever) ; lignes vides ignorées,
    /// composant invalide et plateforme en double refusés.
    /// </summary>
    [Fact]
    public void SettingsEditComponentPerPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var settings = new AppSettings();
            settings.RememberComponent("WinDomain", "PSM-RDP");
            settings.RememberComponent("UnixSSH", "PSM-SSH");
            var dialog = new SettingsDialog(settings);
            Assert.Equal(["UnixSSH", "WinDomain"], dialog.ComponentRows.Select(r => r.Platform));

            dialog.ComponentRows[1].Component = "WIN-PSM";
            dialog.ComponentRows.RemoveAt(0);
            dialog.ComponentRows.Add(new SettingsDialog.ComponentRow { Platform = " WinServerLocal ", Component = "WIN-PSM-T1" });
            dialog.ComponentRows.Add(new SettingsDialog.ComponentRow());
            var saved = dialog.ReadComponents();
            Assert.NotNull(saved);
            Assert.Equal(new Dictionary<string, string> { ["WinDomain"] = "WIN-PSM", ["WinServerLocal"] = "WIN-PSM-T1" }, saved);

            dialog.ComponentRows[1].Component = "WIN PSM";
            Assert.Null(dialog.ReadComponents());
            Assert.Equal(Text.Format(Strings.InvalidComponentRow, 2), dialog.ErrorText.Text);

            dialog.ComponentRows[1].Component = "WIN-PSM-T1";
            dialog.ComponentRows[1].Platform = "windomain";
            Assert.Null(dialog.ReadComponents());
            Assert.Equal(Text.Format(Strings.ComponentDuplicatePlatform, "windomain"), dialog.ErrorText.Text);

            dialog.ComponentRows[2].Component = "PSM-SSH";
            dialog.ComponentRows[1].Platform = "UnixSSH";
            Assert.Null(dialog.ReadComponents());
            Assert.Equal(Text.Format(Strings.ComponentRowPlatformMissing, 3), dialog.ErrorText.Text);
            dialog.Close();
        });
    }

    /// <summary>
    /// Étiquettes des serveurs dans les Paramètres : renommer (les serveurs suivent), ajouter, supprimer (les serveurs la
    /// perdent) ; nom vide ou en double refusé.
    /// </summary>
    [Fact]
    public void SettingsEditServerTags()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var settings = new AppSettings
            {
                Sessions = [new SavedSession { Name = "a", Tag = "QA" }, new SavedSession { Name = "b", Tag = "DEV" }],
            };
            var dialog = new SettingsDialog(settings);
            Assert.Equal(["PROD", "QA", "DEV"], dialog.TagRows.Select(r => r.Name));

            dialog.TagRows[1].Name = "REC";
            dialog.TagRows[1].Color = "#F9A825";
            dialog.TagRows.RemoveAt(2);
            dialog.TagRows.Add(new SettingsDialog.TagRow(null, "SANDBOX", "#00838F"));
            var read = dialog.ReadTags();
            Assert.NotNull(read);
            Assert.Equal(["PROD", "REC", "SANDBOX"], read.Value.Tags.Select(t => t.Name));
            Assert.Equal("#F9A825", read.Value.Tags[1].Color);
            settings.ReplaceServerTags(read.Value.Tags, read.Value.Renamed);
            Assert.Equal(["REC", null], settings.Sessions.Select(x => x.Tag));

            dialog.TagRows[2].Name = "prod";
            Assert.Null(dialog.ReadTags());
            Assert.Equal(Strings.InvalidServerTag, dialog.ErrorText.Text);
            dialog.TagRows[2].Name = " ";
            Assert.Null(dialog.ReadTags());

            // 20 étiquettes au plus : « Ajouter » grisé à 20, et une 21ᵉ refusée au lieu d'être effacée sans un mot.
            dialog.TagRows[2].Name = "SANDBOX";
            Assert.True(dialog.AddTagButton.IsEnabled);
            for (int i = dialog.TagRows.Count; i < ServerTagRules.MaxTags; i++)
            {
                dialog.TagRows.Add(new SettingsDialog.TagRow(null, $"T{i}", "#00838F"));
            }

            Assert.NotNull(dialog.ReadTags());
            Assert.False(dialog.AddTagButton.IsEnabled);
            Assert.Equal(Strings.ServerTagLimitTip, dialog.AddTagButton.ToolTip);
            dialog.TagRows.Add(new SettingsDialog.TagRow(null, "DR", "#00838F"));
            Assert.Null(dialog.ReadTags());
            Assert.Equal(Strings.InvalidServerTag, dialog.ErrorText.Text);
            // Retour à 20 : toujours grisé ; à 19, de nouveau possible.
            dialog.TagRows.RemoveAt(dialog.TagRows.Count - 1);
            Assert.False(dialog.AddTagButton.IsEnabled);
            dialog.TagRows.RemoveAt(dialog.TagRows.Count - 1);
            Assert.True(dialog.AddTagButton.IsEnabled);
            dialog.Close();
        });
    }

    /// <summary>
    /// Ajout dans « Mes serveurs » : une seule fenêtre demande le dossier et l'étiquette ; l'étiquette devinée (nom, dossier)
    /// est cochée et signalée, et suit le dossier saisi tant qu'aucune n'a été choisie. Propriétés : l'étiquette se change,
    /// une étiquette absente des Paramètres reste proposée.
    /// </summary>
    [Fact]
    public void ServerTagIsAskedAndEditable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var tags = ServerTagRules.Defaults();
            var account = new PvwaAccount { Id = "7_1", UserName = "root", Address = "srv01.corp.local", PlatformId = "UnixSSH", SafeName = "LNX" };
            string? Guess(string name, string folder) =>
                ServerTagGuess.ForServer(tags, name, null, account.Address, account.SafeName, folder)?.Name;
            var prompt = new ServerPromptDialog(account, [], ["Prod", "Recette"], _ => false, keep: false, folder: "Prod", adding: true,
                domainAccount: false, tags, (_, folder) => Guess("root@srv01.corp.local", folder), name: "root@srv01.corp.local");
            Assert.Equal(Strings.AddToMyServersTitle, prompt.Title);
            Assert.Equal(Text.Format(Strings.AddToMyServersQuestion, "root@srv01.corp.local"), prompt.QuestionText.Text);
            Assert.Equal(Visibility.Collapsed, prompt.ServerPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, prompt.KeepBox.Visibility);
            Assert.Equal(Visibility.Visible, prompt.KeepPanel.Visibility);
            Assert.True(prompt.SavePanel.IsEnabled);
            Assert.Equal("Prod", prompt.Folder);
            Assert.Equal("PROD", prompt.ChosenTag);
            Assert.Equal(Visibility.Visible, prompt.GuessText.Visibility);
            Assert.True(prompt.Validate(required: false));

            // Dossier changé : l'étiquette devinée suit.
            prompt.FolderBox.Text = "Recette";
            Assert.Equal("Recette", prompt.Folder);
            Assert.Equal("QA", prompt.ChosenTag);
            prompt.FolderBox.Text = "";
            Assert.Null(prompt.ChosenTag);
            Assert.Equal(Visibility.Collapsed, prompt.GuessText.Visibility);

            // Étiquette cochée par l'utilisateur : gardée quel que soit le dossier.
            var buttons = prompt.TagChoices.Children.OfType<System.Windows.Controls.RadioButton>().ToList();
            Assert.Equal(4, buttons.Count);
            buttons[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            buttons[2].IsChecked = true;
            Assert.Equal("DEV", prompt.ChosenTag);
            prompt.FolderBox.Text = "Prod";
            Assert.Equal("DEV", prompt.ChosenTag);
            buttons[3].IsChecked = true;
            Assert.Null(prompt.ChosenTag);
            prompt.Close();

            // Sans étiquette dans les Paramètres : seul le dossier est demandé.
            var none = new ServerPromptDialog(account, [], [], _ => false, keep: false, folder: "", adding: true, domainAccount: false,
                [], null, name: "root@srv01.corp.local");
            Assert.Null(none.ChosenTag);
            Assert.Equal(Visibility.Collapsed, none.TagPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, none.TagLabel.Visibility);
            none.Close();

            var saved = new SavedSession { Name = "root@srv01", UserName = "root", Address = "srv01", Mode = ConnectMode.Ssh, Tag = "OLD" };
            var properties = new SessionPropertiesDialog(saved, null, [], sshAvailable: true, [], tags: tags);
            // PROD, QA, DEV, l'étiquette inconnue gardée, et « (aucune) ».
            Assert.Equal(5, properties.TagBox.Items.Count);
            Assert.Equal("OLD", properties.ChosenTag);
            properties.TagBox.SelectedIndex = 0;
            Assert.Equal("PROD", properties.ChosenTag);
            properties.TagBox.SelectedIndex = 4;
            Assert.Null(properties.ChosenTag);
            properties.Close();
        });
    }

    /// <summary>
    /// Compte de domaine : la fenêtre « Choisir le serveur » propose les serveurs déjà utilisés, cache « Garder dans Mes
    /// serveurs » pour un serveur déjà gardé, refuse un serveur hors des machines autorisées ; à l'ajout, le serveur est
    /// facultatif.
    /// </summary>
    [Fact]
    public void ServerPromptOffersKnownServersAndKeepsTheChoice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var account = new PvwaAccount
            {
                Id = "5_1", UserName = "adm-t1", Address = "corp.local", PlatformId = "WinDomain", SafeName = "T1-ADMINS",
                RemoteMachinesAccess = new RemoteMachinesAccess { RemoteMachines = "srv01.corp.local;srv02.corp.local" },
            };
            var dialog = new ServerPromptDialog(account, ["srv02.corp.local", "srv01.corp.local"], ["Prod", "Prod/Web"],
                server => server.Equals("srv02.corp.local", StringComparison.OrdinalIgnoreCase), keep: true, folder: "Prod");
            Assert.Equal("srv02.corp.local", dialog.Server);
            Assert.Equal(Strings.ServerPromptKnown, dialog.ServerHint.Text);
            Assert.Equal(Visibility.Collapsed, dialog.KeepPanel.Visibility);
            Assert.Equal(Visibility.Visible, dialog.AlreadySavedText.Visibility);
            Assert.False(dialog.Keep);

            dialog.ServerBox.Text = "srv03.corp.local";
            Assert.Equal(Visibility.Visible, dialog.KeepPanel.Visibility);
            Assert.True(dialog.Keep);
            Assert.Equal("Prod", dialog.Folder);
            Assert.True(dialog.Validate(required: true));

            dialog.ServerBox.Text = "srv 03";
            Assert.False(dialog.Validate(required: true));
            Assert.Equal(Strings.ServerPromptInvalid, dialog.ErrorText.Text);
            dialog.ServerBox.Text = "";
            Assert.False(dialog.Validate(required: true));
            Assert.True(dialog.Validate(required: false));
            dialog.Close();

            // Serveur à garder : son étiquette se choisit dans la même fenêtre, devinée d'après le serveur saisi.
            var tags = ServerTagRules.Defaults();
            var tagged = new ServerPromptDialog(account, [], ["Prod"], _ => false, keep: false, folder: "", tags: tags,
                guessTag: (server, folder) => ServerTagGuess.ForServer(tags, $"adm-t1@{server}", server, account.Address, account.SafeName,
                    folder)?.Name);
            Assert.Equal(Visibility.Visible, tagged.KeepBox.Visibility);
            Assert.Equal(Visibility.Visible, tagged.TagPanel.Visibility);
            Assert.False(tagged.SavePanel.IsEnabled);
            tagged.ServerBox.Text = "srvprd01.corp.local";
            Assert.Equal("PROD", tagged.ChosenTag);
            tagged.KeepBox.IsChecked = true;
            Assert.True(tagged.SavePanel.IsEnabled);
            Assert.True(tagged.Keep);
            tagged.ServerBox.Text = "srvdev01.corp.local";
            Assert.Equal("DEV", tagged.ChosenTag);
            tagged.Close();

            // Compte limité à ses machines : un autre serveur est refusé.
            account.RemoteMachinesAccess.AccessRestrictedToRemoteMachines = true;
            var restricted = new ServerPromptDialog(account, ["srv01.corp.local"], [], _ => false, keep: false, folder: "", adding: true);
            // Ajout : dossier demandé directement, sans case « Garder ».
            Assert.Equal(Visibility.Collapsed, restricted.KeepBox.Visibility);
            Assert.Equal(Visibility.Visible, restricted.KeepPanel.Visibility);
            Assert.Equal(Visibility.Visible, restricted.ServerPanel.Visibility);
            Assert.Equal(Strings.ServerPromptAdd, restricted.OkButton.Content);
            restricted.ServerBox.Text = "srv09.corp.local";
            Assert.False(restricted.Validate(required: false));
            Assert.Equal(Text.Format(Strings.ServerPromptRestricted, "srv01.corp.local, srv02.corp.local"), restricted.ServerHint.Text);
            Assert.Equal(Text.Format(Strings.ServerPromptNotAllowed, "srv09.corp.local"), restricted.ErrorText.Text);
            restricted.ServerBox.Text = "SRV02.corp.local";
            Assert.True(restricted.Validate(required: false));
            restricted.Close();
        });
    }

    /// <summary>
    /// « Connexion avancée » d'un compte de domaine : sans serveur, la connexion est refusée (elle viserait le domaine) ;
    /// avec un serveur, il est la machine cible.
    /// </summary>
    [Fact]
    public void AdvancedConnectionRequiresTheServerOfADomainAccount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var account = new PvwaAccount { Id = "5_2", UserName = "adm-t1", Address = "corp.example.com", PlatformId = "AD-Admins-T1", SafeName = "T1" };
            var dialog = new ConnectDialog(account, new ConnectRequest(ConnectMode.Psm, "PSM-RDP"), new AppSettings(), "jdupont", null,
                requireMachine: true);
            Assert.Equal("", dialog.MachineBox.Text);
            Assert.False(dialog.Accept());
            Assert.Equal(Strings.ServerPromptRequired, dialog.ErrorText.Text);
            Assert.Null(dialog.Result);

            dialog.MachineBox.Text = "srv01.corp.example.com";
            Assert.True(dialog.Accept());
            Assert.Equal("srv01.corp.example.com", dialog.Result?.RemoteMachine);
            dialog.Close();

            // Compte d'un serveur : la machine cible reste facultative.
            var server = new PvwaAccount { Id = "5_3", UserName = "admin", Address = "srv02.corp.example.com", PlatformId = "WinServerLocal" };
            var direct = new ConnectDialog(server, new ConnectRequest(ConnectMode.Psm, "PSM-RDP"), new AppSettings(), "jdupont", null);
            Assert.True(direct.Accept());
            Assert.Null(direct.Result?.RemoteMachine);
            direct.Close();

            // Liste de machines sans y être limité : rien de proposé d'office, la session peut viser l'adresse du compte.
            server.RemoteMachinesAccess = new RemoteMachinesAccess { RemoteMachines = "srv03.corp.example.com;srv04.corp.example.com" };
            var listed = new ConnectDialog(server, new ConnectRequest(ConnectMode.Psm, "PSM-RDP"), new AppSettings(), "jdupont", null);
            Assert.Equal("", listed.MachineBox.Text);
            Assert.True(listed.Accept());
            Assert.Null(listed.Result?.RemoteMachine);
            listed.Close();

            // Limité à ces machines : la première est proposée, et il en faut une.
            server.RemoteMachinesAccess.AccessRestrictedToRemoteMachines = true;
            var restricted = new ConnectDialog(server, new ConnectRequest(ConnectMode.Psm, "PSM-RDP"), new AppSettings(), "jdupont", null);
            Assert.Equal("srv03.corp.example.com", restricted.MachineBox.Text);
            restricted.MachineBox.Text = "";
            Assert.False(restricted.Accept());
            restricted.Close();
        });
    }

    /// <summary>
    /// Question RADIUS : « Retour » (Échap) réaffiche les identifiants sans rien envoyer au PVWA, et « Quitter » redevient
    /// la touche Échap.
    /// </summary>
    [Fact]
    public void RadiusQuestionCanGoBackToTheCredentials()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var login = new LoginWindow(new AppSettings { PvwaUrl = "pvwa.corp.local", UserName = "jdoe" });
            Assert.Equal(Visibility.Collapsed, login.BackButton.Visibility);
            Assert.True(login.QuitButton.IsCancel);
            // Hors de la question, Échap reste à « Quitter ».
            Assert.False(login.EscapeGoesBack());

            login.ShowChallenge("Code OTP ?");
            Assert.Equal(Visibility.Collapsed, login.CredentialsPanel.Visibility);
            Assert.Equal(Visibility.Visible, login.BackButton.Visibility);
            // « Retour » n'est pas un bouton d'annulation : un clic fermerait la fenêtre de connexion, donc ZillaTerm.
            Assert.False(login.BackButton.IsCancel);

            Assert.True(login.EscapeGoesBack());
            Assert.Equal(Visibility.Visible, login.CredentialsPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, login.ChallengePanel.Visibility);
            Assert.Equal(Visibility.Collapsed, login.BackButton.Visibility);
            Assert.True(login.LanguageBox.IsEnabled);
            Assert.Null(login.Client);

            login.ShowChallenge("Code OTP ?");
            login.BackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, login.CredentialsPanel.Visibility);
            login.Close();
        });
    }

    /// <summary>
    /// « Connexion avancée » après un refus du composant : « Retenir » n'est pas cochée d'office, et un nom de composant
    /// que les Paramètres refuseraient (espace…) est refusé ici aussi, l'erreur sur le champ.
    /// </summary>
    [Fact]
    public void AdvancedConnectionRefusesAnInvalidComponent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var account = new PvwaAccount { Id = "5_3", UserName = "admin", Address = "srv01.corp.local", PlatformId = "WinServerLocal" };
            var dialog = new ConnectDialog(account, new ConnectRequest(ConnectMode.Psm, "PSM-RDP"), new AppSettings(), "jdoe",
                "EPVWA093E", componentError: true);
            Assert.False(dialog.RememberBox.IsChecked);

            dialog.ComponentBox.Text = "PSM RDP";
            dialog.RememberBox.IsChecked = true;
            Assert.False(dialog.Accept());
            Assert.Equal(Strings.InvalidComponentName, dialog.ErrorText.Text);
            Assert.Null(dialog.Result);

            dialog.ComponentBox.Text = "WIN-PSM";
            Assert.True(dialog.Accept());
            Assert.Equal(("WIN-PSM", true), (dialog.Result?.Component, dialog.Result?.RememberComponent));
            dialog.Close();
        });
    }

    /// <summary>
    /// « Options avancées » : les deux champs du ticket ont chacun un nom pour les lecteurs d'écran, et la colonne des
    /// étiquettes suit la longueur des textes (italien) au lieu d'une largeur fixe.
    /// </summary>
    [Fact]
    public void AdvancedOptionsNameTheTicketFieldsAndFitTheLabels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var account = new PvwaAccount { Id = "5_3", UserName = "admin", Address = "srv01.corp.local", PlatformId = "WinServerLocal" };
            var dialog = new ConnectDialog(account, new ConnectRequest(ConnectMode.Psm, "PSM-RDP"), new AppSettings(), "jdoe", null);
            Assert.Equal(Strings.TicketSystemTip, System.Windows.Automation.AutomationProperties.GetName(dialog.TicketSystemBox));
            Assert.Equal(Strings.TicketIdTip, System.Windows.Automation.AutomationProperties.GetName(dialog.TicketIdBox));
            var fields = Assert.IsType<System.Windows.Controls.Grid>(dialog.ComponentBox.Parent);
            Assert.Equal(GridUnitType.Auto, fields.ColumnDefinitions[0].Width.GridUnitType);
            Assert.Same(fields, dialog.RememberBox.Parent);
            Assert.Equal(1, System.Windows.Controls.Grid.GetColumn(dialog.RememberBox));
            dialog.Close();
        });
    }

    /// <summary>
    /// Transfert X11 : proposé pour une session SSH seulement (grisé pour le PSM et les fichiers seuls, avec la raison),
    /// repris de la requête et rendu dans le résultat ; de même dans les propriétés d'un serveur de « Mes serveurs ». Case
    /// cochée et active : sa conséquence (« ⚠ Effet : … ») est écrite dessous, visible sans survol.
    /// </summary>
    [Fact]
    public void X11ForwardingIsForSshSessionsOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var account = new PvwaAccount { Id = "5_4", UserName = "root", Address = "srv01.corp.local", PlatformId = "UnixSSH" };
            var settings = new AppSettings { PsmpAddress = "psmp.corp.local" };
            var dialog = new ConnectDialog(account, new ConnectRequest(ConnectMode.Ssh, "PSM-RDP", X11Forwarding: true), settings, "jdoe", null);
            Assert.True(dialog.X11Box.IsEnabled);
            Assert.True(dialog.X11Box.IsChecked);
            Assert.Equal(Strings.X11ForwardingTip, dialog.X11Box.ToolTip);
            Assert.Equal(Visibility.Visible, dialog.X11Effect.Visibility);
            Assert.Equal(Strings.X11ForwardingEffect, dialog.X11Effect.Text);
            Assert.True(dialog.Accept());
            Assert.True(dialog.Result?.X11Forwarding);
            dialog.X11Box.IsChecked = false;
            Assert.Equal(Visibility.Collapsed, dialog.X11Effect.Visibility);
            dialog.X11Box.IsChecked = true;

            dialog.SftpRadio.IsChecked = true;
            Assert.False(dialog.X11Box.IsEnabled);
            Assert.Equal(Strings.X11SshOnly, dialog.X11Box.ToolTip);
            Assert.Equal(Visibility.Collapsed, dialog.X11Effect.Visibility);
            Assert.True(dialog.Accept());
            Assert.False(dialog.Result?.X11Forwarding);
            dialog.Close();

            var saved = new SavedSession { AccountId = "5_4", Name = "root@srv01.corp.local", Mode = ConnectMode.Ssh, UserName = "root", Address = "srv01.corp.local" };
            var properties = new SessionPropertiesDialog(saved, account, [], sshAvailable: true, [], x11: true);
            Assert.True(properties.X11Box.IsEnabled);
            Assert.True(properties.X11Box.IsChecked);
            Assert.Equal(Visibility.Visible, properties.X11Effect.Visibility);
            properties.PsmRadio.IsChecked = true;
            Assert.False(properties.X11Box.IsEnabled);
            Assert.Equal(Visibility.Collapsed, properties.X11Effect.Visibility);
            Assert.Equal(Strings.X11SshOnly, properties.X11Box.ToolTip);
            properties.Close();
        });
    }

    /// <summary>
    /// Entrée KeePass : une nouvelle entrée sans adresse est refusée, une entrée qui avait une adresse la garde ; une entrée
    /// existante sans adresse (mot de passe seul) se modifie, avec un titre.
    /// </summary>
    [Fact]
    public void KeePassEntryWithoutAddressStaysEditable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            (bool? Saved, string Error) Save(ZillaTerm.Core.KeePass.KeePassEntry? entry, Action<KeePassEntryDialog> edit)
            {
                var dialog = new KeePassEntryDialog("Coffre", [], entry, "");
                string error = "";
                Exception? failure = null;
                dialog.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        edit(dialog);
                        dialog.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        if (dialog.IsVisible)
                        {
                            error = dialog.ErrorText.Text;
                            dialog.Close();
                        }
                    }
                    catch (Exception e)
                    {
                        failure = e;
                        dialog.Close();
                    }
                });
                var saved = dialog.ShowDialog();
                if (failure is not null)
                {
                    ExceptionDispatchInfo.Throw(failure);
                }

                return (saved, error);
            }

            var passwordOnly = new ZillaTerm.Core.KeePass.KeePassEntry { Id = "a", Title = "Partage", HasPassword = true };
            var server = new ZillaTerm.Core.KeePass.KeePassEntry { Id = "b", Title = "srv01", Url = "ssh://srv01" };

            Assert.Equal((false, Strings.KeePassAddressRequired), Save(null, d => d.TitleBox.Text = "Nouveau"));
            Assert.Equal((true, ""), Save(passwordOnly, d => d.TitleBox.Text = "Partage équipe"));
            Assert.Equal((false, Strings.KeePassTitleRequired), Save(passwordOnly, d => d.TitleBox.Text = ""));
            Assert.Equal((false, Strings.KeePassAddressRequired), Save(server, d => d.UrlBox.Text = ""));
            Assert.Equal((true, ""), Save(server, d => d.UrlBox.Text = "ssh://srv02"));
        });
    }

    /// <summary>Récapitulatif d'un environnement : « Réglage : valeur » pour un ajout, « ancienne → nouvelle » sinon ; oui / non lisibles.</summary>
    [Fact]
    public void EnvironmentRecapLines()
    {
        Assert.Equal(Text.Format(Strings.EnvLineNew, Strings.EnvDefaultPsmp, "psmp.corp.com:22"),
            EnvironmentImport.Line(new EnvironmentChange(EnvironmentSetting.DefaultPsmp, null, "", "psmp.corp.com:22", true)));
        Assert.Equal(Text.Format(Strings.EnvLineChange, Text.Format(Strings.EnvPlatformComponent, "WinDomain"), "PSM-RDP", "WIN-PSM"),
            EnvironmentImport.Line(new EnvironmentChange(EnvironmentSetting.PlatformComponent, "WinDomain", "PSM-RDP", "WIN-PSM", false)));
        Assert.Equal(Text.Format(Strings.EnvLineChange, Strings.EnvSshInApp, Strings.EnvYes, Strings.EnvNo),
            EnvironmentImport.Line(new EnvironmentChange(EnvironmentSetting.SshInApp, null, "true", "false", false)));
    }

    /// <summary>Onglet détaché : le terminal passe dans la fenêtre séparée, puis en ressort pour revenir dans l'onglet.</summary>
    [Fact]
    public void DetachedWindowHoldsTheTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var connector = new ZillaTerm.Core.Ssh.SshConnector("127.0.0.1", 22, "root", new NoInteraction());
            var session = new ZillaTerm.App.Services.SshSession(null, "root@srv01", connector,
                System.Windows.Threading.Dispatcher.CurrentDispatcher, followTerminal: false, saved: null);
            var view = new SshSessionView(session, "root@srv01", "…");

            var window = new DetachedSessionWindow(view, session.Label);
            Assert.Same(view, window.Content);
            Assert.Contains("root@srv01", window.Title);

            Assert.Same(view, window.TakeView());
            Assert.Null(window.Content);
            window.SessionClosed = true;
            window.Close();
            session.Dispose();
        });
    }

    private static (ZillaTerm.App.Services.SshSession Session, SshSessionView View) NewSshView(string label)
    {
        var connector = new ZillaTerm.Core.Ssh.SshConnector("127.0.0.1", 22, "root", new NoInteraction());
        var session = new ZillaTerm.App.Services.SshSession(null, label, connector,
            System.Windows.Threading.Dispatcher.CurrentDispatcher, followTerminal: false, saved: null);
        return (session, new SshSessionView(session, label, "…"));
    }

    /// <summary>Vue parallèle : grille, saisie simultanée vers les sessions cochées, encodage par session, collage, limite.</summary>
    [Fact]
    public void ParallelViewSendsTypingToTheTickedSessions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var views = Enumerable.Range(1, 9).Select(i => NewSshView($"root@srv0{i}")).ToList();
            var sent = new List<(string Label, string Text)>();
            (int Lines, int Sessions)? asked = null;
            bool answer = false;
            var parallel = new ParallelView
            {
                IsConnected = _ => true,
                Deliver = (view, input) => sent.Add((view.Session.Label, input.Encode(view.Session.Emulator))),
                ConfirmPaste = (lines, sessions) =>
                {
                    asked = (lines, sessions);
                    return answer;
                },
            };
            foreach (var (_, view) in views.Take(3))
            {
                parallel.Add(view);
            }

            Assert.Equal((1, 3), (parallel.PanesGrid.RowDefinitions.Count, parallel.PanesGrid.ColumnDefinitions.Count));
            Assert.Equal(Visibility.Collapsed, parallel.BroadcastBanner.Visibility);
            var (s1, v1) = views[0];
            var (s2, v2) = views[1];
            var (s3, v3) = views[2];

            // Sans saisie simultanée : seulement la session où l'on tape.
            v1.InputRouter!(v1, TerminalInput.Typed("uptime\r"));
            Assert.Equal([("root@srv01", "uptime\r")], sent);

            // Avec : toutes les sessions cochées, chacune avec l'encodage de son terminal (vim dans srv02).
            parallel.Broadcast = true;
            Assert.Equal(Visibility.Visible, parallel.BroadcastBanner.Visibility);
            Assert.Contains("root@srv03", parallel.BroadcastText.Text);
            s2.Emulator.Feed("\x1b[?1h");
            sent.Clear();
            v1.InputRouter!(v1, TerminalInput.Special(ZillaTerm.Core.Terminal.TerminalKey.Up));
            Assert.Equal([("root@srv01", "\x1b[A"), ("root@srv02", "\x1bOA"), ("root@srv03", "\x1b[A")], sent);

            // Session décochée : exclue, et ce qu'on y tape ne va qu'à elle.
            parallel.SetIncluded(s3, false);
            Assert.DoesNotContain("root@srv03", parallel.BroadcastText.Text);
            sent.Clear();
            v2.InputRouter!(v2, TerminalInput.Typed("id\r"));
            Assert.Equal(["root@srv01", "root@srv02"], sent.Select(x => x.Label));
            sent.Clear();
            v3.InputRouter!(v3, TerminalInput.Typed("w\r"));
            Assert.Equal(["root@srv03"], sent.Select(x => x.Label));

            // Collage de plusieurs lignes vers plusieurs sessions : confirmation ; refusé, rien n'est envoyé.
            sent.Clear();
            v1.InputRouter!(v1, TerminalInput.Pasted("cd /tmp\nls"));
            Assert.Equal((2, 2), asked);
            Assert.Empty(sent);
            answer = true;
            v1.InputRouter!(v1, TerminalInput.Pasted("cd /tmp\nls"));
            Assert.Equal(2, sent.Count);

            // Ajoutée pendant la saisie simultanée : pas cochée.
            parallel.Add(views[3].View);
            Assert.False(parallel.IsIncluded(views[3].Session));
            Assert.Equal((2, 2), (parallel.PanesGrid.RowDefinitions.Count, parallel.PanesGrid.ColumnDefinitions.Count));

            // Au plus 8 sessions.
            foreach (var (_, view) in views.Skip(4))
            {
                parallel.Add(view);
            }

            Assert.Equal(8, parallel.Sessions.Count);
            Assert.True(parallel.IsFull);
            Assert.False(parallel.Contains(views[8].Session));

            // Une seule session à l'écran, puis toutes.
            parallel.ToggleZoom(s2);
            Assert.Equal((1, 1), (parallel.PanesGrid.RowDefinitions.Count, parallel.PanesGrid.ColumnDefinitions.Count));
            parallel.ToggleZoom(s2);
            Assert.Equal((2, 4), (parallel.PanesGrid.RowDefinitions.Count, parallel.PanesGrid.ColumnDefinitions.Count));

            // Retirée : le terminal est rendu, sans aiguillage de la saisie.
            Assert.Same(v1, parallel.Remove(s1));
            Assert.Null(v1.InputRouter);
            Assert.Null(v1.Parent);
            Assert.Equal(7, parallel.Sessions.Count);

            foreach (var (session, _) in views)
            {
                session.Dispose();
            }
        });
    }

    /// <summary>Choix des sessions de la vue parallèle : au plus 8 cochées.</summary>
    [Fact]
    public void ParallelDialogKeepsAtMostEightSessions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var sessions = Enumerable.Range(1, 9).Select(i => NewSshView($"root@srv0{i}").Session).ToList();
            var dialog = new ParallelDialog(sessions, sessions);
            Assert.Equal(sessions.Take(8), dialog.Selected);
            var boxes = dialog.SessionsPanel.Children.OfType<System.Windows.Controls.CheckBox>().ToList();
            Assert.False(boxes[8].IsEnabled);
            Assert.Contains("8", dialog.CountText.Text);
            dialog.Close();

            dialog = new ParallelDialog(sessions, [sessions[2]]);
            Assert.Equal([sessions[2]], dialog.Selected);
            Assert.True(dialog.SessionsPanel.Children.OfType<System.Windows.Controls.CheckBox>().All(b => b.IsEnabled));
            dialog.Close();
            foreach (var session in sessions)
            {
                session.Dispose();
            }
        });
    }

    /// <summary>Comparaison : lignes côte à côte, navigation, seulement les différences ; binaire par somme SHA-256.</summary>
    [Fact]
    public void CompareWindowShowsTheDifferences()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var left = System.Text.Encoding.UTF8.GetBytes("listen 80;\nserver_name a;\nroot /var/www;\n" + string.Concat(Enumerable.Range(1, 30).Select(i => $"l{i}\n")) + "}\n");
            var right = System.Text.Encoding.UTF8.GetBytes("listen 443;\nserver_name a;\nroot /var/www;\n" + string.Concat(Enumerable.Range(1, 30).Select(i => $"l{i}\n")) + "ssl on;\n}\n");
            var window = new CompareWindow("root@srv01 : /etc/app.conf", left, "root@srv02 : /etc/app.conf", right, new AppSettings());
            Assert.Contains("app.conf", window.Title);
            Assert.Equal(2, window.Result!.Blocks);
            Assert.Contains("2", window.SummaryText.Text);
            Assert.Equal(Visibility.Collapsed, window.ToolButton.Visibility);

            window.GoToDifference(1);
            Assert.Equal(0, window.RowsList.SelectedIndex);
            window.GoToDifference(1);
            Assert.Equal(DiffKind.Added, ((DiffRow)window.RowsList.SelectedItem).Kind);

            window.OnlyDiffBox.IsChecked = true;
            window.OnlyDiffBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Contains(((IEnumerable<DiffRow>)window.RowsList.ItemsSource), r => r.Kind == DiffKind.Gap);
            window.Close();
            Assert.All(left, b => Assert.Equal(0, b));   // contenu effacé de la mémoire à la fermeture

            var binary = new CompareWindow("a", [1, 0, 2], "b", [1, 0, 2], new AppSettings { CompareTool = @"C:\Tools\WinMergeU.exe" });
            Assert.Equal(Visibility.Visible, binary.BinaryPanel.Visibility);
            Assert.Equal(Strings.CompareIdentical, binary.SummaryText.Text);
            Assert.Equal(Visibility.Visible, binary.ToolButton.Visibility);
            binary.Close();
        });
    }

    /// <summary>Envoi vers plusieurs serveurs, choix d'une comparaison, choix de serveurs, À propos : les fenêtres s'ouvrent.</summary>
    [Fact]
    public void MultiServerWindowsOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var (s1, _) = NewSshView("root@srv01");
            var (s2, _) = NewSshView("root@srv02");
            var upload = new MultiUploadDialog([s1, s2], [s1], "~/deploy", "SCP");
            Assert.Equal("~/deploy", upload.Destination);
            // Sessions non connectées : on ne peut pas les choisir.
            Assert.Empty(upload.Sessions);
            Assert.All(upload.ServersPanel.Children.OfType<System.Windows.Controls.CheckBox>(), b => Assert.False(b.IsEnabled));
            var file = Path.GetTempFileName();
            upload.AddPath(file);
            upload.AddPath(file);
            Assert.Single(upload.Paths);
            File.Delete(file);
            upload.Close();

            var compare = new CompareDialog("root@srv01 : /etc/app.conf", "/etc/app.conf", [s1, s2], s1);
            Assert.Equal("/etc/app.conf", compare.RemoteFile);
            // Aucun autre serveur connecté : un fichier de ce poste est proposé (le même chemin donnerait le même fichier).
            Assert.True(compare.LocalRadio.IsChecked);
            compare.ServerRadio.IsChecked = true;
            Assert.True(compare.BrowseServerButton.IsEnabled);
            compare.Close();

            var choose = new ParallelDialog(["web01", "web02", "web03"], [0, 1, 2], 2, "intro", Strings.ParallelOpen);
            Assert.Equal([0, 1], choose.SelectedIndexes);
            Assert.Equal("intro", choose.IntroText.Text);
            choose.Close();

            var about = new AboutWindow(new AppSettings(), () => { });
            Assert.Contains(UpdateChecker.CurrentVersion.ToString(), about.VersionText.Text);
            // Logo net : une image du .ico au moins aussi grande que l'affichage (pas celle de 16 px agrandie).
            Assert.True(((System.Windows.Media.Imaging.BitmapSource)about.LogoImage.Source).PixelWidth >= about.LogoImage.Width);
            about.Close();

            s1.Dispose();
            s2.Dispose();
        });
    }

    /// <summary>
    /// Explorateur d'un serveur (Comparer → Parcourir) : s'ouvre sur le dossier du même chemin, ou son plus proche
    /// parent lisible, fichier présélectionné ; on y navigue et on choisit un fichier.
    /// </summary>
    [Fact]
    public void RemoteFilePickerOpensOnTheSamePathAndReturnsTheChosenFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            static RemoteEntry Entry(string path, bool directory = false) =>
                new(RemotePath.Name(path), path, directory, false, directory ? 0 : 1234, new DateTime(2026, 10, 6, 9, 0, 0), "");
            var tree = new Dictionary<string, List<RemoteEntry>>
            {
                ["/opt/app"] = [Entry("/opt/app/conf", directory: true), Entry("/opt/app/run.sh")],
                ["/opt/app/conf"] = [Entry("/opt/app/conf/app.yml"), Entry("/opt/app/conf/logging.xml")],
            };
            var listed = new List<string>();
            Task<List<RemoteEntry>> List(string directory, bool hidden, CancellationToken ct)
            {
                listed.Add(directory);
                return tree.TryGetValue(directory, out var entries)
                    ? Task.FromResult(entries)
                    : Task.FromException<List<RemoteEntry>>(new IOException("No such file"));
            }

            // Même chemin que le fichier comparé : son dossier s'ouvre, le fichier est sélectionné.
            var picker = new RemoteFileDialog("root@srv02", "/opt/app/conf/app.yml", "/root", List);
            picker.StartAsync().GetAwaiter().GetResult();
            Assert.Equal("/opt/app/conf", picker.CurrentDirectory);
            Assert.Equal("app.yml", ((RemoteEntry?)picker.FileList.SelectedItem)?.Name);
            Assert.True(picker.ChooseButton.IsEnabled);
            Assert.Equal("..", ((RemoteEntry)picker.FileList.Items[0]).Name);
            picker.Close();

            // Dossier absent sur ce serveur : le plus proche parent lisible ; « .. » puis un fichier.
            picker = new RemoteFileDialog("root@srv02", "/opt/app/old/app.yml", "/root", List);
            listed.Clear();
            picker.StartAsync().GetAwaiter().GetResult();
            Assert.Equal(["/opt/app/old", "/opt/app"], listed);
            Assert.Equal("/opt/app", picker.CurrentDirectory);
            Assert.Null(picker.FileList.SelectedItem);
            picker.OpenAsync(Entry("/opt/app/conf", directory: true)).GetAwaiter().GetResult();
            picker.OpenAsync(RemoteEntry.ParentLink("/opt/app/conf")).GetAwaiter().GetResult();
            Assert.Equal("conf", ((RemoteEntry?)picker.FileList.SelectedItem)?.Name);
            Assert.False(picker.ChooseButton.IsEnabled);
            picker.OpenAsync(Entry("/opt/app/run.sh")).GetAwaiter().GetResult();
            Assert.Equal("/opt/app/run.sh", picker.SelectedPath);

            // Chemin saisi : un fichier ouvre son dossier et le sélectionne ; « ~ » est le dossier personnel.
            picker.GoToAsync("/opt/app/conf/logging.xml").GetAwaiter().GetResult();
            Assert.Equal(("/opt/app/conf", "logging.xml"), (picker.CurrentDirectory, ((RemoteEntry?)picker.FileList.SelectedItem)?.Name));
            listed.Clear();
            picker.GoToAsync("~").GetAwaiter().GetResult();
            Assert.Equal(["/root", "/"], listed);
            Assert.Contains("/", picker.StatusText.Text);
            picker.Close();
        });
    }

    /// <summary>Historique d'une liste partagée : journal (le plus récent d'abord) et versions avec leurs auteurs.</summary>
    [Fact]
    public void SharedListHistoryShowsTheChangesAndTheVersions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("cat-shared-").FullName;
        try
        {
            var list = SharedServerList.Create(Path.Combine(directory, "Équipe.json"), "Équipe", "pvwa", "alice");
            list.Add([Server("1", "web01", "Prod"), Server("2", "db01")], "alice");
            list.Remove([list.Content!.Servers.Single(s => s.Name == "db01").Id], "bob");

            RunWithTheme(() =>
            {
                UseDispatcherContext();
                var restored = new List<int>();
                var dialog = new SharedHistoryDialog(new SharedServerList(list.Path), version =>
                {
                    restored.Add(version.Revision);
                    return Task.FromResult<int?>(0);
                });
                Pump(dialog.LoadAsync());

                Assert.Equal(
                    [$"{Strings.SharedActionRemoved} db01 bob", $"{Strings.SharedActionAdded} db01 alice",
                     $"{Strings.SharedActionAdded} web01 alice", $"{Strings.SharedActionCreated} Équipe alice"],
                    dialog.Changes.Select(c => $"{c.Action} {c.Server} {c.By}"));
                Assert.Equal([2, 1], dialog.Versions.Select(v => v.Revision));
                Assert.Equal(("alice", "+ web01, + db01"), (dialog.Versions[0].By, dialog.Versions[0].Summary));
                Assert.Contains("3", dialog.HeaderText.Text);
                Assert.False(dialog.RestoreButton.IsEnabled);
                dialog.VersionsGrid.SelectedIndex = 0;
                Assert.True(dialog.RestoreButton.IsEnabled);
                Assert.Empty(restored);
                dialog.Close();

                // Restauration en cours (partage lent) : choisir une autre version ne réactive pas « Restaurer ».
                var pending = new TaskCompletionSource<int?>();
                var slow = new SharedHistoryDialog(new SharedServerList(list.Path), _ => pending.Task);
                Pump(slow.LoadAsync());
                slow.VersionsGrid.SelectedIndex = 1;
                var restoring = slow.RestoreAsync(slow.Versions[1]);
                Assert.False(slow.RestoreButton.IsEnabled);
                slow.VersionsGrid.SelectedIndex = 0;
                Assert.False(slow.RestoreButton.IsEnabled);
                pending.SetResult(null);
                Pump(restoring);
                Assert.True(slow.RestoreButton.IsEnabled);
                slow.Close();
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Fenêtre principale avant le chargement des comptes : connexions récentes grisées, liste partagée lue en
    /// arrière-plan et affichée avant « Mes serveurs », menus « Importer » et « Partager » dans l'onglet, aucun serveur
    /// signalé introuvable tant que les comptes ne sont pas chargés.
    /// </summary>
    [Fact]
    public void MainWindowShowsSharedListsAndWaitsForTheAccounts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("cat-shared-").FullName;
        try
        {
            var list = SharedServerList.Create(Path.Combine(directory, "Équipe.json"), "Équipe", "pvwa.test", "alice");
            list.Add([Server("1", "web01", "Prod"), Server("2", "db01")], "alice");
            var settings = new AppSettings { KeepPvwaSessionAlive = false, SharedLists = [list.Path] };
            settings.Recent.Add(new RecentSession { AccountId = "1", PvwaHost = "pvwa.test", Label = "root@web01", Mode = "PSM-RDP", When = DateTime.Now });
            // Même ID de compte sur un autre PVWA : pas affichée ici.
            settings.Recent.Add(new RecentSession { AccountId = "1", PvwaHost = "other.test", Label = "admin@dc01", Mode = "PSM-RDP", When = DateTime.Now });
            settings.Sessions.Add(new SavedSession { AccountId = "3", PvwaHost = "pvwa.test", Name = "root@app01" });

            RunWithTheme(() =>
            {
                UseDispatcherContext();
                using var client = new PvwaClient(new Uri("https://pvwa.test"), new System.Net.Http.HttpClientHandler());
                var window = new MainWindow(client, settings, "jdoe", "jdoe", new Services.KeePass.KeePassManager());

                Assert.False(window.RecentList.IsEnabled);
                Assert.Equal("root@web01", Assert.Single(Assert.IsAssignableFrom<IEnumerable<RecentSession>>(window.RecentList.ItemsSource)).Label);
                Assert.Equal(Visibility.Visible, window.RecentLoadingText.Visibility);
                Assert.Equal(Visibility.Visible, window.ImportMenuButton.Visibility);
                Assert.Equal(Visibility.Visible, window.ShareMenuButton.Visibility);
                Assert.Equal(Visibility.Collapsed, window.AddKeePassButton.Visibility);
                // Imports distingués par leur libellé : fichier ZillaTerm, autre logiciel, liste partagée, base KeePass.
                Assert.Equal([Strings.MenuImportServerFile, Strings.MenuImportOtherTool, Strings.MenuOpenSharedList, Strings.MenuKeePassAdd],
                    window.ImportMenuButton.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => (string)i.Header));
                Assert.Equal([Strings.MenuExportMyServers, Strings.MenuCreateSharedList],
                    window.ShareMenuButton.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => (string)i.Header));

                PumpUntil(() => window.SavedTree.ItemsSource is IEnumerable<object> items && items.OfType<SharedListNode>().Any(n => n.IsReadable));
                var nodes = ((IEnumerable<object>)window.SavedTree.ItemsSource).ToList();
                var shared = Assert.IsType<SharedListNode>(nodes[0]);
                Assert.Equal(("Équipe", " (2)"), (shared.Name, shared.StateText));
                Assert.Equal("Prod", Assert.IsType<SharedFolderNode>(shared.Children[0]).Name);
                var db = Assert.IsType<SharedServerNode>(shared.Children[1]);
                Assert.Equal("db01", db.Title);
                Assert.False(db.IsMissing);
                Assert.DoesNotContain(Strings.MissingInCyberArk, db.ToString());
                Assert.Contains("alice", db.Details);
                Assert.Equal("root@app01", Assert.IsType<SavedSessionNode>(nodes[1]).Title);
                window.StopWatchingSharedLists();
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// « Retirer le dossier » d'une liste partagée pendant une recherche qui n'en montre qu'un serveur : tous les serveurs
    /// du dossier (sous-dossiers compris) sont retirés, pas seulement celui affiché ; un serveur seul reste seul.
    /// </summary>
    [Fact]
    public void RemovingASharedFolderTakesTheServersHiddenBySearch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("cat-shared-").FullName;
        try
        {
            var list = SharedServerList.Create(Path.Combine(directory, "Équipe.json"), "Équipe", "pvwa.test", "alice");
            list.Add([Server("1", "web01", "Recette"), Server("2", "db01", "Recette"), Server("3", "app01", "Recette/Interne"),
                Server("4", "web02", "Prod")], "alice");
            var web01 = list.Content!.Servers.Single(s => s.Name == "web01");
            var shown = new SharedServerNode(list, web01, web01.ToSession("pvwa.test", keepId: true), null);
            var folder = new SharedFolderNode(list, "Recette", [shown], isExpanded: true);

            Assert.Equal(["app01", "db01", "web01"], MainWindow.SharedRemovalEntries(folder).Select(s => s.Name).Order());
            Assert.Equal(["web01"], MainWindow.SharedRemovalEntries(shown).Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServerEntry Server(string account, string name, string folder = "") =>
        new() { AccountId = account, Name = name, Folder = folder, Address = name, UserName = "root", PlatformId = "UnixSSH" };

    /// <summary>Session de fichiers seuls (entrée KeePass FTP) : état dans l'onglet, fichiers dans le panneau, pas de terminal.</summary>
    [Fact]
    public void FilesSessionShowsItsStateAndTheFilesPanelBrowsesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            UseDispatcherContext();
            var attempts = new Queue<TaskCompletionSource<IRemoteFiles>>([new(), new()]);
            var pending = attempts.ToArray();
            var session = new ZillaTerm.App.Services.FilesSession("ftp-test · KeePass", "FTPES", _ => attempts.Dequeue().Task,
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var view = new FilesSessionView(session, "alice@ftp.test:21");
            var panel = new FileBrowserPanel();
            panel.Attach(session);
            Assert.Equal(Visibility.Collapsed, panel.FollowBox.Visibility);

            var first = view.ConnectAsync();
            Assert.Equal(Strings.FilesSessionConnecting, view.StateText.Text);
            Assert.Equal(Text.Format(Strings.FilesConnectingBrowse, "FTPES"), panel.MessageText.Text);
            Assert.False(view.ShowFilesButton.IsEnabled);

            pending[0].SetException(new IOException("certificat refusé"));
            Pump(first);
            Assert.Equal(ZillaTerm.App.Services.RemoteSessionState.Failed, session.State);
            Assert.Contains("certificat refusé", view.StateText.Text);
            Assert.Equal(Visibility.Visible, view.ReconnectButton.Visibility);
            Assert.Equal(Strings.FilesClosedBrowse, panel.MessageText.Text);

            var files = new FakeFiles("/srv/ftp", [
                new RemoteEntry("logs", "/srv/ftp/logs", true, false, 0, new DateTime(2026, 10, 6, 9, 0, 0), ""),
                new RemoteEntry("app.conf", "/srv/ftp/app.conf", false, false, 120, new DateTime(2026, 10, 6, 9, 0, 0), ""),
            ]);
            var second = view.ConnectAsync();
            Assert.Equal(Text.Format(Strings.FilesConnectingBrowse, "FTPES"), panel.MessageText.Text);
            pending[1].SetResult(files);
            Pump(second);
            Assert.Equal(ZillaTerm.App.Services.RemoteSessionState.Connected, session.State);
            Assert.Equal(Strings.FilesSessionConnected, view.StateText.Text);
            Assert.True(view.ShowFilesButton.IsEnabled);
            Assert.Equal(Visibility.Collapsed, view.ReconnectButton.Visibility);
            Assert.Equal(Visibility.Collapsed, view.CleartextBanner.Visibility);
            PumpUntil(() => panel.PathBox.Text == "/srv/ftp");
            Assert.Contains(panel.FileList.Items.OfType<RemoteEntry>(), e => e.Name == "app.conf");

            session.Dispose();
            PumpUntil(() => files.Disposed);
        });
    }

    /// <summary>
    /// Onglet fermé pendant le renvoi d'un fichier modifié : la connexion n'est fermée qu'une fois l'écriture finie (coupée,
    /// elle laisserait le fichier du serveur tronqué), et ce renvoi est signalé à part des fichiers non renvoyés.
    /// </summary>
    [Fact]
    public void ClosingASessionWaitsForTheEditedFileBeingWritten()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "zt-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RunWithTheme(() =>
            {
                UseDispatcherContext();
                var opened = new DateTime(2026, 10, 6, 9, 0, 0);
                var written = new TaskCompletionSource<(DateTime, long)>();
                bool writing = false;
                var files = new FakeFiles("/etc/nginx", [])
                {
                    Stat = (opened, 3),
                    Write = (_, _) =>
                    {
                        writing = true;
                        return written.Task;
                    },
                };
                var session = new ZillaTerm.App.Services.FilesSession("srv01.corp.local", "SFTP", _ => Task.FromResult<IRemoteFiles>(files),
                    System.Windows.Threading.Dispatcher.CurrentDispatcher);
                Pump(session.ConnectAsync());
                var editor = new ZillaTerm.App.Services.RemoteEditor(session, new Window(), new AppSettings(), (_, _) => { }, _ => { });
                session.Editor = editor;
                var local = Path.Combine(folder, "nginx.conf");
                File.WriteAllText(local, "old");
                using var file = new EditedFile("/etc/nginx/nginx.conf", local, opened, 3);
                File.WriteAllText(local, "new content");

                var upload = editor.UploadAsync(file);
                PumpUntil(() => writing);
                Assert.Equal(["srv01.corp.local:/etc/nginx/nginx.conf"], ZillaTerm.App.Services.RemoteEditor.WritingFiles([editor]));
                Assert.False(editor.WritesDone.IsCompleted);

                session.Dispose();
                var later = Task.Delay(300);
                PumpUntil(() => later.IsCompleted);
                Assert.False(files.Disposed);

                written.SetResult((opened.AddMinutes(1), 11));
                Pump(upload);
                Assert.True(upload.Result);
                Assert.True(editor.WritesDone.IsCompleted);
                Assert.Empty(ZillaTerm.App.Services.RemoteEditor.WritingFiles([editor]));
                PumpUntil(() => files.Disposed);
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Fichiers d'un serveur en mémoire : une liste fixe, le reste non utilisé.</summary>
    private sealed class FakeFiles(string home, List<RemoteEntry> entries) : IRemoteFiles
    {
        /// <summary>Date et taille rendues par <see cref="GetStatAsync"/>.</summary>
        public (DateTime LastWriteTime, long Length) Stat { get; init; }

        /// <summary>Écriture d'un fichier (renvoi de l'éditeur) ; null : non utilisée.</summary>
        public Func<string, byte[], Task<(DateTime, long)>>? Write { get; init; }

        public bool Disposed { get; private set; }
        public string HomeDirectory { get; } = home;
        public string CurrentDirectory { get; private set; } = home;
        public bool IsConnected => !Disposed;
        public bool ChoosesUploadProtocol => false;
        public TransferProtocol UploadProtocol => TransferProtocol.Ftps;

        public Task<List<RemoteEntry>> ListAsync(string path, bool showHidden, CancellationToken ct)
        {
            CurrentDirectory = path;
            return Task.FromResult(entries.ToList());
        }

        public Task<List<RemoteEntry>> BrowseAsync(string directory, bool showHidden, CancellationToken ct) => Task.FromResult(entries.ToList());
        public Task DeleteAsync(RemoteEntry entry, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(RemoteEntry entry, string newPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(true);
        public Task UploadAsync(string localPath, string remoteDirectory, TransferProtocol protocol, ICollection<TransferCheck> checks,
            IProgress<TransferProgress>? progress, bool background, CancellationToken ct) => throw new NotSupportedException();
        public ITailSource TailSource(string path) => throw new NotSupportedException();
        public Task<long> GetSizeAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<(DateTime LastWriteTime, long Length)> GetStatAsync(string path, CancellationToken ct) => Task.FromResult(Stat);
        public Task<(DateTime LastWriteTime, long Length)> WriteFileAsync(string remotePath, byte[] content, CancellationToken ct) =>
            Write?.Invoke(remotePath, content) ?? throw new NotSupportedException();
        public Task<int?> GetModeAsync(string path, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<PermissionsResult> SetPermissionsAsync(string path, PermissionChange change, bool recursive,
            bool executeOnlyIfAlready, IProgress<int>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<RemoteTreeItem>> ListTreeAsync(IReadOnlyList<RemoteEntry> roots, int maxItems, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, ICollection<TransferCheck>? checks,
            IProgress<TransferProgress>? progress, bool background, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Session PVWA expirée : reconnexion sur le même client, avec le même utilisateur, challenge RADIUS compris ; rien
    /// n'est envoyé au PVWA sans mot de passe (Entrée tapée par erreur).
    /// </summary>
    [Fact]
    public void ReconnectDialogSignsInAgainWithTheSameUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            UseDispatcherContext();
            var pvwa = new LogonPvwa();
            using var client = new PvwaClient(new Uri("https://pvwa.test"), pvwa);
            var dialog = new ReconnectDialog(client, AuthMethod.RADIUS, "jdoe", "jdoe", focusPassword: true);
            void Click() => dialog.ReconnectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Exception? failure = null;
            // Garde-fou : la fenêtre se ferme d'elle-même si le scénario se bloque.
            var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            guard.Tick += (_, _) => dialog.Close();
            dialog.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Assert.Contains("jdoe", dialog.WhoText.Text);
                    Assert.Contains("pvwa.test", dialog.WhoText.Text);
                    Assert.Equal(Visibility.Visible, dialog.CredentialsPanel.Visibility);

                    Click();
                    Assert.Equal(Strings.ReconnectPasswordMissing, dialog.ErrorMessage.Text);
                    Assert.Empty(pvwa.Logons);

                    dialog.PasswordBox.Password = "secret";
                    Click();
                    PumpUntil(() => dialog.ChallengePanel.Visibility == Visibility.Visible);
                    Assert.Equal("Enter the code", dialog.ChallengeText.Text);
                    Assert.Equal(Visibility.Collapsed, dialog.CredentialsPanel.Visibility);

                    dialog.ChallengeBox.Password = "123456";
                    Click();
                }
                catch (Exception e)
                {
                    failure = e;
                    dialog.Close();
                }
            });
            guard.Start();
            var result = dialog.ShowDialog();
            guard.Stop();

            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            Assert.True(result);
            Assert.True(client.IsAuthenticated);
            Assert.Equal(["jdoe:secret", "jdoe:123456"], pvwa.Logons);
            Assert.All(pvwa.Paths, path => Assert.Equal("/API/auth/RADIUS/Logon", path));

            // Méthode Windows : pas de mot de passe à saisir.
            var windows = new ReconnectDialog(client, AuthMethod.Windows, "jdoe", @"CORP\jdoe", focusPassword: false);
            Assert.Equal(Visibility.Collapsed, windows.CredentialsPanel.Visibility);
            windows.Close();
        });
    }

    /// <summary>Faux PVWA pour la reconnexion : challenge RADIUS à la première tentative, jeton à la suivante.</summary>
    private sealed class LogonPvwa : System.Net.Http.HttpMessageHandler
    {
        public List<string> Logons { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Logons.Add($"{body.RootElement.GetProperty("username").GetString()}:{body.RootElement.GetProperty("password").GetString()}");
            var (status, json) = Logons.Count == 1
                ? (HttpStatusCode.InternalServerError, "{\"ErrorCode\":\"ITATS542I\",\"ErrorMessage\":\"Enter the code\"}")
                : (HttpStatusCode.OK, "\"token-2\"");
            return new System.Net.Http.HttpResponseMessage(status)
            {
                Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Les suites des tâches attendues reviennent sur le fil de la fenêtre, comme dans l'application.</summary>
    private static void UseDispatcherContext() =>
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));

    /// <summary>Fait tourner la file du fil de la fenêtre jusqu'à la fin de la tâche.</summary>
    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs = 10000)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var started = Environment.TickCount64;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (done() || Environment.TickCount64 - started > timeoutMs)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert.True(done(), "délai dépassé");
    }

    /// <summary>Recherche dans le terminal (historique compris) et taille de police.</summary>
    [Fact]
    public void TerminalSearchFindsTextInTheHistory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var (session, view) = NewSshView("root@srv01");
            session.Emulator.Feed(string.Concat(Enumerable.Range(1, 60).Select(i => $"line {i}{(i % 20 == 0 ? " ERROR" : "")}\r\n")));
            view.ShowSearch();
            view.SearchBox.Text = "error";
            Assert.Equal(3, view.Matches.Count);
            Assert.Equal(2, view.CurrentMatch);
            view.MoveSearch(-1);
            Assert.Equal(1, view.CurrentMatch);
            view.MoveSearch(1);
            view.MoveSearch(1);
            Assert.Equal(0, view.CurrentMatch);

            ZillaTerm.App.Terminal.TerminalAppearance.Apply("solarized-light", 16, rightClickPastes: false);
            Assert.Same(ZillaTerm.Core.Terminal.TerminalTheme.SolarizedLight, ZillaTerm.App.Terminal.TerminalAppearance.Theme);
            ZillaTerm.App.Terminal.TerminalAppearance.Apply(null, 99, rightClickPastes: false);
            Assert.Equal(ZillaTerm.App.Terminal.TerminalAppearance.MaxFontSize, ZillaTerm.App.Terminal.TerminalAppearance.FontSize);
            ZillaTerm.App.Terminal.TerminalAppearance.Apply(null, 14, rightClickPastes: false);
            session.Dispose();
        });
    }

    /// <summary>
    /// Taille de police des Paramètres : appliquée aux terminaux affichés, et à ceux des onglets en arrière-plan quand ils
    /// réapparaissent ; la taille choisie pour un terminal (Ctrl+molette) reste si seule la palette change.
    /// </summary>
    [Fact]
    public void TerminalFontSizeFromTheSettingsReachesHiddenTerminals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var (shown, shownView) = NewSshView("root@srv01");
            var (hidden, hiddenView) = NewSshView("root@srv02");
            var window = new Window { Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false, Content = shownView };
            window.Show();
            PumpUntil(() => shownView.Terminal.IsLoaded);
            try
            {
                ZillaTerm.App.Terminal.TerminalAppearance.Apply(null, 18, rightClickPastes: false);
                Assert.Equal(18.0, shownView.Terminal.TerminalFontSize);
                Assert.Equal(14.0, hiddenView.Terminal.TerminalFontSize); // onglet en arrière-plan : pas chargé

                window.Content = hiddenView;
                PumpUntil(() => hiddenView.Terminal.IsLoaded);
                Assert.Equal(18.0, hiddenView.Terminal.TerminalFontSize);

                hiddenView.Terminal.SetFontSize(22);
                ZillaTerm.App.Terminal.TerminalAppearance.Apply("one-half-dark", 18, rightClickPastes: false);
                Assert.Equal(22.0, hiddenView.Terminal.TerminalFontSize);
                Assert.Same(ZillaTerm.Core.Terminal.TerminalTheme.OneHalfDark, hiddenView.Terminal.Theme);
            }
            finally
            {
                ZillaTerm.App.Terminal.TerminalAppearance.Apply(null, 14, rightClickPastes: false);
                window.Close();
                shown.Dispose();
                hidden.Dispose();
            }
        });
    }

    /// <summary>Caractères larges, hors BMP et combinants : dessinés sans erreur (sélection comprise), copiés tels quels.</summary>
    [Fact]
    public void TerminalDrawsAndCopiesWideAndCombinedCharacters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var (session, view) = NewSshView("root@srv01");
            var window = new Window { Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false, Content = view };
            window.Show();
            PumpUntil(() => view.Terminal.IsLoaded);
            try
            {
                session.Emulator.Feed("\x1b[2J\x1b[H日本語 e\u0301 😀 \U000F0001 ok\r\n[root@srv01 ~]# ");
                view.Terminal.Focus();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 500, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(view.Terminal);

                view.Terminal.BuildMenu(atCursor: false)!.Items.OfType<System.Windows.Controls.MenuItem>()
                    .Single(i => i.Header as string == Strings.MenuTerminalSelectAll).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert.Equal("日本語 e\u0301 😀 \U000F0001 ok\n[root@srv01 ~]#", view.Terminal.SelectedText);
                bitmap.Render(view.Terminal);
            }
            finally
            {
                window.Close();
                session.Dispose();
            }
        });
    }

    /// <summary>Menu du clic droit dans le terminal : actions selon l'état, effacer l'historique, actions de la session.</summary>
    [Fact]
    public void TerminalMenuOffersTheTerminalAndSessionActions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var (session, view) = NewSshView("root@srv01");
            view.SessionMenu = items => items.Add(new System.Windows.Controls.MenuItem { Header = "session" });
            session.Emulator.Feed(string.Concat(Enumerable.Range(1, 60).Select(i => $"line {i}{(i % 20 == 0 ? " ERROR" : "")}\r\n")));
            view.ShowSearch();
            view.SearchBox.Text = "error";
            Assert.Equal(3, view.Matches.Count);

            var menu = view.Terminal.BuildMenu(atCursor: true)!;
            var entries = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            System.Windows.Controls.MenuItem Entry(string header) => entries.Single(i => i.Header as string == header);
            Assert.False(Entry(Strings.MenuTerminalCopy).IsEnabled); // rien de sélectionné
            Assert.Equal(Strings.ShortcutTerminalPaste, Entry(Strings.MenuTerminalPaste).InputGestureText);
            Assert.NotNull(Entry(Strings.MenuTerminalSearch));
            Assert.NotNull(Entry(Strings.MenuTerminalSave));
            Assert.Equal(3, Entry(Strings.MenuTerminalFont).Items.Count);
            Assert.Equal("session", entries[^1].Header);

            // Effacer l'historique : l'écran reste, la recherche ne trouve plus que ce qui est à l'écran.
            Entry(Strings.MenuTerminalClear).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert.Equal(0, session.Emulator.ScrollbackCount);
            Assert.Equal(2, view.Matches.Count); // lignes 40 et 60 : l'écran (30 lignes) montre les lignes 32 à 60
            Assert.False(view.Terminal.BuildMenu(atCursor: false)!.Items.OfType<System.Windows.Controls.MenuItem>()
                .Single(i => i.Header as string == Strings.MenuTerminalClear).IsEnabled);

            // Tout sélectionner : jusqu'à l'invite, sans les lignes vides du bas de l'écran (après « clear »).
            session.Emulator.Feed("\x1b[2J\x1b[H[root@srv01 ~]# ");
            view.Terminal.BuildMenu(atCursor: false)!.Items.OfType<System.Windows.Controls.MenuItem>()
                .Single(i => i.Header as string == Strings.MenuTerminalSelectAll).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert.Equal("[root@srv01 ~]#", view.Terminal.SelectedText);
            session.Dispose();
        });
    }

    /// <summary>
    /// Pavé tactile de précision : les petits pas de la molette s'additionnent (le reste repart de zéro au changement de
    /// sens). Capture de la souris perdue pendant une sélection : la sélection est terminée.
    /// </summary>
    [Fact]
    public void TerminalAddsSmallWheelStepsAndEndsASelectionWhenTheMouseIsTaken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var (session, view) = NewSshView("root@srv01");
            var window = new Window { Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false, Content = view };
            window.Show();
            PumpUntil(() => view.Terminal.IsLoaded);
            try
            {
                var terminal = view.Terminal;
                session.Emulator.Feed(string.Concat(Enumerable.Range(1, 100).Select(i => $"line {i}\r\n")));
                void Wheel(int delta) => terminal.RaiseEvent(
                    new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                    });

                for (int i = 0; i < 6; i++)
                {
                    Wheel(20);
                }

                Assert.Equal(3, terminal.ScrollState.Offset);
                Wheel(30);
                Wheel(-20);
                Assert.Equal(3, terminal.ScrollState.Offset);
                Wheel(-20);
                Assert.Equal(2, terminal.ScrollState.Offset);

                terminal.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.True(terminal.IsSelecting);
                Assert.True(terminal.IsMouseCaptured);
                System.Windows.Input.Mouse.Capture(null);
                Assert.False(terminal.IsSelecting);
            }
            finally
            {
                window.Close();
                session.Dispose();
            }
        });
    }

    private sealed class NoInteraction : ZillaTerm.Core.Ssh.ISshInteraction
    {
        public bool CheckHostKey(string host, int port, string algorithm, string sha256Fingerprint) => false;

        public ZillaTerm.Core.Ssh.SshAnswer? Prompt(ZillaTerm.Core.Ssh.SshQuestion question) => null;
    }

    private sealed class MemoryFile : ITailSource
    {
        private readonly List<byte> _content = [];

        public long Length => _content.Count;

        public void Append(string text) => _content.AddRange(System.Text.Encoding.UTF8.GetBytes(text));

        public Task<long> GetSizeAsync(CancellationToken ct) => Task.FromResult((long)_content.Count);

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) =>
            Task.FromResult(_content.Skip((int)offset).Take(count).ToArray());
    }

    /// <summary>Connexion de suivi en mémoire : fichiers par chemin, coupure et reconnexion à la demande.</summary>
    private sealed class MemoryLink(string server, ZillaTerm.App.Services.RemoteSession? session = null) : ITailLink
    {
        private readonly Dictionary<string, MemoryFile> _files = [];

        public bool Connected { get; set; } = true;

        public bool Disposed { get; private set; }

        public string Server => server;

        public ZillaTerm.App.Services.RemoteSession? Session => session;

        public bool Dedicated => false;

        public int Generation { get; set; } = 1;

        public bool IsConnecting => false;

        public string? Error => null;

        public bool CanReconnect => false;

        public MemoryFile Add(string path) => _files[path] = new MemoryFile();

        public bool CheckConnected() => Connected && !Disposed;

        public void RequestReconnect()
        {
        }

        public Task ReconnectAsync() => Task.CompletedTask;

        public ITailSource Source(string path) => _files[path];

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Thème sombre : noms des fichiers (lignes simples, sélectionnée) et nom de l'onglet de session lisibles. Le style de
    /// Windows écrit les premiers en bleu nuit, le second en noir, et sélectionne la ligne avec un dégradé clair.
    /// </summary>
    [Fact]
    public void DarkThemeKeepsFileAndTabNamesReadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            var dark = Palette.Dark(merged.ToList());
            merged.Add(dark);
            try
            {
                var panel = new FileBrowserPanel();
                panel.Initialize(new AppSettings(), () => { }, new TransferHistory());
                var header = new SessionTabHeader("srv01.corp.local", "IconSsh", new System.Windows.Controls.Button(), "SSH", null);
                header.SetState(SessionTabState.Connected);
                var tabs = new System.Windows.Controls.TabControl { Style = (Style)Application.Current.FindResource("MainTabControl") };
                tabs.Items.Add(new System.Windows.Controls.TabItem { Header = header, Content = panel });
                var window = new Window { Content = tabs, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                RemoteEntry E(string name) => new(name, "/etc/" + name, false, false, 10, new DateTime(2026, 10, 1), "-rw-r--r--");
                panel.FileList.ItemsSource = new List<RemoteEntry> { E("hosts"), E("passwd") };
                panel.FileList.SelectedIndex = 0;
                window.UpdateLayout();

                static System.Windows.Media.Color ColorOf(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color;
                var content = ColorOf((System.Windows.Media.Brush)dark["ContentBrush"]);
                for (int i = 0; i < 2; i++)
                {
                    var row = (System.Windows.Controls.ListViewItem)panel.FileList.ItemContainerGenerator.ContainerFromIndex(i);
                    var background = row.Background is System.Windows.Media.SolidColorBrush { Color.A: 255 } fill ? fill.Color : content;
                    Assert.True(Contrast(ColorOf(row.Foreground), background) >= 4.5, $"ligne {i} : {ColorOf(row.Foreground)} sur {background}");
                }

                var tab = ColorOf((System.Windows.Media.Brush)dark["MainTabBrush"]);
                Assert.True(Contrast(ColorOf(header.Title.Foreground), tab) >= 4.5, $"onglet : {ColorOf(header.Title.Foreground)} sur {tab}");

                // Retour au thème clair sans redémarrer : la ligne reprend le modèle de Windows.
                merged.Remove(dark);
                window.UpdateLayout();
                Assert.NotSame(dark["FileRow"], panel.FileList.ItemContainerStyle);
                window.Close();
            }
            finally
            {
                merged.Remove(dark);
            }
        });

        static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            static double Luminance(System.Windows.Media.Color color)
            {
                static double Channel(byte value)
                {
                    var c = value / 255.0;
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }

                return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
            }

            var (light, darker) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
            return (light + 0.05) / (darker + 0.05);
        }
    }

    /// <summary>
    /// Ligne sélectionnée d'un arbre : les textes secondaires (nombre, mode, ⚠, ✓) prennent la couleur du texte de la ligne,
    /// faite pour le fond de la sélection ; hors sélection, ils gardent la leur.
    /// </summary>
    [Fact]
    public void SelectedTreeRowKeepsSecondaryTextsReadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            System.Windows.Controls.TextBlock Note(string style) => new() { Text = "x", Style = (Style)Application.Current.FindResource(style) };
            var (note, warning, mark, other) = (Note("TreeNote"), Note("TreeWarning"), Note("TreeAccent"), Note("TreeNote"));
            var row = new System.Windows.Controls.TreeViewItem { Header = new System.Windows.Controls.StackPanel { Children = { note, warning, mark } } };
            var tree = new System.Windows.Controls.TreeView();
            tree.Items.Add(row);
            tree.Items.Add(new System.Windows.Controls.TreeViewItem { Header = other });
            var window = new Window { Content = tree, Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                row.IsSelected = true;
                window.UpdateLayout();

                static System.Windows.Media.Color ColorOf(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color;
                var muted = ColorOf((System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush"));
                Assert.NotEqual(muted, ColorOf(row.Foreground));
                Assert.All(new[] { note, warning, mark }, text => Assert.Equal(ColorOf(row.Foreground), ColorOf(text.Foreground)));
                Assert.Equal(muted, ColorOf(other.Foreground));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Contraste élevé : les couleurs d'état deviennent toutes celle du texte ; la forme de la pastille distingue encore les
    /// cinq états, dans la même largeur (l'onglet ne s'allonge pas).
    /// </summary>
    [Fact]
    public void TabStatesKeepDistinctShapesInHighContrast()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            var contrast = Palette.HighContrast();
            merged.Add(contrast);
            try
            {
                var header = new SessionTabHeader("srv01.corp.local", "IconSsh", new System.Windows.Controls.Button(), "SSH", null);
                var marks = Enum.GetValues<SessionTabState>().Select(state =>
                {
                    header.SetState(state);
                    Assert.Equal(8, header.StateMark.Width);
                    return (header.StateMark.Data, Filled: !ReferenceEquals(header.StateMark.Fill, System.Windows.Media.Brushes.Transparent));
                }).ToList();

                Assert.Equal(5, marks.Distinct().Count());
            }
            finally
            {
                merged.Remove(contrast);
            }
        });
    }

    /// <summary>
    /// Lecteurs d'écran : une case ou un bouton dont le libellé est un AccessText (seul ou avec une image) est nommé par ce
    /// libellé, sans la touche d'accès, et suit ses changements (« Enregistrer » / « Arrêter l'enregistrement ») ; un nom
    /// explicite reste.
    /// </summary>
    [Fact]
    public void AccessTextLabelsNameTheirButtons()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            AccessibleName.Follow();
            var box = new System.Windows.Controls.CheckBox { Content = new System.Windows.Controls.AccessText { Text = "_Garder la session" } };
            var label = new System.Windows.Controls.AccessText { Text = "_Enregistrer l'intervention" };
            var record = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Children = { new System.Windows.Shapes.Ellipse { Width = 10, Height = 10 }, label },
                },
            };
            var named = new System.Windows.Controls.Button { Content = new System.Windows.Controls.AccessText { Text = "_Ajouter" } };
            System.Windows.Automation.AutomationProperties.SetName(named, "Ajouter une étiquette");
            var window = new Window
            {
                Content = new System.Windows.Controls.StackPanel { Children = { box, record, named } },
                Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                // Nom donné au chargement des contrôles.
                PumpUntil(() => box.IsLoaded && record.IsLoaded && named.IsLoaded);
                static string NameOf(UIElement element) => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element).GetName();
                Assert.Equal("Garder la session", NameOf(box));
                Assert.Equal("Enregistrer l'intervention", NameOf(record));
                label.Text = "_Arrêter l'enregistrement";
                Assert.Equal("Arrêter l'enregistrement", NameOf(record));
                Assert.Equal("Ajouter une étiquette", NameOf(named));
                Assert.Equal("PROMPT_COMMAND", AccessibleName.WithoutAccessKey("_PROMPT__COMMAND"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Question du PSMP après un refus : l'avertissement (essai n sur 3, Verr. Maj) est lu avec le champ, qui prend le focus ;
    /// au premier essai, rien de plus que la question.
    /// </summary>
    [Fact]
    public void RefusedAnswerIsReadWithTheField()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            const string refused = "Refusé par le serveur. Essai 2 sur 3 : vérifiez-le (Verr. Maj ?).";
            var again = new PromptDialog("", "Password:", echo: false, refused: refused);
            Assert.Equal(refused, System.Windows.Automation.AutomationProperties.GetHelpText(again.SecretBox));
            Assert.Equal("Password:", System.Windows.Automation.AutomationProperties.GetName(again.SecretBox));
            again.Close();

            var first = new PromptDialog("", "Password:", echo: false);
            Assert.Equal("", System.Windows.Automation.AutomationProperties.GetHelpText(first.SecretBox));
            first.Close();
        });
    }

    /// <summary>
    /// Thème sombre : les fenêtres s'ouvrent avec ses modèles (listes déroulantes, onglets, tableaux, cases), le choix
    /// est relu des réglages, et les icônes foncées sont éclaircies (écran de terminal cerné d'un trait clair).
    /// </summary>
    [Fact]
    public void DarkThemeDrawsWindowsWithItsTemplates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            var dark = Palette.Dark(merged.ToList());
            merged.Add(dark);
            try
            {
                var dialog = new SettingsDialog(new AppSettings { Theme = AppTheme.Dark }) { ShowInTaskbar = false, ShowActivated = false };
                dialog.Show();
                PumpUntil(() => dialog.IsLoaded);
                Assert.Equal(AppTheme.Dark, dialog.AppThemeBox.SelectedValue);
                Assert.Same(dark["ComboTemplate"], dialog.LanguageBox.Template);
                foreach (var page in dialog.Pages.Items.OfType<System.Windows.Controls.TabItem>())
                {
                    dialog.Pages.SelectedItem = page;
                    dialog.UpdateLayout();
                }

                dialog.Close();

                static IEnumerable<System.Windows.Media.GeometryDrawing> Shapes(System.Windows.Media.Drawing drawing) => drawing switch
                {
                    System.Windows.Media.DrawingGroup group => group.Children.SelectMany(Shapes),
                    System.Windows.Media.GeometryDrawing shape => [shape],
                    _ => [],
                };

                static System.Windows.Media.Color? Fill(System.Windows.Media.GeometryDrawing shape) =>
                    (shape.Brush as System.Windows.Media.SolidColorBrush)?.Color;

                var gear = Shapes(((System.Windows.Media.DrawingImage)dark["IconSettings"]).Drawing).ToList();
                Assert.Contains(System.Windows.Media.Color.FromRgb(0xAE, 0xB8, 0xC2), gear.Select(Fill));
                var screen = Shapes(((System.Windows.Media.DrawingImage)dark["IconSsh"]).Drawing).First();
                Assert.NotNull(screen.Pen);
                // Pastilles des dialogues : inchangées.
                Assert.False(dark.Contains("IconWarning"));
            }
            finally
            {
                merged.Remove(dark);
            }
        });
    }

    /// <summary>
    /// Intervention : les consignes d'astreinte s'affichent telles quelles (texte brut, sans mise en forme) ; enregistrer, donner
    /// un titre (repris dans la liste), arrêter ; l'intervention en cours ne se supprime pas ; le journal se relit avec la
    /// note, le titre et sa fin.
    /// </summary>
    [Fact]
    public void InterventionWindowRecordsANoteAndListsTheIntervention()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "zillaterm-intervention-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunWithTheme(() =>
            {
                var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                var recorder = new ZillaTerm.App.Services.InterventionRecorder(dir, new XorProtector());
                const string team = "Astreinte : 01 23 45 67 89\n<b>pas de HTML</b>";
                var window = new InterventionWindow(team, "", recorder, "jdoe", "session CyberArk (pvwa.corp.local)");
                Assert.Equal(team, window.TeamText.Text);
                Assert.Equal(Visibility.Collapsed, window.NoTeamText.Visibility);
                Assert.False(window.NotesButton.IsEnabled);

                window.RecordButton.RaiseEvent(click);
                Assert.True(recorder.IsRecording);
                Assert.True(window.NotesButton.IsEnabled);
                Assert.Equal(Strings.InterventionStopRecording, window.RecordLabel.Text);
                recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Note, "", "INC0012345 : redémarrage de httpd");
                Assert.True(recorder.SetTitle("INC0012345 httpd srv01"));
                Assert.False(recorder.SetTitle(" INC0012345  httpd srv01 "));
                Assert.Contains("INC0012345 httpd srv01", (string)((System.Windows.Controls.ListBoxItem)window.JournalList.Items[0]).Content,
                    StringComparison.Ordinal);

                window.JournalList.SelectedIndex = 0;
                Assert.True(window.ExportButton.IsEnabled);
                Assert.False(window.DeleteButton.IsEnabled);

                window.RecordButton.RaiseEvent(click);
                Assert.False(recorder.IsRecording);
                // L'intervention arrêtée reste sélectionnée, prête à exporter.
                Assert.Equal(0, window.JournalList.SelectedIndex);
                Assert.Equal(Strings.InterventionStoppedHint, window.MessageText.Text);
                Assert.True(window.ExportButton.IsEnabled);
                Assert.True(window.DeleteButton.IsEnabled);
                var file = Assert.IsType<ZillaTerm.Core.Interventions.InterventionFile>(((System.Windows.Controls.ListBoxItem)window.JournalList.Items[0]).Tag);
                var recording = ZillaTerm.Core.Interventions.InterventionJournal.Read(file.Path, recorder.Protector);
                Assert.Equal(ZillaTerm.Core.Interventions.InterventionKind.Started, recording.Entries[0].Kind);
                Assert.Contains(recording.Entries, e => e.Kind == ZillaTerm.Core.Interventions.InterventionKind.Note && e.Text == "INC0012345 : redémarrage de httpd");
                Assert.Equal(ZillaTerm.Core.Interventions.InterventionKind.Stopped, recording.Entries[^1].Kind);
                Assert.Equal("INC0012345 httpd srv01", recording.Title);
                Assert.Equal("INC0012345 httpd srv01", file.Title);
                Assert.False(recording.Unfinished);

                Assert.True(window.ShowReminderAsync("", "").IsCompleted);
                Assert.Equal(Visibility.Visible, window.NoTeamText.Visibility);
                window.Close();
            });
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Disque qui refuse le journal pendant l'enregistrement : l'interface est prévenue aussitôt, la fenêtre dit depuis
    /// quand plus rien n'est gardé (couleur d'erreur du thème), et note et capture ne sont plus proposées.
    /// </summary>
    [Fact]
    public void InterventionWindowSaysWhenTheDiskRefusesTheJournal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "zillaterm-intervention-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunWithTheme(() =>
            {
                UseDispatcherContext();
                var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                var recorder = new ZillaTerm.App.Services.InterventionRecorder(dir, new FailingProtector(failFrom: 2));
                int changed = 0;
                recorder.Changed += () => changed++;
                var window = new InterventionWindow("", "", recorder, "jdoe", "session CyberArk (pvwa.corp.local)");
                window.RecordButton.RaiseEvent(click);
                Assert.Equal(1, changed);
                Assert.Null(recorder.FailureMessage);

                recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Note, "", "perdue");
                recorder.Flush();
                PumpUntil(() => changed == 2);
                Assert.Equal(2, changed);
                Assert.True(recorder.IsRecording);
                Assert.NotNull(recorder.Failure);
                int count = recorder.Count;
                recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Note, "", "ignorée");
                Assert.Equal(count, recorder.Count);

                var since = recorder.FailedAt!.Value.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
                Assert.Equal(Text.Format(Strings.InterventionInterruptedAt, since), window.StatusText.Text);
                Assert.Equal(recorder.FailureMessage, window.MessageText.Text);
                Assert.Contains(since, window.MessageText.Text, StringComparison.Ordinal);
                Assert.Same(window.FindResource("ErrorBrush"), window.MessageText.Foreground);
                var notes = new InterventionPanel();
                notes.Attach(recorder);
                Assert.False(notes.TitleBox.IsEnabled);
                Assert.False(notes.NoteBox.IsEnabled);
                Assert.False(notes.CaptureButton.IsEnabled);
                Assert.Equal(recorder.FailureMessage, notes.MessageText.Text);
                notes.Detach();

                window.RecordButton.RaiseEvent(click);
                Assert.False(recorder.IsRecording);
                Assert.Null(recorder.FailureMessage);
                window.Close();
            });
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Onglet « Intervention » : le titre (Entrée) et la note (Entrée) vont au journal et dans l'historique ; un long retour
    /// de terminal est réduit, se déplie en entier et grandit avec la sortie ; à l'arrêt, la saisie est grisée et
    /// l'historique reste jusqu'à « Fermer l'historique » ; un nouvel enregistrement repart de zéro.
    /// </summary>
    [Fact]
    public void InterventionPanelKeepsTitleAndNotesAndShowsTheHistory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "zillaterm-intervention-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunWithTheme(() =>
            {
                UseDispatcherContext();
                var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                var recorder = new ZillaTerm.App.Services.InterventionRecorder(dir, new XorProtector());
                var panel = new InterventionPanel();
                panel.Attach(recorder);
                Assert.False(panel.HasHistory);
                Assert.False(panel.TitleBox.IsEnabled);
                Assert.Equal(Strings.InterventionNotRecording, panel.StatusText.Text);
                var window = new Window { Content = panel, Width = 420, Height = 640, ShowActivated = false, ShowInTaskbar = false };
                window.Show();

                recorder.Start("jdoe", "session CyberArk (pvwa.corp.local)");
                Assert.True(panel.HasHistory);
                Assert.True(panel.TitleBox.IsEnabled);
                Assert.Equal(Visibility.Collapsed, panel.CloseHistoryButton.Visibility);
                var started = Assert.IsType<InterventionHistoryItem>(Assert.Single(panel.HistoryList.Items));
                Assert.Equal(Strings.InterventionKindStarted, started.Kind);

                System.Windows.Input.KeyEventArgs Enter(System.Windows.RoutedEvent routed) =>
                    new(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Enter)
                    {
                        RoutedEvent = routed,
                    };

                panel.TitleBox.Text = "  INC0012345\u202E disque plein  ";
                panel.TitleBox.RaiseEvent(Enter(System.Windows.Input.Keyboard.KeyDownEvent));
                Assert.Equal("INC0012345 disque plein", recorder.Title);
                Assert.Equal("INC0012345 disque plein", panel.TitleBox.Text);
                Assert.Equal(Strings.InterventionTitleSaved, panel.MessageText.Text);
                Assert.Equal("INC0012345 disque plein", ZillaTerm.Core.Interventions.InterventionJournal.ReadTitle(recorder.JournalPath!, recorder.Protector));

                Assert.False(panel.NoteButton.IsEnabled);
                panel.NoteBox.Text = "redémarrage de httpd\r\nvalidé par l'astreinte N2";
                Assert.True(panel.NoteButton.IsEnabled);
                var noteEnter = Enter(System.Windows.Input.Keyboard.PreviewKeyDownEvent);
                panel.NoteBox.RaiseEvent(noteEnter);
                Assert.True(noteEnter.Handled);
                Assert.Equal("", panel.NoteBox.Text);
                Assert.Equal(Strings.InterventionNoteAdded, panel.MessageText.Text);

                recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Connection, "root@srv01", "Connecté : root@srv01");
                recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Terminal, "root@srv01", "[root@srv01 ~]# journalctl -u httpd");
                for (int i = 1; i <= 20; i++)
                {
                    recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Terminal, "root@srv01", $"ligne {i}");
                }

                panel.ReadHistory();
                var items = panel.HistoryList.Items.Cast<InterventionHistoryItem>().ToList();
                Assert.Equal([ZillaTerm.Core.Interventions.InterventionKind.Started, ZillaTerm.Core.Interventions.InterventionKind.Title,
                    ZillaTerm.Core.Interventions.InterventionKind.Note, ZillaTerm.Core.Interventions.InterventionKind.Connection,
                    ZillaTerm.Core.Interventions.InterventionKind.Terminal], items.Select(item => item.Block.Kind));
                Assert.Equal(Strings.InterventionKindTitle, items[1].Kind);
                Assert.Equal("redémarrage de httpd\nvalidé par l'astreinte N2", items[2].AllText);
                Assert.False(items[2].IsLong);

                var output = items[^1];
                Assert.True(output.IsTerminal);
                Assert.True(output.IsLong);
                Assert.Equal(21, output.LineCount);
                Assert.Equal(InterventionHistoryItem.PreviewLines + 1, output.Preview.Split('\n').Length);
                Assert.Equal("", output.FullText);
                Assert.Equal(Text.Format(Strings.InterventionShowAll, 21), output.ToggleText);
                output.IsExpanded = true;
                Assert.Equal(21, output.FullText.Split('\n').Length);
                Assert.Equal(Strings.InterventionCollapse, output.ToggleText);

                // La sortie continue : le même bloc grandit.
                recorder.Record(ZillaTerm.Core.Interventions.InterventionKind.Terminal, "root@srv01", "ligne 21");
                panel.ReadHistory();
                Assert.Equal(5, panel.HistoryList.Items.Count);
                Assert.Equal(22, output.LineCount);
                Assert.EndsWith("ligne 21", output.FullText, StringComparison.Ordinal);

                // Arrêt : saisie grisée, historique gardé avec la fin, jusqu'à « Fermer l'historique ».
                recorder.Stop(Strings.InterventionStoppedByUser);
                Assert.False(panel.TitleBox.IsEnabled);
                Assert.False(panel.NoteBox.IsEnabled);
                Assert.False(panel.CaptureButton.IsEnabled);
                Assert.Equal(Strings.InterventionNotesStopped, panel.MessageText.Text);
                Assert.Equal(ZillaTerm.Core.Interventions.InterventionKind.Stopped,
                    ((InterventionHistoryItem)panel.HistoryList.Items[^1]).Block.Kind);
                Assert.True(panel.HasHistory);
                Assert.Equal(Visibility.Visible, panel.CloseHistoryButton.Visibility);

                // Nouvel enregistrement : l'onglet repart de zéro.
                recorder.Start("jdoe", "session CyberArk (pvwa.corp.local)");
                Assert.Single(panel.HistoryList.Items);
                Assert.True(panel.NoteBox.IsEnabled);
                Assert.Equal("", panel.TitleBox.Text);
                Assert.Equal(Visibility.Collapsed, panel.CloseHistoryButton.Visibility);
                recorder.Stop(Strings.InterventionStoppedByUser);

                int closed = 0;
                panel.CloseRequested += (_, _) => closed++;
                panel.CloseHistoryButton.RaiseEvent(click);
                Assert.Equal(1, closed);
                Assert.False(panel.HasHistory);
                Assert.Empty(panel.HistoryList.Items);
                Assert.Equal(Visibility.Collapsed, panel.CloseHistoryButton.Visibility);
                panel.Detach();
                window.Close();
            });
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Consignes d'astreinte tirées d'un fichier (tenu à jour par les responsables) : son texte remplace celui des
    /// réglages, inverseurs de sens de lecture retirés ; fichier absent : le texte des réglages reste, avec la raison.
    /// </summary>
    [Fact]
    public void InterventionWindowShowsTheDutyReminderFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "zillaterm-reminder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "consignes.txt");
            File.WriteAllText(file, "Responsable : 01 23 45 67 89\r\nSupervision : \u202E98 76 54 32 10\r\n", new System.Text.UTF8Encoding(true));
            RunWithTheme(() =>
            {
                UseDispatcherContext();
                var recorder = new ZillaTerm.App.Services.InterventionRecorder(Path.Combine(dir, "journaux"), new XorProtector());
                var window = new InterventionWindow("Texte des réglages", file, recorder, "jdoe", "session CyberArk (pvwa.corp.local)");
                PumpUntil(() => window.TeamText.Text != "Texte des réglages");
                Assert.Equal("Responsable : 01 23 45 67 89\nSupervision : 98 76 54 32 10", window.TeamText.Text);
                Assert.Equal(Text.Format(Strings.DutyFileSource, file), window.ReminderSource.Text);

                Pump(window.ShowReminderAsync("Texte des réglages", Path.Combine(dir, "absent.txt")));
                Assert.Equal("Texte des réglages", window.TeamText.Text);
                Assert.Equal(Text.Format(Strings.DutyFileUnreadable, Strings.DutyFileMissing), window.ReminderSource.Text);

                Assert.True(window.ShowReminderAsync("Texte des réglages", "").IsCompleted);
                Assert.Equal(Visibility.Collapsed, window.ReminderSource.Visibility);
                window.Close();
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Protecteur qui refuse à partir du bloc <c>failFrom</c> (comme un disque plein au moment d'écrire).</summary>
    private sealed class FailingProtector(int failFrom) : ZillaTerm.Core.KeePass.ISecretProtector
    {
        private int _calls;

        public byte[] Protect(byte[] data) => ++_calls >= failFrom
            ? throw new System.Security.Cryptography.CryptographicException("refusé")
            : data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private sealed class XorProtector : ZillaTerm.Core.KeePass.ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    /// <summary>
    /// Thread STA avec l'objet <c>Application</c> et le thème (comme dans ZillaTerm), retiré ensuite pour que les
    /// autres tests ne trouvent pas de ressources liées à ce thread.
    /// </summary>
    private static void RunWithTheme(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/ZillaTerm;component/Theme.xaml") };
            app.Resources.MergedDictionaries.Add(theme);
            try
            {
                test();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                app.Resources.MergedDictionaries.Remove(theme);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
