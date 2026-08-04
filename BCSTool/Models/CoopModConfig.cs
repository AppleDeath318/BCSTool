namespace BCSTool.Models;

/// <summary>
/// Editable values from CoopData\mod-config.json.
///
/// Difficulty keys are special: when an override is disabled, Coop's JSONC
/// file keeps that key commented out so the save/host setting remains in
/// control. The Value property is still retained so re-enabling the override
/// restores the user's last selected value.
/// </summary>
public sealed class CoopModConfig
{
    // --------------------------------------------------------
    // CAMPAIGN DIFFICULTY OVERRIDES
    // --------------------------------------------------------

    public bool PlayerReceivedDamageOverride { get; set; }
    public string PlayerReceivedDamage { get; set; } = "Realistic";

    public bool PlayerTroopsReceivedDamageOverride { get; set; }
    public string PlayerTroopsReceivedDamage { get; set; } = "VeryEasy";

    public bool CombatAIDifficultyOverride { get; set; }
    public string CombatAIDifficulty { get; set; } = "VeryEasy";

    public bool RecruitmentDifficultyOverride { get; set; }
    public string RecruitmentDifficulty { get; set; } = "VeryEasy";

    public bool PlayerMapMovementSpeedOverride { get; set; }
    public string PlayerMapMovementSpeed { get; set; } = "VeryEasy";

    public bool StealthAndDisguiseDifficultyOverride { get; set; }
    public string StealthAndDisguiseDifficulty { get; set; } = "VeryEasy";

    public bool PersuasionSuccessChanceOverride { get; set; }
    public string PersuasionSuccessChance { get; set; } = "VeryEasy";

    public bool ClanMemberDeathChanceOverride { get; set; }
    public string ClanMemberDeathChance { get; set; } = "VeryEasy";

    public bool BattleDeathOverride { get; set; }
    public string BattleDeath { get; set; } = "VeryEasy";

    public bool BirthAndDeathOverride { get; set; }
    public bool BirthAndDeath { get; set; } = true;

    public bool AutoAllocateClanMemberPerksOverride { get; set; }
    public bool AutoAllocateClanMemberPerks { get; set; }

    // --------------------------------------------------------
    // COOP MOD OPTIONS
    // --------------------------------------------------------

    public bool FastForwardEnabled { get; set; } = true;
    public bool AutoPauseEnabled { get; set; } = true;
    public bool ClientsCanUseCheats { get; set; }

    public bool GoldFoodInfluenceChangeInSettlements { get; set; } = true;

    public string GoldFoodInfluenceChangeInBattles { get; set; } =
        "OneDayMax";

    public bool GoldFoodInfluenceChangeForDisconnectedPlayers { get; set; }

    public int PlayerBattleAiJoinWindowHours { get; set; } = 24;

    public bool SpeedLimitWhilePlayersInBattle { get; set; } = true;

    public int WandererLimit { get; set; } = 32;

    public bool WandererLimitScalesWithPlayers { get; set; }

    public int PlayerKingdomClanTierRequired { get; set; } = 4;

    public bool SmithingStaminaRecoveryOutsideSettlements { get; set; } =
        true;

    public double SmithingStaminaRecoveryMultiplier { get; set; } = 0.1;

    public double MaximumLootersMultiplier { get; set; } = 1.0;
}
