namespace ZillaTerm.App.Services;

// Dans ZillaTerm.App, ce type est déclaré dans WindowCapture.cs (capture de fenêtres Windows, non porté ici) ; il sert
// aussi au partage d'écran (ScreenShareSource). Si WindowCapture est porté, ce fichier disparaît avec lui.

/// <summary>Pixels d'une capture : BGRA 32 bits, de haut en bas, <see cref="Width"/> × 4 octets par ligne, au début de
/// <see cref="Bgra"/> (qui peut être plus long).</summary>
public readonly record struct ScreenPixels(byte[] Bgra, int Width, int Height);
