using Avalonia.Threading;

namespace ZillaTerm.App;

/// <summary>
/// Réponse à une question (dialogue asynchrone d'Avalonia) attendue par un appel synchrone : rappel d'une bibliothèque
/// (certificat TLS, connexion en clair…), comme Dispatcher.Invoke + ShowDialog sous WPF. Depuis un autre fil, la question
/// est posée sur le fil de l'interface et le fil appelant attend ; depuis le fil de l'interface, une boucle de messages
/// imbriquée garde l'interface vivante jusqu'à la réponse.
/// </summary>
internal static class UiWait
{
    public static T Run<T>(Func<Task<T>> question)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(question).GetAwaiter().GetResult();
        }

        var task = question();
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }
}
