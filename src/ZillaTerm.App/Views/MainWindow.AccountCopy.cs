using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Views;

/// <summary>
/// Liste des comptes : copie chiffrée de la dernière liste complète (affichée aussitôt à la connexion suivante), texte
/// du chargement, et arbre « Disponibles » gardé tel quel quand la liste est remplacée sous les yeux.
/// </summary>
public partial class MainWindow
{
    /// <summary>Copies de ce poste, chiffrées pour le compte Windows (DPAPI), propres à cet usage.</summary>
    private static readonly AccountListCopy AccountCopies =
        new(AccountListCopy.DefaultDirectory, new DpapiProtector("ZillaTerm.AccountList.v1"u8.ToArray()));

    /// <summary>
    /// Liste affichée tirée de la copie (heure de son chargement depuis le PVWA), tant que le PVWA ne l'a pas remplacée ;
    /// null sinon.
    /// </summary>
    private DateTime? _listFromCopy;

    /// <summary>Texte du nombre de comptes pendant le chargement (progression, liste de la copie en cours de mise à jour).</summary>
    private string _loadingText = "";

    /// <summary>Arbre reconstruit avec une nouvelle liste : sa sélection perdue en route ne change pas la cible des boutons.</summary>
    private bool _keepingTreeView;

    /// <summary>
    /// État de l'arbre pas encore rétabli après une reconstruction : une autre reconstruction juste derrière (dernière page,
    /// puis liste complète) le reprend, l'arbre n'ayant alors plus ni sélection ni défilement.
    /// </summary>
    private TreeViewState? _pendingTreeView;

    /// <summary>
    /// Écritures des copies, l'une après l'autre, toutes fenêtres confondues (une reconnexion ouvre une nouvelle fenêtre
    /// pendant que l'ancienne écrit peut-être encore) : jamais deux à la fois sur le même fichier. Fil de l'interface.
    /// </summary>
    private static Task _copyWrite = Task.CompletedTask;

    /// <summary>Copie de la dernière liste de cet utilisateur sur ce PVWA, lue hors du fil de l'interface ; null sans copie.</summary>
    private async Task<SavedAccountList?> ReadAccountCopyAsync()
    {
        string host = PvwaHost, user = _sessionUser;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _copyWrite;
            var copy = await Task.Run(() => AccountCopies.Read(host, user, DateTime.UtcNow));
            if (copy is not null)
            {
                DebugLog.Write("comptes", string.Create(CultureInfo.InvariantCulture,
                    $"Copie de la liste lue : {copy.Accounts.Count} comptes du {copy.SavedUtc:u}, {watch.ElapsedMilliseconds} ms"));
            }

            return copy;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DebugLog.Write("comptes", "Copie de la liste des comptes non lue", e);
            return null;
        }
    }

    /// <summary>
    /// Liste complète lue au PVWA : devient la copie (hors du fil de l'interface), ou, l'option désactivée, les copies sont
    /// supprimées. Un échec n'est que noté dans le journal de débogage : la copie n'est qu'un raccourci.
    /// </summary>
    private void SaveAccountCopy(IReadOnlyList<PvwaAccount> accounts)
    {
        string host = PvwaHost, user = _sessionUser;
        bool keep = _settings.KeepAccountListCopy;
        var snapshot = keep ? accounts.ToArray() : [];
        var savedUtc = DateTime.UtcNow;
        _copyWrite = _copyWrite.ContinueWith(_ =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (keep)
                {
                    AccountCopies.Write(host, user, snapshot, savedUtc);
                    DebugLog.Write("comptes", string.Create(CultureInfo.InvariantCulture,
                        $"Copie de la liste écrite : {snapshot.Length} comptes, {watch.ElapsedMilliseconds} ms"));
                }
                else
                {
                    AccountCopies.DeleteAll();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DebugLog.Write("comptes", "Copie de la liste des comptes non écrite", e);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Option désactivée (Paramètres, fichier d'environnement) : les copies sont supprimées aussitôt.</summary>
    private void ApplyAccountCopySetting()
    {
        if (!_settings.KeepAccountListCopy)
        {
            SaveAccountCopy([]);
        }
    }

    /// <summary>Texte du nombre de comptes pendant le chargement : progression, et date de la liste de la copie affichée.</summary>
    private void SetLoadingText(int loaded, int total)
    {
        _loadingText = _listFromCopy is { } saved
            ? total > 0 ? Text.Format(Strings.AccountsFromCopyProgress, CopyDateText(saved), loaded, total)
                : Text.Format(Strings.AccountsFromCopy, CopyDateText(saved))
            : total > 0 ? Text.Format(Strings.LoadingAccountsProgress, loaded, total)
                : Strings.LoadingAccounts;
        if (_loading)
        {
            CountText.Text = _loadingText;
        }
    }

    /// <summary>Date et heure locales du chargement de la liste de la copie (date courte et heure).</summary>
    private static string CopyDateText(DateTime savedUtc) => savedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>État de l'arbre « Disponibles » à garder : dossiers ouverts, compte sélectionné, défilement, focus.</summary>
    private sealed record TreeViewState(HashSet<string> Expanded, string? SelectedId, double Offset, bool HadFocus)
    {
        /// <param name="pending">État encore à rétablir (reconstruction précédente) : sa sélection et son défilement priment.</param>
        public static TreeViewState Save(TreeView tree, TreeViewState? pending) => new(
            tree.ItemsSource is IReadOnlyList<FolderNode> folders
                ? folders.Where(f => f.IsExpanded).Select(f => f.Name).ToHashSet(StringComparer.Ordinal)
                : [],
            pending is not null ? pending.SelectedId : tree.SelectedItem is AccountNode node ? node.Account.Id : null,
            pending?.Offset ?? ScrollOf(tree)?.VerticalOffset ?? 0,
            pending?.HadFocus ?? tree.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// Après la reconstruction de l'arbre : même défilement, et le compte qui était sélectionné l'est à nouveau (s'il est
    /// toujours là et visible), sans changer la cible des boutons au passage.
    /// </summary>
    private void RestoreTreeView(TreeViewState view, IReadOnlyList<FolderNode> folders)
    {
        if (!ReferenceEquals(SessionTree.ItemsSource, folders))
        {
            return;
        }

        _pendingTreeView = null;
        ScrollOf(SessionTree)?.ScrollToVerticalOffset(view.Offset);
        if (view.SelectedId is null)
        {
            return;
        }

        SessionTree.UpdateLayout();
        foreach (var folder in folders.Where(f => f.IsExpanded))
        {
            var node = folder.Children.FirstOrDefault(n => n.Account.Id == view.SelectedId);
            if (node is null)
            {
                continue;
            }

            if (SessionTree.ItemContainerGenerator.ContainerFromItem(folder) is TreeViewItem folderItem
                && folderItem.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item)
            {
                _keepingTreeView = true;
                try
                {
                    item.IsSelected = true;
                }
                finally
                {
                    _keepingTreeView = false;
                }

                if (view.HadFocus)
                {
                    item.Focus();
                }
            }

            return;
        }
    }

    private static ScrollViewer? ScrollOf(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll)
            {
                return scroll;
            }

            if (ScrollOf(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
