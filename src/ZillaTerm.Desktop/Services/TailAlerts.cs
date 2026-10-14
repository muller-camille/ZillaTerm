using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Tmds.DBus.Protocol;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services;

/// <summary>
/// Signaux d'une alerte du suivi de fichier : bouton de la barre des tâches qui clignote, et notification Windows.
/// La notification ne contient jamais la ligne du journal (elle peut s'afficher sur l'écran verrouillé et reste dans
/// le centre de notifications) : seulement le nombre de lignes et le nom du fichier.
/// </summary>
/// <remarks>
/// <para>
/// Windows : le bouton clignote (FlashWindowEx) ; la notification de ZillaTerm.App (icône de la zone de notification de
/// WinForms) n'a pas d'équivalent dans Avalonia : pas de notification.
/// </para>
/// <para>
/// Linux (X11) : la fenêtre demande l'attention (_NET_WM_STATE_DEMANDS_ATTENTION et indice d'urgence, retirés quand elle
/// passe au premier plan) avec le son de l'alerte du serveur X ; notification du bureau (org.freedesktop.Notifications),
/// dont un clic ramène la fenêtre. macOS : l'icône du Dock rebondit jusqu'à l'activation, avec le son d'alerte ;
/// notification du Centre de notifications (sans retour au clic). Ce qui manque (Wayland sans X11, pas de serveur de
/// notifications) ne fait rien, sans erreur.
/// </para>
/// </remarks>
internal static class TailAlerts
{
    private const uint FlashAll = 0x3;
    private const uint FlashUntilForeground = 0xC;

    private static Window? _target;
    private static Action? _onClick;

    /// <summary>Fait clignoter le bouton de la fenêtre dans la barre des tâches jusqu'à ce qu'elle passe au premier plan.</summary>
    public static void Flash(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashInfo>(),
                    Window = handle.Handle,
                    Flags = FlashAll | FlashUntilForeground,
                };
                FlashWindowEx(ref info);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacAttention.Request();
            }
            else if (handle.HandleDescriptor == "XID" && !window.IsActive)
            {
                X11Attention.Request(window, handle.Handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DebugLog.Write("tail", "signal de la fenêtre impossible", ex);
        }
    }

    /// <summary>Notification Windows ; un clic ramène la fenêtre et appelle <paramref name="onClick"/>.</summary>
    public static void Notify(Window window, string title, string text, Action? onClick)
    {
        try
        {
            _target = window;
            _onClick = onClick;
            if (OperatingSystem.IsMacOS())
            {
                MacNotify(title, text);
            }
            else if (!OperatingSystem.IsWindows())
            {
                _ = DesktopNotifications.ShowAsync(title, text);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException or IOException)
        {
            DebugLog.Write("tail", "notification impossible", ex);
        }
    }

    /// <summary>Fenêtre fermée : un clic sur une ancienne notification ne la vise plus.</summary>
    public static void Forget(Window window)
    {
        if (ReferenceEquals(_target, window))
        {
            _target = null;
            _onClick = null;
        }
    }

    /// <summary>Retire l'icône de la zone de notification (plus aucune fenêtre de suivi).</summary>
    public static void Hide()
    {
        _target = null;
        _onClick = null;
        DesktopNotifications.CloseLast();
    }

    private static void ShowTarget()
    {
        if (_target is not { IsVisible: true } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        _onClick?.Invoke();
    }

    /// <summary>
    /// macOS : notification par osascript ; titre et texte passés comme arguments du script (argv), jamais insérés dans
    /// son texte.
    /// </summary>
    private static void MacNotify(string title, string text)
    {
        var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
        foreach (var arg in (string[])
                 [
                     "-e", "on run argv",
                     "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                     "-e", "end run",
                     title, text,
                 ])
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info);
    }

    /// <summary>
    /// Notifications du bureau (Linux) : org.freedesktop.Notifications sur le bus de session. Une seule à la fois (la
    /// suivante la remplace) ; son action par défaut (clic) ramène la fenêtre.
    /// </summary>
    private static class DesktopNotifications
    {
        private const string Service = "org.freedesktop.Notifications";
        private const string ObjectPath = "/org/freedesktop/Notifications";

        private static DBusConnection? _connection;
        private static IDisposable? _clicks;
        private static uint _last;

        public static async Task ShowAsync(string title, string text)
        {
            try
            {
                var connection = await ConnectAsync();
                _last = await connection.CallMethodAsync(NotifyMessage(connection, _last, title, text),
                    static (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Pas de bus de session ni de serveur de notifications : rien.
                DebugLog.Write("tail", "notification impossible", ex);
            }
        }

        /// <summary>Notify(application, remplace, icône, titre, texte, actions, indications, durée en ms).</summary>
        private static MessageBuffer NotifyMessage(DBusConnection connection, uint replaces, string title, string text)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Notify", "susssasa{sv}i", MessageFlags.None);
            writer.WriteString("ZillaTerm");
            writer.WriteUInt32(replaces);
            writer.WriteString("dialog-warning");
            writer.WriteString(title);
            writer.WriteString(text);
            // Action « default » : le clic sur la notification.
            writer.WriteArray(new[] { "default", title });
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["urgency"] = VariantValue.Byte(1),
                ["sound-name"] = VariantValue.String("dialog-warning"),
            });
            writer.WriteInt32(8000);
            return writer.CreateMessage();
        }

        /// <summary>Retire la dernière notification, si le serveur la montre encore.</summary>
        public static void CloseLast()
        {
            if (_connection is not { } connection || _last == 0)
            {
                return;
            }

            uint id = _last;
            _last = 0;
            try
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(Service, ObjectPath, Service, "CloseNotification", "u", MessageFlags.NoReplyExpected);
                writer.WriteUInt32(id);
                connection.TrySendMessage(writer.CreateMessage());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DebugLog.Write("tail", "notification non retirée", ex);
            }
        }

        // Connexion en cours d'ouverture (notifications rapprochées : une seule connexion) ; oubliée si elle échoue.
        private static Task<DBusConnection>? _connecting;

        private static async Task<DBusConnection> ConnectAsync()
        {
            _connecting ??= ConnectCoreAsync();
            try
            {
                return await _connecting;
            }
            catch
            {
                _connecting = null;
                throw;
            }
        }

        private static async Task<DBusConnection> ConnectCoreAsync()
        {
            var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("Bus de session D-Bus inconnu."));
            try
            {
                await connection.ConnectAsync();
                _clicks = await connection.WatchSignalAsync(Service, ObjectPath, Service, "ActionInvoked",
                    static (Message m, object? _) =>
                    {
                        var reader = m.GetBodyReader();
                        return (Id: reader.ReadUInt32(), Action: reader.ReadString());
                    },
                    (Notification<(uint Id, string Action)> n) =>
                    {
                        if (!n.IsCompletion && n.HasValue && n.Value.Id != 0 && n.Value.Id == _last && n.Value.Action == "default")
                        {
                            Dispatcher.UIThread.Post(ShowTarget);
                        }
                    }, ObserverFlags.None, emitOnCapturedContext: false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            _connection = connection;
            return connection;
        }
    }

    /// <summary>
    /// X11 : demande d'attention au gestionnaire de fenêtres (EWMH), qui la retire quand la fenêtre est activée, et indice
    /// d'urgence (ICCCM), retiré ici à l'activation ; son d'alerte du serveur X. Connexion à part de celle d'Avalonia.
    /// </summary>
    private static class X11Attention
    {
        private const int ClientMessage = 33;
        private const long SubstructureRedirectMask = 1L << 20;
        private const long SubstructureNotifyMask = 1L << 19;
        private const long UrgencyHint = 1L << 8;
        private const int NetWmStateAdd = 1;

        private static nint _display;

        public static void Request(Window window, nint xid)
        {
            if (_display == 0)
            {
                _display = XOpenDisplay(0);
                if (_display == 0)
                {
                    return;
                }
            }

            var root = XDefaultRootWindow(_display);
            var message = new XClientMessageEvent
            {
                Type = ClientMessage,
                SendEvent = 1,
                Display = _display,
                Window = xid,
                MessageType = XInternAtom(_display, "_NET_WM_STATE", false),
                Format = 32,
                Data0 = NetWmStateAdd,
                Data1 = XInternAtom(_display, "_NET_WM_STATE_DEMANDS_ATTENTION", false),
                Data3 = 1,
            };
            XSendEvent(_display, root, false, (nint)(SubstructureRedirectMask | SubstructureNotifyMask), ref message);
            SetUrgency(xid, true);
            XBell(_display, 0);
            XFlush(_display);
            window.Activated -= OnActivated;
            window.Activated += OnActivated;
        }

        /// <summary>Fenêtre passée au premier plan : l'indice d'urgence est retiré (le gestionnaire retire sa demande).</summary>
        private static void OnActivated(object? sender, EventArgs e)
        {
            if (sender is not Window window)
            {
                return;
            }

            window.Activated -= OnActivated;
            if (window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle && _display != 0)
            {
                SetUrgency(handle.Handle, false);
                XFlush(_display);
            }
        }

        private static void SetUrgency(nint xid, bool urgent)
        {
            // Pas encore d'indications sur la fenêtre (Avalonia n'en pose pas) : une structure vide, allouée par Xlib.
            var hints = XGetWMHints(_display, xid);
            if (hints == 0 && urgent)
            {
                hints = XAllocWMHints();
            }

            if (hints == 0)
            {
                return;
            }

            // Premier champ de XWMHints : « flags » (long).
            long flags = Marshal.ReadIntPtr(hints);
            Marshal.WriteIntPtr(hints, (nint)(urgent ? flags | UrgencyHint : flags & ~UrgencyHint));
            XSetWMHints(_display, xid, hints);
            XFree(hints);
        }

        // XEvent : 24 « long » ; XClientMessageEvent au début.
        [StructLayout(LayoutKind.Sequential)]
        private struct XClientMessageEvent
        {
            public int Type;
            public nint Serial;
            public int SendEvent;
            public nint Display;
            public nint Window;
            public nint MessageType;
            public int Format;
            public nint Data0;
            public nint Data1;
            public nint Data2;
            public nint Data3;
            public nint Data4;
            public nint Pad0;
            public nint Pad1;
            public nint Pad2;
            public nint Pad3;
            public nint Pad4;
            public nint Pad5;
            public nint Pad6;
            public nint Pad7;
            public nint Pad8;
            public nint Pad9;
            public nint Pad10;
            public nint Pad11;
            public nint Pad12;
        }

        [DllImport("libX11.so.6")]
        private static extern nint XOpenDisplay(nint name);

        [DllImport("libX11.so.6")]
        private static extern nint XDefaultRootWindow(nint display);

        [DllImport("libX11.so.6")]
        private static extern nint XInternAtom(nint display, string name, bool onlyIfExists);

        [DllImport("libX11.so.6")]
        private static extern int XSendEvent(nint display, nint window, bool propagate, nint eventMask, ref XClientMessageEvent e);

        [DllImport("libX11.so.6")]
        private static extern nint XGetWMHints(nint display, nint window);

        [DllImport("libX11.so.6")]
        private static extern nint XAllocWMHints();

        [DllImport("libX11.so.6")]
        private static extern int XSetWMHints(nint display, nint window, nint hints);

        [DllImport("libX11.so.6")]
        private static extern int XFree(nint data);

        [DllImport("libX11.so.6")]
        private static extern int XBell(nint display, int percent);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(nint display);
    }

    /// <summary>macOS : l'icône du Dock rebondit jusqu'à l'activation de l'application (NSCriticalRequest), avec le son d'alerte.</summary>
    private static class MacAttention
    {
        private const nint CriticalRequest = 0;

        public static void Request()
        {
            var app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != 0)
            {
                objc_msgSend(app, sel_registerName("requestUserAttention:"), CriticalRequest);
            }

            NSBeep();
        }

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern nint objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern nint sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern nint objc_msgSend(nint receiver, nint selector);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern nint objc_msgSend(nint receiver, nint selector, nint argument);

        [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
        private static extern void NSBeep();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
