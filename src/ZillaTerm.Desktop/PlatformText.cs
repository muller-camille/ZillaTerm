namespace ZillaTerm.App.Localization;

/// <summary>
/// Textes qui parlent de Windows (compte Windows, Bloc-notes, pare-feu Windows…) : sous Linux et macOS, leur variante
/// neutre (« …Unix »). Les fenêtres les prennent ici plutôt que dans <see cref="Strings"/>.
/// </summary>
public static class PlatformText
{
    private static bool Windows => OperatingSystem.IsWindows();

    public static string AboutDescription => Windows ? Strings.AboutDescription : Strings.AboutDescriptionUnix;

    public static string LocalStoreCreateIntro => Windows ? Strings.LocalStoreCreateIntro : Strings.LocalStoreCreateIntroUnix;

    public static string RetrieveClipboard => Windows ? Strings.RetrieveClipboard : Strings.RetrieveClipboardUnix;

    public static string KeePassRememberHint => Windows ? Strings.KeePassRememberHint : Strings.KeePassRememberHintUnix;

    public static string ShareWarningEnds => Windows ? Strings.ShareWarningEnds : Strings.ShareWarningEndsUnix;

    public static string SshClientNotFound => Windows ? Strings.SshClientNotFound : Strings.SshClientNotFoundUnix;

    public static string TextEditorHelp => Windows ? Strings.TextEditorHelp : Strings.TextEditorHelpUnix;

    public static string KeepAlive => Windows ? Strings.KeepAlive : Strings.KeepAliveUnix;

    public static string AppThemeSystem => Windows ? Strings.AppThemeSystem : Strings.AppThemeSystemUnix;

    public static string CompareToolHelp => Windows ? Strings.CompareToolHelp : Strings.CompareToolHelpUnix;

    public static string TailNotify => Windows ? Strings.TailNotify : Strings.TailNotifyUnix;

    public static string TailNotifyTip => Windows ? Strings.TailNotifyTip : Strings.TailNotifyTipUnix;

    public static string X11ForwardingTip => Windows ? Strings.X11ForwardingTip : Strings.X11ForwardingTipUnix;

    public static string X11DisplayHelp => Windows ? Strings.X11DisplayHelp : Strings.X11DisplayHelpUnix;

    public static string SettingsShareDirectHelp => Windows ? Strings.SettingsShareDirectHelp : Strings.SettingsShareDirectHelpUnix;
}
