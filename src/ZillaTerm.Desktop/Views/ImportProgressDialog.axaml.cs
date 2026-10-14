using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.App.Views;

/// <summary>
/// Seconde étape de l'import de comptes : création une par une (<c>POST /API/Accounts</c>) des lignes confirmées,
/// avec l'état de chaque ligne au fur et à mesure. À la fin, propose d'enregistrer le résultat en CSV (sans les mots
/// de passe). Chaque mot de passe est effacé de la mémoire après l'envoi de sa ligne, les autres à la fermeture.
/// </summary>
public partial class ImportProgressDialog : DialogWindow
{
    private readonly AccountImport _import = new();
    private readonly Func<NewAccount, CancellationToken, Task<PvwaAccount>> _create = (_, _) => Task.FromResult(new PvwaAccount());
    private readonly CancellationTokenSource _closing = new();
    private readonly List<ImportRowView> _rows = [];
    private bool _running;
    private bool _finished;
    private bool _stopRequested;
    private bool _closeWhenStopped;
    private bool _askingStop;
    private int _refused;
    private string _summary = "";

    public ImportProgressDialog()
    {
        InitializeComponent();
        AskToSave = _ => Task.FromResult(false);
        ConfirmStop = () => Task.FromResult(false);
    }

    public ImportProgressDialog(AccountImport import, Func<NewAccount, CancellationToken, Task<PvwaAccount>> create)
        : this()
    {
        _import = import;
        _create = create;
        _rows = import.Rows.Select(r => new ImportRowView(r)).ToList();
        AskToSave = summary => ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
        {
            Title = Strings.ImportTitle,
            Heading = Strings.ImportSaveHeading,
            Message = summary + "\n\n" + Strings.ImportSaveMessage,
            Actions = [Strings.ImportSaveAction],
            DefaultAction = 0,
            CancelLabel = Strings.DontSave,
        });
        ConfirmStop = () => ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
        {
            Title = Strings.ImportTitle,
            Heading = Strings.ImportStopHeading,
            Bullets = [Strings.ImportStopCurrent, Strings.ImportStopCreated],
            Kind = ConfirmKind.Warning,
            Actions = [Strings.ImportStopAction],
            CancelLabel = Strings.ImportContinue,
        });
        BuildColumns();
        RowsGrid.ItemsSource = _rows;
        HeadingText.Text = Text.Format(Strings.ImportRunning, import.Ready);
        Progress.Maximum = Math.Max(1, import.Ready);
        Opened += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _import.Clear();
        };
    }

    /// <summary>Nombre de comptes créés (la liste des comptes est à recharger s'il n'est pas nul).</summary>
    public int Created { get; private set; }

    /// <summary>Le PVWA a répondu que la session avait expiré : la fenêtre principale doit se déconnecter.</summary>
    public bool SessionExpired { get; private set; }

    /// <summary>Question posée à la fin de l'import (texte du bilan) : enregistrer le résultat en CSV ?</summary>
    internal Func<string, Task<bool>> AskToSave { get; set; }

    internal IReadOnlyList<ImportRowView> Rows => _rows;

    /// <summary>Fermeture demandée pendant l'import (Fermer, Échap, croix) : arrêter ? Non par défaut, l'import continue.</summary>
    internal Func<Task<bool>> ConfirmStop { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Pendant l'import : arrêt (après la ligne en cours) seulement si l'utilisateur le confirme, puis fermeture.
        if (_running)
        {
            e.Cancel = true;
            if (!_closeWhenStopped && !_askingStop)
            {
                _ = AskStopAsync();
            }
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// La question d'arrêt est asynchrone (Avalonia) : la fermeture est d'abord refusée, puis l'import s'arrête après la
    /// ligne en cours si l'utilisateur le confirme, et la fenêtre se ferme ensuite (<see cref="RunAsync"/>).
    /// </summary>
    private async Task AskStopAsync()
    {
        _askingStop = true;
        try
        {
            if (await ConfirmStop())
            {
                if (_running)
                {
                    _stopRequested = _closeWhenStopped = true;
                }
                else
                {
                    // Import fini pendant la question : la fermeture demandée a lieu maintenant.
                    Close();
                }
            }
        }
        finally
        {
            _askingStop = false;
        }
    }

    private void BuildColumns()
    {
        DataGridTextColumn Add(string header, string path, double width = 1, DataGridLengthUnitType unit = DataGridLengthUnitType.Star)
        {
            var column = new DataGridTextColumn { Header = header, Binding = GridBinding.OneWay(path), Width = new DataGridLength(width, unit) };
            RowsGrid.Columns.Add(column);
            return column;
        }

        Add(Strings.ImportColLine, nameof(ImportRowView.Line), 1, DataGridLengthUnitType.Auto);
        Add(CoreStrings.ColumnSafe, nameof(ImportRowView.Safe));
        Add(CoreStrings.ColumnPlatform, nameof(ImportRowView.Platform));
        Add(CoreStrings.ColumnServer, nameof(ImportRowView.Address), 1.5);
        Add(CoreStrings.ColumnUser, nameof(ImportRowView.UserName));
        Add(CoreStrings.ColumnDomain, nameof(ImportRowView.LogonDomain), 0.7);
        Add(Strings.ImportColPassword, nameof(ImportRowView.Password), 1, DataGridLengthUnitType.Auto);
        // Résultat en couleur (classes ResultText, Created, Refused…), qui suit l'état de la ligne pendant l'import.
        RowsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Strings.ImportColResult,
            Width = new DataGridLength(2, DataGridLengthUnitType.Star),
            ClipboardContentBinding = GridBinding.OneWay(nameof(ImportRowView.Status)),
            CellTemplate = new FuncDataTemplate<ImportRowView>((_, _) => ResultText()),
        });
    }

    /// <summary>Texte du résultat d'une ligne, avec une classe par état (couleur dans les styles de la fenêtre).</summary>
    private static TextBlock ResultText()
    {
        var text = new TextBlock { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Classes.Add("ResultText");
        text.Bind(TextBlock.TextProperty, GridBinding.OneWay(nameof(ImportRowView.Status)));
        foreach (var outcome in new[] { ImportOutcome.Created, ImportOutcome.Refused, ImportOutcome.NotImported, ImportOutcome.NotSent, ImportOutcome.Sending })
        {
            text.BindClass(outcome.ToString(),
                GridBinding.OneWay(nameof(ImportRowView.Outcome), new FuncValueConverter<ImportOutcome, bool>(value => value == outcome)), text);
        }

        return text;
    }

    /// <summary>Une ligne sur deux sur un fond teinté (le tableau d'Avalonia n'a pas d'AlternatingRowBackground).</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e) => e.Row.Classes.Set("Alternate", e.Row.Index % 2 == 1);

    /// <summary>Envoie les lignes prêtes une par une, puis propose d'enregistrer le résultat.</summary>
    internal async Task RunAsync()
    {
        if (_running || _finished)
        {
            return;
        }

        var pending = _rows.Where(r => r.Row.Outcome == ImportOutcome.Pending).ToList();
        int done = 0;
        SetRunning(true);
        try
        {
            foreach (var row in pending)
            {
                if (_stopRequested || SessionExpired)
                {
                    break;
                }

                row.SetOutcome(ImportOutcome.Sending);
                if (IsLoaded)
                {
                    RowsGrid.ScrollIntoView(row, null);
                }

                try
                {
                    var account = await _create(row.Row.Account!, _closing.Token);
                    row.Row.AccountId = account.Id;
                    row.SetOutcome(ImportOutcome.Created);
                    Created++;
                }
                catch (PvwaException ex) when (ex.IsUnauthorized)
                {
                    // Compte non créé : la ligne pourra être renvoyée après reconnexion.
                    SessionExpired = true;
                    row.Row.Detail = ErrorText.Describe(ex);
                    row.SetOutcome(ImportOutcome.NotSent);
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    row.SetOutcome(ImportOutcome.NotSent);
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    row.Row.Detail = ErrorText.Describe(ex);
                    row.SetOutcome(ImportOutcome.Refused);
                    _refused++;
                }
                finally
                {
                    if (row.Row.Account!.Secret is { } secret)
                    {
                        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secret.AsSpan()));
                    }

                    done++;
                    Progress.Value = done;
                    ProgressText.Text = Text.Format(Strings.ImportProgress, done, pending.Count, Created, _refused);
                }
            }
        }
        finally
        {
            foreach (var row in pending.Where(r => r.Row.Outcome == ImportOutcome.Pending))
            {
                row.SetOutcome(ImportOutcome.NotSent);
            }

            _finished = true;
            SetRunning(false);
        }

        _summary = SessionExpired ? Text.Format(Strings.ImportSessionExpired, Created, _refused)
            : pending.Any(r => r.Row.Outcome == ImportOutcome.NotSent) ? Text.Format(Strings.ImportStopped, Created, _refused)
            : Text.Format(Strings.ImportDone, Created, _refused);
        ProgressText.Text = _summary;
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        if (await AskToSave(_summary))
        {
            await SaveResultAsync();
        }

        if (SessionExpired || _closeWhenStopped)
        {
            Close();
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _stopRequested = true;
        StopButton.IsEnabled = false;
    }

    private async void OnSaveResult(object? sender, RoutedEventArgs e) => await SaveResultAsync();

    private async Task SaveResultAsync()
    {
        var path = await Pickers.SaveFileAsync(this, Strings.ExportFilter, $"{Strings.ImportResultFileName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Strings.ImportSaveResult.Replace("_", ""));
        if (path is null)
        {
            return;
        }

        try
        {
            WriteResult(path);
            ProgressText.Text = _summary + Environment.NewLine + Text.Format(Strings.ImportResultSaved, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.ExportFailed, ex.Message), ConfirmKind.Danger, Strings.ImportTitle);
        }
    }

    /// <summary>Résultat de chaque ligne en CSV (BOM UTF-8 pour Excel, séparateur de la région du système).</summary>
    internal void WriteResult(string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AccountCsv.WriteResults(writer, _import.Rows, CsvExporter.DefaultSeparator(System.Globalization.CultureInfo.CurrentCulture));
    }

    private void SetRunning(bool running)
    {
        _running = running;
        StopButton.IsVisible = running;
        SaveButton.IsEnabled = !running && _finished;
    }

    /// <summary>Ligne du tableau ; le mot de passe n'est jamais affiché, seulement sa présence.</summary>
    public sealed class ImportRowView(ImportRow row) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ImportRow Row { get; } = row;

        public int Line => Row.Line;

        public string Safe => Row.Safe;

        public string Platform => Row.Platform;

        public string Address => Row.Address;

        public string UserName => Row.UserName;

        public string LogonDomain => Row.LogonDomain;

        public string Password => Row.HasPassword ? "••••••" : "";

        public ImportOutcome Outcome => Row.Outcome;

        public string Status => Row.Outcome switch
        {
            ImportOutcome.Refused => Text.Format(Strings.ImportStatusRefused, Row.Detail),
            ImportOutcome.NotImported => Text.Format(Strings.ImportStatusError, Row.Error),
            _ => AccountCsv.OutcomeText(Row.Outcome),
        };

        public void SetOutcome(ImportOutcome outcome)
        {
            Row.Outcome = outcome;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Outcome)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
}
