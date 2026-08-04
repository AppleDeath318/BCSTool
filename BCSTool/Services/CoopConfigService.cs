using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Loads and saves Bannerlord Coop's server and mod configuration files.
///
/// The source files are JSON-with-comments (JSONC). Active values are parsed
/// with System.Text.Json while comments are skipped and trailing commas are
/// allowed. Known settings are updated line-by-line so surrounding comments
/// and property ordering are retained.
///
/// Before each save, the current configuration is copied to a sibling .bak
/// file and the edited JSONC is written directly to the original file.
/// </summary>
public sealed class CoopConfigService
{
    private static readonly JsonDocumentOptions JsonOptions =
        new()
        {
            CommentHandling =
                JsonCommentHandling.Skip,

            AllowTrailingCommas =
                true
        };

    public string CoopDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "CoopData");

    public string ModConfigPath =>
        Path.Combine(
            CoopDataDirectory,
            "mod-config.json");

    public string ServerConfigPath =>
        Path.Combine(
            CoopDataDirectory,
            "DedicatedServer",
            "server-config.json");


    // ========================================================
    // SERVER CONFIG
    // ========================================================

    public DedicatedServerConfig LoadServerConfig()
    {
        var text =
            ReadRequiredFile(
                ServerConfigPath);

        using var document =
            JsonDocument.Parse(
                text,
                JsonOptions);

        var root =
            document.RootElement;

        return
            new DedicatedServerConfig
            {
                SaveName =
                    GetString(
                        root,
                        "saveName",
                        "saveauto1"),

                AutosaveMinutes =
                    GetInt(
                        root,
                        "autosaveMinutes",
                        5),

                Password =
                    GetString(
                        root,
                        "password",
                        ""),

                LogFile =
                    GetBool(
                        root,
                        "logFile",
                        true),

                Steam =
                    GetBool(
                        root,
                        "steam",
                        true),

                TraceTick =
                    GetBool(
                        root,
                        "traceTick",
                        false),

                TracePublish =
                    GetBool(
                        root,
                        "tracePublish",
                        false),

                TraceBandits =
                    GetBool(
                        root,
                        "traceBandits",
                        false)
            };
    }


    public void SaveServerConfig(
        DedicatedServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SaveName))
        {
            throw new InvalidOperationException(
                "Save name cannot be empty.");
        }

        if (config.AutosaveMinutes < 0)
        {
            throw new InvalidOperationException(
                "Autosave minutes cannot be negative.");
        }

        if (config.Password.Length > 128)
        {
            throw new InvalidOperationException(
                "Server password cannot exceed 128 characters.");
        }

        var path =
            ServerConfigPath;

        var text =
            ReadRequiredFile(path);

        text =
            SetRequiredKey(
                text,
                "saveName",
                JsonSerializer.Serialize(
                    config.SaveName));

        text =
            SetRequiredKey(
                text,
                "autosaveMinutes",
                config.AutosaveMinutes.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "password",
                JsonSerializer.Serialize(
                    config.Password));

        text =
            SetRequiredKey(
                text,
                "logFile",
                ToJsonBool(
                    config.LogFile));

        text =
            SetRequiredKey(
                text,
                "steam",
                ToJsonBool(
                    config.Steam));

        text =
            SetOptionalRootBoolean(
                text,
                "traceTick",
                config.TraceTick);

        text =
            SetOptionalRootBoolean(
                text,
                "tracePublish",
                config.TracePublish);

        text =
            SetOptionalRootBoolean(
                text,
                "traceBandits",
                config.TraceBandits);

        SaveWithBackup(
            path,
            text);
    }


    // ========================================================
    // MOD CONFIG
    // ========================================================

    public CoopModConfig LoadModConfig()
    {
        var text =
            ReadRequiredFile(
                ModConfigPath);

        using var document =
            JsonDocument.Parse(
                text,
                JsonOptions);

        var root =
            document.RootElement;

        var difficulty =
            root.TryGetProperty(
                "difficulty",
                out var difficultyElement)
                ? difficultyElement
                : default;

        if (
            !root.TryGetProperty(
                "modOptions",
                out var modOptions))
        {
            throw new InvalidDataException(
                "mod-config.json does not contain a modOptions object.");
        }

        var config =
            new CoopModConfig();

        LoadOptionalDifficultyString(
            text,
            difficulty,
            "playerReceivedDamage",
            "Realistic",
            out var playerReceivedDamageOverride,
            out var playerReceivedDamage);

        config.PlayerReceivedDamageOverride =
            playerReceivedDamageOverride;

        config.PlayerReceivedDamage =
            playerReceivedDamage;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "playerTroopsReceivedDamage",
            "VeryEasy",
            out var playerTroopsReceivedDamageOverride,
            out var playerTroopsReceivedDamage);

        config.PlayerTroopsReceivedDamageOverride =
            playerTroopsReceivedDamageOverride;

        config.PlayerTroopsReceivedDamage =
            playerTroopsReceivedDamage;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "combatAIDifficulty",
            "VeryEasy",
            out var combatAIDifficultyOverride,
            out var combatAIDifficulty);

        config.CombatAIDifficultyOverride =
            combatAIDifficultyOverride;

        config.CombatAIDifficulty =
            combatAIDifficulty;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "recruitmentDifficulty",
            "VeryEasy",
            out var recruitmentDifficultyOverride,
            out var recruitmentDifficulty);

        config.RecruitmentDifficultyOverride =
            recruitmentDifficultyOverride;

        config.RecruitmentDifficulty =
            recruitmentDifficulty;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "playerMapMovementSpeed",
            "VeryEasy",
            out var playerMapMovementSpeedOverride,
            out var playerMapMovementSpeed);

        config.PlayerMapMovementSpeedOverride =
            playerMapMovementSpeedOverride;

        config.PlayerMapMovementSpeed =
            playerMapMovementSpeed;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "stealthAndDisguiseDifficulty",
            "VeryEasy",
            out var stealthAndDisguiseDifficultyOverride,
            out var stealthAndDisguiseDifficulty);

        config.StealthAndDisguiseDifficultyOverride =
            stealthAndDisguiseDifficultyOverride;

        config.StealthAndDisguiseDifficulty =
            stealthAndDisguiseDifficulty;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "persuasionSuccessChance",
            "VeryEasy",
            out var persuasionSuccessChanceOverride,
            out var persuasionSuccessChance);

        config.PersuasionSuccessChanceOverride =
            persuasionSuccessChanceOverride;

        config.PersuasionSuccessChance =
            persuasionSuccessChance;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "clanMemberDeathChance",
            "VeryEasy",
            out var clanMemberDeathChanceOverride,
            out var clanMemberDeathChance);

        config.ClanMemberDeathChanceOverride =
            clanMemberDeathChanceOverride;

        config.ClanMemberDeathChance =
            clanMemberDeathChance;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "battleDeath",
            "VeryEasy",
            out var battleDeathOverride,
            out var battleDeath);

        config.BattleDeathOverride =
            battleDeathOverride;

        config.BattleDeath =
            battleDeath;


        LoadOptionalDifficultyBool(
            text,
            difficulty,
            "birthAndDeath",
            true,
            out var birthAndDeathOverride,
            out var birthAndDeath);

        config.BirthAndDeathOverride =
            birthAndDeathOverride;

        config.BirthAndDeath =
            birthAndDeath;


        LoadOptionalDifficultyBool(
            text,
            difficulty,
            "autoAllocateClanMemberPerks",
            false,
            out var autoAllocateClanMemberPerksOverride,
            out var autoAllocateClanMemberPerks);

        config.AutoAllocateClanMemberPerksOverride =
            autoAllocateClanMemberPerksOverride;

        config.AutoAllocateClanMemberPerks =
            autoAllocateClanMemberPerks;


        config.FastForwardEnabled =
            GetBool(
                modOptions,
                "fastForwardEnabled",
                true);

        config.AutoPauseEnabled =
            GetBool(
                modOptions,
                "autoPauseEnabled",
                true);

        config.ClientsCanUseCheats =
            GetBool(
                modOptions,
                "clientsCanUseCheats",
                false);

        config.GoldFoodInfluenceChangeInSettlements =
            GetBool(
                modOptions,
                "goldFoodInfluenceChangeInSettlements",
                true);

        config.GoldFoodInfluenceChangeInBattles =
            GetString(
                modOptions,
                "goldFoodInfluenceChangeInBattles",
                "OneDayMax");

        config.GoldFoodInfluenceChangeForDisconnectedPlayers =
            GetBool(
                modOptions,
                "goldFoodInfluenceChangeForDisconnectedPlayers",
                false);

        config.PlayerBattleAiJoinWindowHours =
            GetInt(
                modOptions,
                "playerBattleAiJoinWindowHours",
                24);

        config.SpeedLimitWhilePlayersInBattle =
            GetBool(
                modOptions,
                "speedLimitWhilePlayersInBattle",
                true);

        config.WandererLimit =
            GetInt(
                modOptions,
                "wandererLimit",
                32);

        config.WandererLimitScalesWithPlayers =
            GetBool(
                modOptions,
                "wandererLimitScalesWithPlayers",
                false);

        config.PlayerKingdomClanTierRequired =
            GetInt(
                modOptions,
                "playerKingdomClanTierRequired",
                4);

        config.SmithingStaminaRecoveryOutsideSettlements =
            GetBool(
                modOptions,
                "smithingStaminaRecoveryOutsideSettlements",
                true);

        config.SmithingStaminaRecoveryMultiplier =
            GetDouble(
                modOptions,
                "smithingStaminaRecoveryMultiplier",
                0.1);

        config.MaximumLootersMultiplier =
            GetDouble(
                modOptions,
                "maximumLootersMultiplier",
                1.0);

        return config;
    }


    public void SaveModConfig(
        CoopModConfig config)
    {
        ValidateDifficultyValue(
            config.PlayerReceivedDamage,
            nameof(config.PlayerReceivedDamage));

        ValidateDifficultyValue(
            config.PlayerTroopsReceivedDamage,
            nameof(config.PlayerTroopsReceivedDamage));

        ValidateDifficultyValue(
            config.CombatAIDifficulty,
            nameof(config.CombatAIDifficulty));

        ValidateDifficultyValue(
            config.RecruitmentDifficulty,
            nameof(config.RecruitmentDifficulty));

        ValidateDifficultyValue(
            config.PlayerMapMovementSpeed,
            nameof(config.PlayerMapMovementSpeed));

        ValidateDifficultyValue(
            config.StealthAndDisguiseDifficulty,
            nameof(config.StealthAndDisguiseDifficulty));

        ValidateDifficultyValue(
            config.PersuasionSuccessChance,
            nameof(config.PersuasionSuccessChance));

        ValidateDifficultyValue(
            config.ClanMemberDeathChance,
            nameof(config.ClanMemberDeathChance));

        ValidateDifficultyValue(
            config.BattleDeath,
            nameof(config.BattleDeath));

        if (
            config.GoldFoodInfluenceChangeInBattles is not
                ("Disabled" or "OneDayMax" or "Enabled"))
        {
            throw new InvalidOperationException(
                "Battle gold/food/influence mode must be Disabled, OneDayMax, or Enabled.");
        }

        if (config.PlayerBattleAiJoinWindowHours < 0)
        {
            throw new InvalidOperationException(
                "AI battle join window hours cannot be negative.");
        }

        if (config.WandererLimit < 0)
        {
            throw new InvalidOperationException(
                "Wanderer limit cannot be negative.");
        }

        if (config.PlayerKingdomClanTierRequired < 0)
        {
            throw new InvalidOperationException(
                "Kingdom clan tier requirement cannot be negative.");
        }

        if (config.SmithingStaminaRecoveryMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Smithing stamina recovery multiplier cannot be negative.");
        }

        if (config.MaximumLootersMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Maximum looters multiplier cannot be negative.");
        }


        var path =
            ModConfigPath;

        var text =
            ReadRequiredFile(path);


        // Difficulty overrides.
        text =
            SetOptionalCommentedKey(
                text,
                "playerReceivedDamage",
                JsonSerializer.Serialize(
                    config.PlayerReceivedDamage),
                config.PlayerReceivedDamageOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "playerTroopsReceivedDamage",
                JsonSerializer.Serialize(
                    config.PlayerTroopsReceivedDamage),
                config.PlayerTroopsReceivedDamageOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "combatAIDifficulty",
                JsonSerializer.Serialize(
                    config.CombatAIDifficulty),
                config.CombatAIDifficultyOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "recruitmentDifficulty",
                JsonSerializer.Serialize(
                    config.RecruitmentDifficulty),
                config.RecruitmentDifficultyOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "playerMapMovementSpeed",
                JsonSerializer.Serialize(
                    config.PlayerMapMovementSpeed),
                config.PlayerMapMovementSpeedOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "stealthAndDisguiseDifficulty",
                JsonSerializer.Serialize(
                    config.StealthAndDisguiseDifficulty),
                config.StealthAndDisguiseDifficultyOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "persuasionSuccessChance",
                JsonSerializer.Serialize(
                    config.PersuasionSuccessChance),
                config.PersuasionSuccessChanceOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "clanMemberDeathChance",
                JsonSerializer.Serialize(
                    config.ClanMemberDeathChance),
                config.ClanMemberDeathChanceOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "battleDeath",
                JsonSerializer.Serialize(
                    config.BattleDeath),
                config.BattleDeathOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "birthAndDeath",
                ToJsonBool(
                    config.BirthAndDeath),
                config.BirthAndDeathOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "autoAllocateClanMemberPerks",
                ToJsonBool(
                    config.AutoAllocateClanMemberPerks),
                config.AutoAllocateClanMemberPerksOverride);


        // Mod options.
        text =
            SetRequiredKey(
                text,
                "fastForwardEnabled",
                ToJsonBool(
                    config.FastForwardEnabled));

        text =
            SetRequiredKey(
                text,
                "autoPauseEnabled",
                ToJsonBool(
                    config.AutoPauseEnabled));

        text =
            SetRequiredKey(
                text,
                "clientsCanUseCheats",
                ToJsonBool(
                    config.ClientsCanUseCheats));

        text =
            SetRequiredKey(
                text,
                "goldFoodInfluenceChangeInSettlements",
                ToJsonBool(
                    config.GoldFoodInfluenceChangeInSettlements));

        text =
            SetRequiredKey(
                text,
                "goldFoodInfluenceChangeInBattles",
                JsonSerializer.Serialize(
                    config.GoldFoodInfluenceChangeInBattles));

        text =
            SetRequiredKey(
                text,
                "goldFoodInfluenceChangeForDisconnectedPlayers",
                ToJsonBool(
                    config.GoldFoodInfluenceChangeForDisconnectedPlayers));

        text =
            SetRequiredKey(
                text,
                "playerBattleAiJoinWindowHours",
                config.PlayerBattleAiJoinWindowHours.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "speedLimitWhilePlayersInBattle",
                ToJsonBool(
                    config.SpeedLimitWhilePlayersInBattle));

        text =
            SetRequiredKey(
                text,
                "wandererLimit",
                config.WandererLimit.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "wandererLimitScalesWithPlayers",
                ToJsonBool(
                    config.WandererLimitScalesWithPlayers));

        text =
            SetRequiredKey(
                text,
                "playerKingdomClanTierRequired",
                config.PlayerKingdomClanTierRequired.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "smithingStaminaRecoveryOutsideSettlements",
                ToJsonBool(
                    config.SmithingStaminaRecoveryOutsideSettlements));

        text =
            SetRequiredKey(
                text,
                "smithingStaminaRecoveryMultiplier",
                config.SmithingStaminaRecoveryMultiplier.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "maximumLootersMultiplier",
                config.MaximumLootersMultiplier.ToString(
                    CultureInfo.InvariantCulture));


        SaveWithBackup(
            path,
            text);
    }


    // ========================================================
    // JSONC HELPERS — v1.8.1 SIMPLE WRITER (NO REGEX)
    // ========================================================

    private static string ReadRequiredFile(
        string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file was not found:\n{path}",
                path);
        }

        return
            File.ReadAllText(
                path,
                Encoding.UTF8);
    }


    private static string SetRequiredKey(
        string text,
        string key,
        string jsonValue)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var index =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: false);

        if (index < 0)
        {
            throw new InvalidDataException(
                $"Could not find setting '{key}' in the configuration file.");
        }

        lines[index] =
            BuildSettingLine(
                lines[index],
                key,
                jsonValue,
                commented: false);

        return
            string.Join(
                newline,
                lines);
    }


    /// <summary>
    /// Enables/disables one optional difficulty setting by toggling only the
    /// JSONC // marker on that setting line. Indentation and any explanatory
    /// inline comment after the comma are preserved.
    /// </summary>
    private static string SetOptionalCommentedKey(
        string text,
        string key,
        string jsonValue,
        bool enabled)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var index =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: true);

        if (index < 0)
        {
            throw new InvalidDataException(
                $"Could not find optional difficulty setting '{key}' in mod-config.json.");
        }

        lines[index] =
            BuildSettingLine(
                lines[index],
                key,
                jsonValue,
                commented: !enabled);

        return
            string.Join(
                newline,
                lines);
    }


    /// <summary>
    /// Diagnostic booleans may be absent. Existing active keys are updated.
    /// Missing false keys remain absent. Missing true keys are inserted before
    /// the root closing brace.
    /// </summary>
    private static string SetOptionalRootBoolean(
        string text,
        string key,
        bool value)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var index =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: false);

        if (index >= 0)
        {
            lines[index] =
                BuildSettingLine(
                    lines[index],
                    key,
                    ToJsonBool(value),
                    commented: false);

            return
                string.Join(
                    newline,
                    lines);
        }

        if (!value)
        {
            return
                string.Join(
                    newline,
                    lines);
        }

        var closingIndex =
            FindRootClosingBraceIndex(
                lines);

        if (closingIndex < 0)
        {
            throw new InvalidDataException(
                "server-config.json does not contain a root closing brace.");
        }

        var list =
            new List<string>(
                lines);

        list.Insert(
            closingIndex,
            $"  \"{key}\": true,");

        return
            string.Join(
                newline,
                list);
    }


    private static string BuildSettingLine(
        string originalLine,
        string key,
        string jsonValue,
        bool commented)
    {
        var indentation =
            GetLeadingWhitespace(
                originalLine);

        var trailingComment =
            ExtractTrailingCommentFromOptionalLine(
                originalLine);

        var result =
            indentation +
            (commented ? "// " : "") +
            "\"" + key + "\": " + jsonValue + ",";

        if (trailingComment.Length > 0)
        {
            result +=
                " " +
                trailingComment;
        }

        return result;
    }


    private static string ExtractTrailingCommentFromOptionalLine(
        string line)
    {
        var commaIndex =
            line.IndexOf(',');

        if (commaIndex < 0)
            return "";

        var commentIndex =
            line.IndexOf(
                "//",
                commaIndex + 1,
                StringComparison.Ordinal);

        if (commentIndex < 0)
            return "";

        return
            line[commentIndex..]
                .Trim();
    }


    private static string GetLeadingWhitespace(
        string line)
    {
        var count = 0;

        while (
            count < line.Length &&
            char.IsWhiteSpace(
                line[count]))
        {
            count++;
        }

        return
            line[..count];
    }


    private static int FindSettingLineIndex(
        string[] lines,
        string key,
        bool includeCommented)
    {
        for (
            var i = 0;
            i < lines.Length;
            i++)
        {
            if (
                IsSettingLineForKey(
                    lines[i],
                    key,
                    includeCommented))
            {
                return i;
            }
        }

        return -1;
    }


    private static bool IsSettingLineForKey(
        string line,
        string key,
        bool includeCommented)
    {
        var trimmed =
            line.TrimStart();

        if (
            trimmed.StartsWith(
                "//",
                StringComparison.Ordinal))
        {
            if (!includeCommented)
                return false;

            trimmed =
                trimmed[2..]
                    .TrimStart();
        }

        var quotedKey =
            "\"" +
            key +
            "\"";

        if (
            !trimmed.StartsWith(
                quotedKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        var remainder =
            trimmed[quotedKey.Length..]
                .TrimStart();

        return
            remainder.StartsWith(
                ":",
                StringComparison.Ordinal);
    }


    private static int FindRootClosingBraceIndex(
        string[] lines)
    {
        for (
            var i = lines.Length - 1;
            i >= 0;
            i--)
        {
            if (
                lines[i]
                    .Trim()
                    .Equals(
                        "}",
                        StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }


    private static void SaveWithBackup(
        string path,
        string text)
    {
        File.Copy(
            path,
            path + ".bak",
            overwrite: true);

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
    }


    private static void LoadOptionalDifficultyString(
        string rawText,
        JsonElement difficulty,
        string key,
        string fallback,
        out bool enabled,
        out string value)
    {
        if (
            difficulty.ValueKind == JsonValueKind.Object &&
            difficulty.TryGetProperty(
                key,
                out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            enabled = true;

            value =
                element.GetString() ??
                fallback;

            return;
        }

        enabled = false;

        value =
            ReadCommentedStringValue(
                rawText,
                key) ??
            fallback;
    }


    private static void LoadOptionalDifficultyBool(
        string rawText,
        JsonElement difficulty,
        string key,
        bool fallback,
        out bool enabled,
        out bool value)
    {
        if (
            difficulty.ValueKind == JsonValueKind.Object &&
            difficulty.TryGetProperty(
                key,
                out var element) &&
            (
                element.ValueKind == JsonValueKind.True ||
                element.ValueKind == JsonValueKind.False
            ))
        {
            enabled = true;
            value = element.GetBoolean();
            return;
        }

        enabled = false;

        value =
            ReadCommentedBoolValue(
                rawText,
                key) ??
            fallback;
    }


    private static string? ReadCommentedStringValue(
        string text,
        string key)
    {
        var rawValue =
            ReadCommentedRawValue(
                text,
                key);

        if (rawValue is null)
            return null;

        try
        {
            return
                JsonSerializer.Deserialize<string>(
                    rawValue);
        }
        catch
        {
            return null;
        }
    }


    private static bool? ReadCommentedBoolValue(
        string text,
        string key)
    {
        var rawValue =
            ReadCommentedRawValue(
                text,
                key);

        if (
            rawValue is null ||
            !bool.TryParse(
                rawValue,
                out var value))
        {
            return null;
        }

        return value;
    }


    private static string? ReadCommentedRawValue(
        string text,
        string key)
    {
        var lines =
            SplitLines(
                text,
                out _);

        foreach (var line in lines)
        {
            var trimmed =
                line.TrimStart();

            if (
                !trimmed.StartsWith(
                    "//",
                    StringComparison.Ordinal))
            {
                continue;
            }

            trimmed =
                trimmed[2..]
                    .TrimStart();

            var quotedKey =
                "\"" +
                key +
                "\"";

            if (
                !trimmed.StartsWith(
                    quotedKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var remainder =
                trimmed[quotedKey.Length..]
                    .TrimStart();

            if (
                !remainder.StartsWith(
                    ":",
                    StringComparison.Ordinal))
            {
                continue;
            }

            remainder =
                remainder[1..]
                    .TrimStart();

            var commaIndex =
                FindValueTerminatingComma(
                    remainder);

            var rawValue =
                commaIndex >= 0
                    ? remainder[..commaIndex]
                    : remainder;

            return
                rawValue.Trim();
        }

        return null;
    }


    private static int FindValueTerminatingComma(
        string text)
    {
        var inString = false;
        var escaped = false;

        for (
            var i = 0;
            i < text.Length;
            i++)
        {
            var ch =
                text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (
                inString &&
                ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString =
                    !inString;

                continue;
            }

            if (
                !inString &&
                ch == ',')
            {
                return i;
            }
        }

        return -1;
    }


    private static string[] SplitLines(
        string text,
        out string newline)
    {
        newline =
            text.Contains(
                "\r\n",
                StringComparison.Ordinal)
                ? "\r\n"
                : "\n";

        return
            text.Replace(
                    "\r\n",
                    "\n",
                    StringComparison.Ordinal)
                .Split('\n');
    }


    private static string GetString(
        JsonElement element,
        string key,
        string fallback)
    {
        return
            element.TryGetProperty(
                key,
                out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
    }


    private static int GetInt(
        JsonElement element,
        string key,
        int fallback)
    {
        return
            element.TryGetProperty(
                key,
                out var value) &&
            value.TryGetInt32(
                out var result)
                ? result
                : fallback;
    }


    private static double GetDouble(
        JsonElement element,
        string key,
        double fallback)
    {
        return
            element.TryGetProperty(
                key,
                out var value) &&
            value.TryGetDouble(
                out var result)
                ? result
                : fallback;
    }


    private static bool GetBool(
        JsonElement element,
        string key,
        bool fallback)
    {
        if (
            !element.TryGetProperty(
                key,
                out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }


    private static string ToJsonBool(bool value) =>
        value
            ? "true"
            : "false";


    private static void ValidateDifficultyValue(
        string value,
        string propertyName)
    {
        if (
            value is not
                ("VeryEasy" or "Easy" or "Realistic"))
        {
            throw new InvalidOperationException(
                $"{propertyName} must be VeryEasy, Easy, or Realistic.");
        }
    }
}
