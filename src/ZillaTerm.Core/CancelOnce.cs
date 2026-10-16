namespace ZillaTerm.Core;

/// <summary>
/// Annulation des opérations d'une fenêtre (fermeture, tentative remplacée) : <see cref="Cancel"/> annule puis libère la
/// source, une seule fois. Le jeton et son état restent lisibles ensuite : une opération encore en cours, ou qui le relit
/// après une attente, n'échoue pas sur une source libérée.
/// </summary>
public sealed class CancelOnce
{
    private readonly CancellationTokenSource _source = new();
    private int _cancelled;

    public CancelOnce() => Token = _source.Token;

    public CancellationToken Token { get; }

    public bool IsCancellationRequested => Token.IsCancellationRequested;

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) != 0)
        {
            return;
        }

        try
        {
            _source.Cancel();
        }
        finally
        {
            _source.Dispose();
        }
    }
}
