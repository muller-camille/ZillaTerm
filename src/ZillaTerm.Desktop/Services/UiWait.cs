using Avalonia.Threading;

namespace ZillaTerm.App.Services;

/// <summary>
/// Question posée sur le fil de l'interface par un fil qui attend la réponse (clé d'hôte, mot de passe demandés par SSH.NET
/// pendant la connexion) : l'équivalent de <c>Dispatcher.Invoke(() => dialog.ShowDialog())</c> de WPF, avec les dialogues
/// asynchrones d'Avalonia.
/// </summary>
internal static class UiWait
{
    /// <summary>
    /// Exécute <paramref name="ask"/> sur le fil de l'interface et rend sa réponse. Depuis un autre fil, celui-ci attend ;
    /// depuis le fil de l'interface, une boucle de messages imbriquée tourne jusqu'à la réponse (comme ShowDialog en WPF).
    /// </summary>
    public static T Run<T>(Func<Task<T>> ask)
    {
        var dispatcher = Dispatcher.UIThread;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(ask).GetAwaiter().GetResult();
        }

        var task = ask();
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            // Réveille la boucle imbriquée à la fin de la question, même si elle se termine hors du fil de l'interface.
            _ = task.ContinueWith(_ => dispatcher.Post(() => frame.Continue = false, DispatcherPriority.Send),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            dispatcher.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }
}
