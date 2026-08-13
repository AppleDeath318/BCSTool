namespace BCSTool.Models;

public enum PlayerAccessMode
{
    None,
    Banlist,
    Whitelist
}

public sealed class PlayerAccessEntry
{
    public string SteamId { get; set; } = "";
    public string LastKnownCharacterName { get; set; } = "";
    public string HeroId { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class PlayerIdentityEntry
{
    public string SteamId { get; set; } = "";
    public string HeroId { get; set; } = "";
    public string LastKnownCharacterName { get; set; } = "";
}

/// <summary>
/// One row in the main window's player-information panel. Only player rows
/// carry action metadata; their indented detail rows are display-only.
/// </summary>
public sealed class PlayerInformationLine
{
    private PlayerInformationLine(
        string text,
        bool isPlayerLine,
        int playerId,
        string characterName,
        string steamId,
        bool canCopySteamId)
    {
        Text = text;
        IsPlayerLine = isPlayerLine;
        PlayerId = playerId;
        CharacterName = characterName;
        SteamId = steamId;
        CanCopySteamId = canCopySteamId;
    }

    public string Text { get; }
    public bool IsPlayerLine { get; }
    public int PlayerId { get; }
    public string CharacterName { get; }
    public string SteamId { get; }
    public bool CanCopySteamId { get; }

    public static PlayerInformationLine Player(
        string text,
        int playerId,
        string characterName,
        string steamId = "") =>
        new(text, true, playerId, characterName, steamId, false);

    public static PlayerInformationLine Detail(
        string text,
        string steamId = "",
        bool canCopySteamId = false) =>
        new(text, false, -1, "", steamId, canCopySteamId);
}
