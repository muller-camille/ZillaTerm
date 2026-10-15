using System.Runtime.InteropServices;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Contrôle Bureau à distance de Windows (ActiveX de mstscax.dll, celui qu'utilise mstsc). La version WPF l'héberge dans
/// WinForms (AxHost), qu'un projet net10.0 pour Windows, Linux et macOS n'a pas : il n'est pas dans cette version. Sans
/// lui, comme sur un poste où il manque, la fenêtre principale ouvre les sessions avec le client FreeRDP de ZillaTerm,
/// sinon avec mstsc (Windows) ou le client Bureau à distance du système, hors de l'application.
/// </summary>
internal static class RdpClientHost
{
    /// <summary>Toujours faux : le contrôle de Windows n'est pas hébergé par cette version.</summary>
    public static bool IsAvailable => false;

    /// <summary>Erreur d'un poste sans le contrôle (REGDB_E_CLASSNOTREG), comme dans la version WPF.</summary>
    public static COMException NotAvailable() => new("MsRdpClient", unchecked((int)0x80040154));
}
