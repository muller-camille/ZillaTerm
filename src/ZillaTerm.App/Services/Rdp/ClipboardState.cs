using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// État du presse-papiers de Windows pour le journal de débogage des sessions Bureau à distance : numéro de séquence,
/// programme qui l'a rempli, formats présents (fichiers, texte, image…). Jamais son contenu ni le nom des fichiers. Le
/// presse-papiers n'est pas ouvert : le client FreeRDP, qui le lit au même moment, n'est jamais bloqué.
/// </summary>
internal static class ClipboardState
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfHdrop = 15;

    private static readonly (string Name, uint Format)[] Standard =
    [
        ("fichiers (CF_HDROP)", CfHdrop),
        ("texte", CfUnicodeText),
        ("image", CfDib),
    ];

    // Formats des fichiers copiés par l'Explorateur, ou mis par le client FreeRDP pour des fichiers de la session.
    private static readonly string[] Registered = ["FileGroupDescriptorW", "FileContents", "Preferred DropEffect", "Shell IDList Array"];

    public static string Describe()
    {
        var formats = new List<string>();
        foreach (var (name, format) in Standard)
        {
            if (IsClipboardFormatAvailable(format))
            {
                formats.Add(name);
            }
        }

        foreach (var name in Registered)
        {
            uint format = RegisterClipboardFormat(name);
            if (format != 0 && IsClipboardFormatAvailable(format))
            {
                formats.Add(name);
            }
        }

        return $"n° {GetClipboardSequenceNumber()}, rempli par {Owner()}, formats : "
            + (formats.Count > 0 ? string.Join(", ", formats) : "aucun des formats suivis");
    }

    private static string Owner()
    {
        var window = GetClipboardOwner();
        if (window == IntPtr.Zero)
        {
            return "aucune fenêtre";
        }

        GetWindowThreadProcessId(window, out var id);
        try
        {
            using var process = Process.GetProcessById(id);
            return $"{process.ProcessName} (processus {id})";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return $"processus {id}";
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
