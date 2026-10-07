namespace BCSTool.Models;

/// <summary>
/// Editable values from CoopData\mod-config.json.
///
/// Bannerlord Coop v0.1.6 activates every campaign-difficulty key and applies
/// those values on the hosting side during startup.
/// </summary>
public sealed class CoopModConfig
{
    // --------------------------------------------------------
    // CAMPAIGN DIFFICULTY
    // --------------------------------------------------------

    public string PlayerReceivedDamage { get; set; } = "VeryEasy";

    public string PlayerTroopsReceivedDamage { get; set; } = "VeryEasy";

    public string CombatAIDifficulty { get; set; } = "VeryEasy";

    public string RecruitmentDifficulty { get; set; } = "VeryEasy";

    public string PlayerMapMovementSpeed { get; set; } = "VeryEasy";

    public string StealthAndDisguiseDifficulty { get; set; } = "VeryEasy";

    public string PersuasionSuccessChance { get; set; } = "VeryEasy";

    public string ClanMemberDeathChance { get; set; } = "VeryEasy";

    public string BattleDeath { get; set; } = "VeryEasy";

    public bool BirthAndDeath { get; set; } = true;

    public bool AutoAllocateClanMemberPerks { get; set; }

    // --------------------------------------------------------
    // PROXIMITY VOICE
    // --------------------------------------------------------

    public bool VoiceEnabled { get; set; } = true;

    public double MapFullVolumeDistance { get; set; } = 5;

    public double MapMaximumDistance { get; set; } = 20;

    public double SceneFullVolumeDistance { get; set; } = 10;

    public double SceneMaximumDistance { get; set; } = 50;

    // --------------------------------------------------------
    // COOP MOD OPTIONS
    // --------------------------------------------------------

    public int BattleSize { get; set; } = 1000;

    public bool FastForwardEnabled { get; set; } = true;
    public bool AutoPauseEnabled { get; set; } = true;
    public bool ClientsCanUseCheats { get; set; }

    public bool GoldFoodInfluenceChangeInSettlements { get; set; }

    public string GoldFoodInfluenceChangeInBattles { get; set; } =
        "Disabled";

    public bool GoldFoodInfluenceChangeForDisconnectedPlayers { get; set; }

    public bool BlockAiWarDeclarationsOnOfflinePlayers { get; set; }

    public int PlayerBattleAiJoinWindowHours { get; set; } = 6;

    public bool SpeedLimitWhilePlayersInBattle { get; set; } = true;

    public bool EnsureUnaffiliatedWanderers { get; set; } = true;

    public int WandererLimit { get; set; } = 32;

    public bool WandererLimitScalesWithPlayers { get; set; }

    public int PlayerKingdomClanTierRequired { get; set; } = 4;

    public bool CoopClansEnabled { get; set; } = true;

    public bool SmithingStaminaRecoveryOutsideSettlements { get; set; } =
        true;

    public double SmithingStaminaRecoveryMultiplier { get; set; } = 0.1;

    public double MaximumLootersMultiplier { get; set; } = 1.0;

    public double LooterPartySizeMultiplier { get; set; } = 1.0;

    public string LordDefectionRetries { get; set; } = "Vanilla";

    public bool EnableHeroExecutions { get; set; }

    public bool EnablePlayerClanMemberExecutions { get; set; }

    public bool EnablePlayerExecutions { get; set; }

    public bool ShowPlayerNameplates { get; set; } = true;

    public bool PlayerWoundedBattleEntry { get; set; } = true;
}
