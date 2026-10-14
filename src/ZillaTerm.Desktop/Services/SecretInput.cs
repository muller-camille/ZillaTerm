using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ZillaTerm.App.Views;

namespace ZillaTerm.App.Services;

/// <summary>
/// Mot de passe saisi dans un <see cref="PasswordBox"/>, rendu en tableau à effacer par l'appelant après usage. Avalonia
/// n'ayant pas de SecureString, le champ est vidé en même temps (<paramref name="clear"/>) pour ne pas garder plus longtemps
/// la chaîne affichée.
/// </summary>
internal static class SecretInput
{
    public static char[] Read(PasswordBox box, bool clear = false)
    {
        var chars = (box.Text ?? "").ToCharArray();
        if (clear)
        {
            box.Clear();
        }

        return chars;
    }

    /// <summary>Mot de passe en UTF-8 (clé KeePass, coffre local).</summary>
    public static byte[] ReadUtf8(PasswordBox box, bool clear = false)
    {
        var chars = Read(box, clear);
        try
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(chars)];
            Encoding.UTF8.GetBytes(chars, bytes);
            return bytes;
        }
        finally
        {
            Clear(chars);
        }
    }

    public static void Clear(char[] chars) => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
}
