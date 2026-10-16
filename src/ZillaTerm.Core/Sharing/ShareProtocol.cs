using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Sharing;

/// <summary>Contenu d'un partage : un terminal (texte et couleurs), ou un écran (Bureau à distance, VNC).</summary>
public enum ShareKind : byte
{
    Terminal = 1,
    Screen = 2,
}

/// <summary>Messages chiffrés d'un partage, après l'ouverture de l'échange.</summary>
public enum ShareMessageType : byte
{
    // De celui qui partage vers celui qui regarde.
    Info = 1,
    TerminalReset = 2,
    TerminalData = 3,
    ScreenFrame = 4,
    Control = 5,
    Notice = 6,
    Bye = 7,
    Ping = 8,

    // De celui qui regarde vers celui qui partage.
    Join = 16,
    Input = 17,
    ControlRequest = 18,
    Leave = 19,
    Pong = 20,
}

/// <summary>Pourquoi un partage ou une personne qui regarde s'arrête.</summary>
public enum ShareEndReason : byte
{
    Stopped = 1,
    Refused = 2,
    Excluded = 3,
    Expired = 4,
    SessionClosed = 5,
    Locked = 6,
    TooManyAttempts = 7,
    Left = 8,
    Lost = 9,
    Full = 10,
    ApplicationClosed = 11,
}

/// <summary>Avis de celui qui partage : écran figé (onglet en arrière-plan, fenêtre réduite), puis repris.</summary>
public enum ShareNotice : byte
{
    ScreenPaused = 1,
    ScreenResumed = 2,
}

/// <summary>Qui demande à regarder : nom donné, compte Windows et poste (annoncés, non vérifiés), version de ZillaTerm.</summary>
public sealed record ShareJoin(string Name, string User, string Machine, string Version);

/// <summary>Ce qui est partagé, envoyé une fois l'accord donné.</summary>
/// <param name="EndsAt">Fin du partage (UTC).</param>
public sealed record ShareInfo(ShareKind Kind, string Label, string Sharer, string Machine, bool ControlAllowed, DateTime EndsAt);

/// <summary>Un message : son type et son contenu.</summary>
public readonly record struct ShareMessage(ShareMessageType Type, byte[] Payload)
{
    /// <summary>Message le plus long accepté (une image clé d'écran 4K tient largement).</summary>
    public const int MaxLength = 32 << 20;

    /// <summary>Message le plus long accepté de celui qui regarde (saisie, collage).</summary>
    public const int MaxGuestLength = 256 << 10;

    /// <summary>Longueur au plus d'un nom, compte, poste ou libellé.</summary>
    public const int MaxName = 128;

    public byte[] ToBytes()
    {
        var data = new byte[Payload.Length + 1];
        data[0] = (byte)Type;
        Payload.CopyTo(data, 1);
        return data;
    }

    public static ShareMessage? Parse(byte[] plain) =>
        plain.Length > 0 && Enum.IsDefined((ShareMessageType)plain[0]) ? new ShareMessage((ShareMessageType)plain[0], plain[1..]) : null;

    public static ShareMessage Info(ShareInfo info) => new(ShareMessageType.Info, JsonSerializer.SerializeToUtf8Bytes(new InfoDto(
        info.Kind == ShareKind.Screen ? "screen" : "terminal", info.Label, info.Sharer, info.Machine, info.ControlAllowed,
        new DateTimeOffset(DateTime.SpecifyKind(info.EndsAt, DateTimeKind.Utc)).ToUnixTimeSeconds())));

    public static ShareMessage TerminalReset(int columns, int rows, string screen)
    {
        var text = Encoding.UTF8.GetBytes(screen);
        var payload = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)columns);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)rows);
        text.CopyTo(payload, 4);
        return new ShareMessage(ShareMessageType.TerminalReset, payload);
    }

    public static ShareMessage TerminalData(string text) => new(ShareMessageType.TerminalData, Encoding.UTF8.GetBytes(text));

    public static ShareMessage ScreenFrame(byte[] frame) => new(ShareMessageType.ScreenFrame, frame);

    public static ShareMessage Control(bool granted) => new(ShareMessageType.Control, [granted ? (byte)1 : (byte)0]);

    public static ShareMessage Notice(ShareNotice notice) => new(ShareMessageType.Notice, [(byte)notice]);

    public static ShareMessage Bye(ShareEndReason reason) => new(ShareMessageType.Bye, [(byte)reason]);

    public static ShareMessage Ping() => new(ShareMessageType.Ping, []);

    public static ShareMessage Pong() => new(ShareMessageType.Pong, []);

    public static ShareMessage Join(ShareJoin join) => new(ShareMessageType.Join, JsonSerializer.SerializeToUtf8Bytes(
        new JoinDto(Clean(join.Name), Clean(join.User), Clean(join.Machine), Clean(join.Version))));

    public static ShareMessage ControlRequest(bool want) => new(ShareMessageType.ControlRequest, [want ? (byte)1 : (byte)0]);

    public static ShareMessage Leave() => new(ShareMessageType.Leave, []);

    /// <summary>Saisie de celui qui a la main : nature, touche, modificateurs, texte. Encodée par celui qui partage.</summary>
    public static ShareMessage Input(TerminalInput input)
    {
        var text = Encoding.UTF8.GetBytes(input.Text);
        var payload = new byte[3 + text.Length];
        payload[0] = (byte)input.Kind;
        payload[1] = (byte)input.Key;
        payload[2] = (byte)((input.Shift ? 1 : 0) | (input.Alt ? 2 : 0) | (input.Ctrl ? 4 : 0));
        text.CopyTo(payload, 3);
        return new ShareMessage(ShareMessageType.Input, payload);
    }

    public ShareInfo? ReadInfo()
    {
        try
        {
            if (JsonSerializer.Deserialize<InfoDto>(Payload) is not { } dto || dto.Kind is not ("terminal" or "screen"))
            {
                return null;
            }

            return new ShareInfo(dto.Kind == "screen" ? ShareKind.Screen : ShareKind.Terminal, Clean(dto.Label), Clean(dto.Sharer),
                Clean(dto.Machine), dto.ControlAllowed, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(dto.EndsAt, 0, 253402300799)).UtcDateTime);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ShareJoin? ReadJoin()
    {
        try
        {
            return JsonSerializer.Deserialize<JoinDto>(Payload) is { } dto
                ? new ShareJoin(Clean(dto.Name), Clean(dto.User), Clean(dto.Machine), Clean(dto.Version))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Taille et écran à reproduire ; null si le message est invalide.</summary>
    public (int Columns, int Rows, string Screen)? ReadReset()
    {
        if (Payload.Length < 4)
        {
            return null;
        }

        int columns = BinaryPrimitives.ReadUInt16BigEndian(Payload);
        int rows = BinaryPrimitives.ReadUInt16BigEndian(Payload.AsSpan(2));
        return columns is >= 2 and <= 1000 && rows is >= 2 and <= 500 ? (columns, rows, Encoding.UTF8.GetString(Payload, 4, Payload.Length - 4)) : null;
    }

    public string ReadText() => Encoding.UTF8.GetString(Payload);

    public bool ReadFlag() => Payload is [1];

    public byte ReadByte() => Payload.Length == 1 ? Payload[0] : (byte)0;

    /// <summary>Saisie reçue ; null si elle est invalide (nature ou touche inconnue).</summary>
    public TerminalInput? ReadInput()
    {
        if (Payload.Length < 3 || !Enum.IsDefined((TerminalInputKind)Payload[0]) || !Enum.IsDefined((TerminalKey)Payload[1]) || Payload[2] > 7)
        {
            return null;
        }

        var kind = (TerminalInputKind)Payload[0];
        var text = Encoding.UTF8.GetString(Payload, 3, Payload.Length - 3);
        return kind == TerminalInputKind.Paste
            ? TerminalInput.Pasted(text)
            : new TerminalInput(kind, text, (TerminalKey)Payload[1], (Payload[2] & 1) != 0, (Payload[2] & 2) != 0, (Payload[2] & 4) != 0);
    }

    /// <summary>
    /// Nom, compte, poste ou libellé reçu : une ligne, sans caractère de contrôle ni invisible (inversion du sens
    /// d'écriture…), espaces réunis, <see cref="MaxName"/> caractères au plus.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var clean = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            bool space = Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;
            if (!space)
            {
                if (clean.Length + rune.Utf16SequenceLength > MaxName)
                {
                    break;
                }

                clean.Append(rune.ToString());
            }
            else if (clean.Length > 0 && clean[^1] != ' ')
            {
                clean.Append(' ');
            }
        }

        return clean.ToString().Trim();
    }

    private sealed record InfoDto(string Kind, string? Label, string? Sharer, string? Machine, bool ControlAllowed, long EndsAt);

    private sealed record JoinDto(string? Name, string? User, string? Machine, string? Version);
}

/// <summary>Échec d'un partage, avec sa raison (pour un message clair).</summary>
public enum ShareFailure
{
    /// <summary>Ni le poste qui partage, ni le dossier relais n'ont répondu.</summary>
    Unreachable,

    /// <summary>L'invitation n'est pas (ou plus) celle de ce partage : secret refusé.</summary>
    Rejected,

    /// <summary>Le partage est terminé (durée écoulée, arrêté).</summary>
    Expired,

    /// <summary>Le dossier du partage est introuvable dans le dossier relais.</summary>
    NotFound,

    /// <summary>Celui qui partage a refusé, ou n'a pas répondu à temps.</summary>
    Refused,

    /// <summary>Trop de personnes regardent déjà.</summary>
    Full,

    /// <summary>Connexion directe impossible, et le dossier relais de l'invitation n'a pas été accepté.</summary>
    RelayRefused,
}

public sealed class ShareException(ShareFailure failure, string? detail = null, Exception? inner = null)
    : Exception($"{failure}{(detail is null ? "" : ": " + detail)}", inner)
{
    public ShareFailure Failure { get; } = failure;
}
