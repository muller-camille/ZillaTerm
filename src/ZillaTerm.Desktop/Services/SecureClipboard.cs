using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services;

/// <summary>
/// Presse-papiers pour un mot de passe : copié directement depuis un tableau de caractères (sans chaîne .NET), marqué
/// pour être exclu de l'historique (Win+V), de la synchronisation entre appareils et des outils de surveillance du
/// presse-papiers, puis effacé après un délai s'il contient toujours ce mot de passe.
/// </summary>
/// <remarks>
/// <para>Windows : comme dans ZillaTerm.App (API du presse-papiers de Windows).</para>
/// <para>
/// Linux (X11) et macOS : presse-papiers d'Avalonia. Le mot de passe est gardé dans un tableau effacé dès qu'il quitte le
/// presse-papiers ; il n'est converti en chaîne .NET (non effaçable) qu'au moment où une application le colle. Il est
/// marqué secret pour les gestionnaires d'historique qui le reconnaissent : « x-kde-passwordManagerHint » (Klipper de KDE,
/// CopyQ…) sous Linux, « org.nspasteboard.ConcealedType » sous macOS ; il n'y a pas d'équivalent pour les autres, ni pour
/// le presse-papiers universel d'Apple. Effacement au bout du délai et à la fermeture s'il contient encore ce mot de
/// passe : toujours celui de ZillaTerm, ou le même texte repris par un gestionnaire de presse-papiers.
/// </para>
/// </remarks>
internal sealed class SecureClipboard : IDisposable, IAsyncDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    /// <summary>Nouvel essai quand le presse-papiers est occupé au moment de l'effacer.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _delay;
    private readonly TopLevel _owner;
    private uint _sequence;
    private bool _disposed;

    // Hors de Windows : contenu placé par ZillaTerm, et empreinte (HMAC à clé aléatoire) du mot de passe pour le reconnaître
    // s'il a été repris par un gestionnaire de presse-papiers.
    private SecretTransfer? _copied;
    private byte[]? _key;
    private byte[]? _fingerprint;

    /// <param name="owner">Fenêtre propriétaire du presse-papiers (requise par Windows pour y écrire).</param>
    /// <param name="cleared">Appelé quand le mot de passe est effacé à la fin du délai.</param>
    public SecureClipboard(TopLevel owner, TimeSpan delay, Action cleared)
    {
        _owner = owner;
        _delay = delay;
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += async (_, _) =>
        {
            if (await ClearAsync())
            {
                cleared();
            }
        };
    }

    public TimeSpan Delay => _delay;

    /// <summary>Copie <paramref name="secret"/> ; faux si une autre application garde le presse-papiers ouvert.</summary>
    public async Task<bool> CopyAsync(char[] secret)
    {
        if (OperatingSystem.IsWindows())
        {
            return CopyWindows(secret);
        }

        if (_owner.Clipboard is not { } clipboard)
        {
            return false;
        }

        var transfer = new SecretTransfer(secret);
        try
        {
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("clipboard", "Copie du mot de passe impossible", ex);
            transfer.Wipe();
            return false;
        }

        _copied?.Wipe();
        _copied = transfer;
        _key = RandomNumberGenerator.GetBytes(32);
        _fingerprint = Fingerprint(_key, secret);
        RestartTimer(_delay);
        return true;
    }

    /// <summary>
    /// Vide le presse-papiers s'il contient encore le mot de passe copié ; vrai s'il a été vidé. Occupé par une autre
    /// application (gestionnaire de presse-papiers, bureau à distance) : nouvel essai une seconde plus tard, tant
    /// qu'il contient ce mot de passe.
    /// </summary>
    public Task<bool> ClearAsync() => OperatingSystem.IsWindows() ? Task.FromResult(ClearWindows(attempts: 10)) : ClearOtherAsync();

    /// <summary>À la fermeture : plus d'essai possible ensuite, on insiste davantage.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (OperatingSystem.IsWindows())
        {
            ClearWindows(attempts: 100);
        }
        else
        {
            _ = ClearOtherAsync();
        }
    }

    /// <summary>Comme <see cref="Dispose"/>, en attendant l'effacement (Linux, macOS : presse-papiers asynchrone).</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (OperatingSystem.IsWindows())
        {
            ClearWindows(attempts: 100);
        }
        else
        {
            await ClearOtherAsync();
        }
    }

    private void RestartTimer(TimeSpan interval)
    {
        _timer.Stop();
        _timer.Interval = interval;
        _timer.Start();
    }

    // ===================== Linux, macOS =====================

    private async Task<bool> ClearOtherAsync()
    {
        _timer.Stop();
        if (_copied is not { } copied || _owner.Clipboard is not { } clipboard)
        {
            // Rien de copié.
            return false;
        }

        try
        {
            // Toujours le contenu de ZillaTerm, ou le même texte repris par un gestionnaire de presse-papiers : effacé.
            // Autre chose (l'utilisateur a copié autre chose entre-temps) : on n'y touche pas.
            bool ours = ReferenceEquals(await clipboard.TryGetInProcessDataAsync(), copied) || await HoldsSecretAsync(clipboard);
            if (ours)
            {
                await clipboard.ClearAsync();
            }

            Forget();
            return ours;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("clipboard", "Effacement du mot de passe copié impossible", ex);
            if (!_disposed)
            {
                // Le mot de passe y est peut-être encore : le minuteur réessaiera.
                RestartTimer(RetryDelay);
            }

            return false;
        }
    }

    /// <summary>Texte actuel du presse-papiers égal au mot de passe copié (comparé par son empreinte).</summary>
    private async Task<bool> HoldsSecretAsync(IClipboard clipboard)
    {
        if (_key is null || _fingerprint is null || await clipboard.TryGetTextAsync() is not { } text)
        {
            return false;
        }

        var chars = text.ToCharArray();
        try
        {
            return CryptographicOperations.FixedTimeEquals(Fingerprint(_key, chars), _fingerprint);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    private void Forget()
    {
        _copied?.Wipe();
        _copied = null;
        _key = null;
        _fingerprint = null;
    }

    private static byte[] Fingerprint(byte[] key, char[] secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return HMACSHA256.HashData(key, bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// Contenu placé dans le presse-papiers : le texte (créé à chaque demande d'une application qui colle) et la marque
    /// « secret » des gestionnaires d'historique. Avalonia le libère quand il quitte le presse-papiers : la copie du mot de
    /// passe est alors effacée.
    /// </summary>
    private sealed class SecretTransfer : IAsyncDataTransfer, IAsyncDataTransferItem
    {
        // Linux (X11) : indication reconnue par Klipper (KDE), CopyQ et d'autres gestionnaires de presse-papiers.
        private static readonly DataFormat<byte[]> KdePasswordHint = DataFormat.CreateBytesPlatformFormat("x-kde-passwordManagerHint");

        // macOS : type « caché » de nspasteboard.org, reconnu par les gestionnaires d'historique du presse-papiers.
        private static readonly DataFormat<byte[]> MacConcealed = DataFormat.CreateBytesPlatformFormat("org.nspasteboard.ConcealedType");

        private readonly char[] _secret;
        private readonly DataFormat[] _formats;

        public SecretTransfer(char[] secret)
        {
            _secret = (char[])secret.Clone();
            _formats = [DataFormat.Text, OperatingSystem.IsMacOS() ? MacConcealed : KdePasswordHint];
        }

        public IReadOnlyList<DataFormat> Formats => _formats;

        public IReadOnlyList<IAsyncDataTransferItem> Items => [this];

        public Task<object?> TryGetRawAsync(DataFormat format)
        {
            lock (_secret)
            {
                object? value = format == DataFormat.Text ? new string(_secret)
                    : format == KdePasswordHint ? "secret"u8.ToArray()
                    : format == MacConcealed ? Array.Empty<byte>()
                    : null;
                return Task.FromResult(value);
            }
        }

        public void Wipe()
        {
            lock (_secret)
            {
                Array.Clear(_secret);
            }
        }

        public void Dispose() => Wipe();
    }

    // ===================== Windows =====================

    private bool CopyWindows(char[] secret)
    {
        if (!Open())
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var text = GlobalAlloc(GmemMoveable, (nuint)((secret.Length + 1) * sizeof(char)));
            if (text == 0)
            {
                throw new Win32Exception();
            }

            var pointer = GlobalLock(text);
            if (pointer == 0)
            {
                GlobalFree(text);
                throw new Win32Exception();
            }

            Marshal.Copy(secret, 0, pointer, secret.Length);
            Marshal.WriteInt16(pointer, secret.Length * sizeof(char), 0);
            GlobalUnlock(text);
            if (SetClipboardData(CfUnicodeText, text) == 0)
            {
                // Mémoire non prise par Windows : effacée et libérée ici.
                ZeroAndFree(text, secret.Length + 1);
                throw new Win32Exception();
            }

            // Formats reconnus par Windows et par les outils de gestion du presse-papiers.
            SetDword("ExcludeClipboardContentFromMonitorProcessing", 0);
            SetDword("CanIncludeInClipboardHistory", 0);
            SetDword("CanUploadToCloudClipboard", 0);
        }
        finally
        {
            CloseClipboard();
        }

        _sequence = GetClipboardSequenceNumber();
        RestartTimer(_delay);
        return true;
    }

    private bool ClearWindows(int attempts)
    {
        _timer.Stop();
        if (_sequence == 0 || GetClipboardSequenceNumber() != _sequence)
        {
            // Rien de copié, ou l'utilisateur a copié autre chose entre-temps : on n'y touche pas.
            _sequence = 0;
            return false;
        }

        bool emptied = false;
        if (Open(attempts))
        {
            try
            {
                emptied = EmptyClipboard();
            }
            finally
            {
                CloseClipboard();
            }
        }

        if (emptied)
        {
            _sequence = 0;
        }
        else if (!_disposed)
        {
            // Le mot de passe y est encore : le minuteur réessaiera.
            RestartTimer(RetryDelay);
        }

        return emptied;
    }

    private bool Open(int attempts = 10)
    {
        var window = _owner.TryGetPlatformHandle()?.Handle ?? 0;

        // Une autre application peut garder le presse-papiers ouvert un court instant.
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (OpenClipboard(window))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static void SetDword(string format, int value)
    {
        uint id = RegisterClipboardFormat(format);
        if (id == 0)
        {
            return;
        }

        var data = GlobalAlloc(GmemMoveable, sizeof(int));
        if (data == 0)
        {
            return;
        }

        var pointer = GlobalLock(data);
        if (pointer == 0)
        {
            GlobalFree(data);
            return;
        }

        Marshal.WriteInt32(pointer, value);
        GlobalUnlock(data);
        if (SetClipboardData(id, data) == 0)
        {
            GlobalFree(data);
        }
    }

    private static void ZeroAndFree(nint memory, int chars)
    {
        var pointer = GlobalLock(memory);
        if (pointer != 0)
        {
            for (int i = 0; i < chars; i++)
            {
                Marshal.WriteInt16(pointer, i * sizeof(char), 0);
            }

            GlobalUnlock(memory);
        }

        GlobalFree(memory);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
