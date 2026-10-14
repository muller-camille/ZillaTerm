using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Ftp;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Sessions de fichiers seuls (comptes CyberArk en SFTP via le PSMP ; accès d'urgence, entrées KeePass sftp://, ftp://,
/// ftpes://, ftps://) : un onglet d'état, les fichiers dans l'onglet Fichiers (même explorateur, mêmes transferts
/// vérifiés que les sessions SSH).
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Fichiers d'un compte CyberArk sans terminal : une session PSMP SFTP (enregistrée par CyberArk comme les autres),
    /// rouverte à la reconnexion ; les envois peuvent aussi passer par SCP si la plateforme l'autorise.
    /// </summary>
    private void OpenPsmpFilesTab(PvwaAccount account, PsmpEndpoint psmp, string login, string label, SavedSession? saved,
        Func<Task>? duplicate, ConnectRequest request)
    {
        var connector = new SshConnector(psmp.Host, psmp.Port, login, _psmpUi.For(label), PsmpKeyAsync, group: _openingGroup);
        var session = new FilesSession(label, "SFTP", connector.OpenFileBrowserAsync, Dispatcher.UIThread, account, saved)
        {
            Psmp = psmp.Host,
            Request = request,
        };
        ShowFilesTab(session, $"{login}@{psmp.Host}", duplicate);
    }

    /// <summary>Ouvre la session de fichiers d'une entrée KeePass ; le mot de passe est lu à chaque connexion.</summary>
    private void OpenKeePassFiles(KeePassTarget target, string label, Func<string?> password, Func<Task> duplicate)
    {
        Func<CancellationToken, Task<IRemoteFiles>> open;
        if (target.Protocol == RemoteProtocol.Sftp)
        {
            var connector = new SshConnector(target.Host, target.Port, target.UserName, _directUi.For(label), password: password,
                addressWhat: CoreStrings.ServerAddressWhat);
            open = connector.OpenFileBrowserAsync;
        }
        else
        {
            // Connexion en clair acceptée une fois pour la session (connexions dédiées au suivi comprises).
            bool cleartextAccepted = false;
            var connection = new FtpConnection
            {
                Host = target.Host,
                Port = target.Port,
                UserName = target.UserName.Length > 0 ? target.UserName : "anonymous",
                Password = password,
                Security = target.Protocol switch
                {
                    RemoteProtocol.Ftps => FtpSecurity.Implicit,
                    RemoteProtocol.Ftpes => FtpSecurity.Explicit,
                    _ => FtpSecurity.Opportunistic,
                },
                // Rappels synchrones de la connexion : la question est posée sur le fil de l'interface et attendue.
                TrustCertificate = certificate => UiWait.Run(() => TrustFtpCertificateAsync(target, certificate)),
                AllowCleartext = () => cleartextAccepted || (cleartextAccepted = UiWait.Run(() => ConfirmFtpCleartextAsync(target))),
                // Serveur déjà vu avec TLS : son retrait est une alerte (confirmée, elle vaut pour la session).
                TlsSeenBefore = () => Dispatcher.UIThread.Invoke(() => FtpTlsMemory.Seen(_settings, target.Host, target.Port)),
                AllowTlsRemoved = () => cleartextAccepted = UiWait.Run(() => ConfirmFtpTlsRemovedAsync(target)),
            };
            open = async ct =>
            {
                var browser = await FtpFileBrowser.ConnectAsync(connection, ct);
                if (browser.IsEncrypted)
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (FtpTlsMemory.Remember(_settings, target.Host, target.Port))
                        {
                            SaveSettings();
                        }
                    });
                }

                return browser;
            };
        }

        var session = new FilesSession(label, KeePassTarget.Name(target.Protocol), open, Dispatcher.UIThread);
        var who = target.UserName.Length > 0 ? $"{target.UserName}@{target.Address}" : target.Address;
        ShowFilesTab(session, who, duplicate);
    }

    private void ShowFilesTab(FilesSession session, string target, Func<Task>? duplicate)
    {
        session.Editor = new RemoteEditor(session, this, _settings, (text, error) => SetStatus(text, error),
            directory => FilesPanel.OnRemoteChanged(session, directory));
        var view = new FilesSessionView(session, target)
        {
            // Reconnexion : la connexion actuelle sera fermée, ses transferts sont donc annulés d'abord (avec accord).
            BeforeReconnect = async () =>
            {
                if (!await FilesPanel.ConfirmCancelTransfersAsync(this, session))
                {
                    return false;
                }

                await FilesPanel.CancelTransfersAsync(session);
                return true;
            },
        };
        view.ShowFilesRequested += ShowFilesWide;
        view.ShowTag(TagOf(session));
        var tab = new TabItem { Content = view, Tag = session };
        tab.Header = TabHeader(tab, session.Label, "IconFiles", duplicate);
        session.StateChanged += () =>
        {
            switch (session.State)
            {
                case RemoteSessionState.Connected:
                    SessionStatus(session.Label, Text.Format(Strings.FilesOpened, session.Label, session.Protocol));
                    break;
                case RemoteSessionState.Failed:
                    SessionStatus(session.Label, Text.Format(Strings.FilesSessionError, session.Label, session.Error), isError: true);
                    break;
            }
        };

        _remoteSessions.Add(session);
        MainTabs.Items.Add(tab);
        MainTabs.SelectedItem = tab;
        SideTabs.SelectedItem = FilesTab;
        _ = view.ConnectAsync();
    }

    /// <summary>
    /// Certificat FTPS que Windows n'approuve pas (auto-signé, autre nom…) : accepté s'il a déjà été épinglé pour ce
    /// serveur, sinon question ; un certificat différent de celui épinglé est signalé comme un changement suspect.
    /// </summary>
    private async Task<bool> TrustFtpCertificateAsync(KeePassTarget target, FtpCertificate certificate)
    {
        var host = "ftps://" + target.Host;
        var status = KnownHosts.Check(_settings.KnownHosts, host, target.Port, "X.509", certificate.Sha256);
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        if (!await ConfirmCertificateAsync(Strings.FtpCertificateTitle, Strings.FtpCertVerifyHeading, Strings.FtpCertChangedHeading, target.Address,
                (certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter, certificate.Sha256, certificate.Problem),
                status == HostKeyStatus.Unknown ? null : KnownHosts.Known(_settings.KnownHosts, host, target.Port, "X.509")?.Sha256 ?? ""))
        {
            return false;
        }

        KnownHosts.Remember(_settings.KnownHosts, host, target.Port, "X.509", certificate.Sha256);
        SaveSettings();
        return true;
    }

    /// <summary>
    /// Question sur un certificat que Windows n'approuve pas (FTPS, Bureau à distance) : premier usage, empreinte à
    /// comparer ; ou changement par rapport à l'empreinte épinglée <paramref name="pinned"/>, alerte qui ne s'accepte
    /// qu'après avoir coché « J'ai confirmé… ». « Annuler la connexion » par défaut.
    /// </summary>
    private Task<bool> ConfirmCertificateAsync(string title, string verifyHeading, string changedHeading, string address,
        (string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Sha256, string Problem) certificate, string? pinned)
    {
        static string Spaced(string sha256) => string.Join(" ", sha256.Chunk(16).Select(c => new string(c)));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        IReadOnlyList<string> details =
        [
            Text.Format(Strings.FtpCertSubject, certificate.Subject),
            Text.Format(Strings.FtpCertIssuer, certificate.Issuer),
            Text.Format(Strings.FtpCertValidity, certificate.NotBefore.ToString("d", culture), certificate.NotAfter.ToString("d", culture)),
        ];
        var request = pinned is null
            ? new ConfirmRequest
            {
                Title = title,
                Heading = verifyHeading,
                Subject = address,
                Message = Text.Format(Strings.FtpCertUnknownMessage, certificate.Problem),
                Bullets = details,
                Codes = [(Strings.FtpCertFingerprint, Spaced(certificate.Sha256))],
                Kind = ConfirmKind.Warning,
                Actions = [Strings.HostKeyTrust],
                CancelLabel = Strings.HostKeyCancel,
            }
            : new ConfirmRequest
            {
                Title = title,
                Banner = Strings.HostKeyChangedBanner,
                Heading = changedHeading,
                Subject = address,
                Message = Text.Format(Strings.FtpCertChangedMessage, certificate.Problem),
                Bullets = details,
                Codes =
                [
                    (Strings.FtpCertOld, Spaced(pinned)),
                    (Strings.FtpCertNew, Spaced(certificate.Sha256)),
                ],
                Kind = ConfirmKind.Danger,
                Actions = [Strings.FtpCertReplace],
                DangerAction = 0,
                CancelLabel = Strings.HostKeyCancel,
                Acknowledge = Strings.HostKeyAckServer,
            };
        return ConfirmDialog.ConfirmAsync(this, request);
    }

    /// <summary>
    /// Serveur qui chiffrait ses connexions et ne propose plus TLS : alerte d'interception possible, « Annuler la
    /// connexion » par défaut, et rien sans avoir coché « J'ai confirmé… ». Confirmé, le retrait est mémorisé.
    /// </summary>
    private async Task<bool> ConfirmFtpTlsRemovedAsync(KeePassTarget target)
    {
        if (!await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
            {
                Title = Strings.FtpCleartextTitle,
                Banner = Strings.HostKeyChangedBanner,
                Heading = Strings.FtpTlsRemovedHeading,
                Subject = target.Address,
                Message = Strings.FtpTlsRemovedMessage,
                Kind = ConfirmKind.Danger,
                Actions = [Strings.FtpCleartextAction],
                DangerAction = 0,
                CancelLabel = Strings.HostKeyCancel,
                Acknowledge = Strings.FtpTlsRemovedAck,
            }))
        {
            return false;
        }

        FtpTlsMemory.Forget(_settings, target.Host, target.Port);
        SaveSettings();
        return true;
    }

    private Task<bool> ConfirmFtpCleartextAsync(KeePassTarget target) =>
        ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
        {
            Title = Strings.FtpCleartextTitle,
            Heading = Strings.FtpCleartextHeading,
            Subject = target.Address,
            Message = Strings.FtpCleartextMessage,
            Kind = ConfirmKind.Warning,
            Actions = [Strings.FtpCleartextAction],
            DangerAction = 0,
        });
}
