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

    /// <summary>
    /// BCS Tool requires Bannerlord Coop's server log for console display,
    /// player/command events, readiness, save completion, and crash detection.
    /// If server-config.json exists with logFile disabled, turn it back on.
    /// Returns true only when the file had to be changed.
    /// </summary>
    public bool EnsureServerLoggingEnabled()
    {
        if (!File.Exists(ServerConfigPath))
            return false;

        var config =
            LoadServerConfig();

        if (config.LogFile)
            return false;

        config.LogFile =
            true;

        SaveServerConfig(
            config);

        return true;
    }



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
        // The application cannot operate correctly without the live
        // coop-server log, so logging is a required invariant of every config
        // write performed by BCS Tool.
        config.LogFile =
            true;

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

        var network =
            root.TryGetProperty(
                "network",
                out var networkElement)
                ? networkElement
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

        config.PlayerReceivedDamage =
            LoadDifficultyString(
                text,
                difficulty,
                "playerReceivedDamage",
                "VeryEasy");

        config.PlayerTroopsReceivedDamage =
            LoadDifficultyString(
                text,
                difficulty,
                "playerTroopsReceivedDamage",
                "VeryEasy");

        config.CombatAIDifficulty =
            LoadDifficultyString(
                text,
                difficulty,
                "combatAIDifficulty",
                "VeryEasy");

        config.RecruitmentDifficulty =
            LoadDifficultyString(
                text,
                difficulty,
                "recruitmentDifficulty",
                "VeryEasy");

        config.PlayerMapMovementSpeed =
            LoadDifficultyString(
                text,
                difficulty,
                "playerMapMovementSpeed",
                "VeryEasy");

        config.StealthAndDisguiseDifficulty =
            LoadDifficultyString(
                text,
                difficulty,
                "stealthAndDisguiseDifficulty",
                "VeryEasy");

        config.PersuasionSuccessChance =
            LoadDifficultyString(
                text,
                difficulty,
                "persuasionSuccessChance",
                "VeryEasy");

        config.ClanMemberDeathChance =
            LoadDifficultyString(
                text,
                difficulty,
                "clanMemberDeathChance",
                "VeryEasy");

        config.BattleDeath =
            LoadDifficultyString(
                text,
                difficulty,
                "battleDeath",
                "VeryEasy");

        config.BirthAndDeath =
            LoadDifficultyBool(
                text,
                difficulty,
                "birthAndDeath",
                false);

        config.AutoAllocateClanMemberPerks =
            LoadDifficultyBool(
                text,
                difficulty,
                "autoAllocateClanMemberPerks",
                false);


        config.MovementOutgoingMiBPerSecond =
            GetDouble(
                network,
                "movementOutgoingMiBPerSecond",
                1.0);

        config.MovementIncomingMiBPerSecond =
            GetDouble(
                network,
                "movementIncomingMiBPerSecond",
                1.0);


        config.BattleSize =
            GetInt(
                modOptions,
                "battleSize",
                1000);


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

        config.LooterPartySizeMultiplier =
            GetDouble(
                modOptions,
                "looterPartySizeMultiplier",
                1.0);

        config.LordDefectionRetries =
            GetString(
                modOptions,
                "lordDefectionRetries",
                "Vanilla");

        config.EnableHeroExecutions =
            GetBool(
                modOptions,
                "enableHeroExecutions",
                true);

        config.EnablePlayerClanMemberExecutions =
            GetBool(
                modOptions,
                "enablePlayerClanMemberExecutions",
                false);

        config.ShowPlayerNameplates =
            GetBool(
                modOptions,
                "showPlayerNameplates",
                true);

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

        ValidateMovementBandwidth(
            config.MovementOutgoingMiBPerSecond,
            "Outgoing movement bandwidth");

        ValidateMovementBandwidth(
            config.MovementIncomingMiBPerSecond,
            "Incoming movement bandwidth");

        if (config.BattleSize is < 200 or > 1000)
        {
            throw new InvalidOperationException(
                "Battle size must be between 200 and 1000.");
        }

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

        if (
            !double.IsFinite(
                config.SmithingStaminaRecoveryMultiplier) ||
            config.SmithingStaminaRecoveryMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Smithing stamina recovery multiplier must be a finite, non-negative number.");
        }

        if (
            !double.IsFinite(
                config.MaximumLootersMultiplier) ||
            config.MaximumLootersMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Looter / bandit party count multiplier must be a finite, non-negative number.");
        }

        if (
            !double.IsFinite(
                config.LooterPartySizeMultiplier) ||
            config.LooterPartySizeMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Looter party size multiplier must be a finite, non-negative number.");
        }

        if (
            config.LordDefectionRetries is not
                ("Vanilla" or "NeverExpire" or "AlwaysRetry"))
        {
            throw new InvalidOperationException(
                "Lord defection retries must be Vanilla, NeverExpire, or AlwaysRetry.");
        }


        var path =
            ModConfigPath;

        var text =
            ReadRequiredFile(path);


        // Bannerlord Coop v0.1.3 activates and applies every difficulty value
        // at startup. Saving through BCS Tool also upgrades formerly commented
        // difficulty entries in older mod-config files.
        text =
            SetObjectKey(
                text,
                "difficulty",
                "playerReceivedDamage",
                JsonSerializer.Serialize(
                    config.PlayerReceivedDamage));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "playerTroopsReceivedDamage",
                JsonSerializer.Serialize(
                    config.PlayerTroopsReceivedDamage));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "combatAIDifficulty",
                JsonSerializer.Serialize(
                    config.CombatAIDifficulty));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "recruitmentDifficulty",
                JsonSerializer.Serialize(
                    config.RecruitmentDifficulty));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "playerMapMovementSpeed",
                JsonSerializer.Serialize(
                    config.PlayerMapMovementSpeed));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "stealthAndDisguiseDifficulty",
                JsonSerializer.Serialize(
                    config.StealthAndDisguiseDifficulty));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "persuasionSuccessChance",
                JsonSerializer.Serialize(
                    config.PersuasionSuccessChance));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "clanMemberDeathChance",
                JsonSerializer.Serialize(
                    config.ClanMemberDeathChance));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "battleDeath",
                JsonSerializer.Serialize(
                    config.BattleDeath));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "birthAndDeath",
                ToJsonBool(
                    config.BirthAndDeath));

        text =
            SetObjectKey(
                text,
                "difficulty",
                "autoAllocateClanMemberPerks",
                ToJsonBool(
                    config.AutoAllocateClanMemberPerks));


        // Local movement-network limits.
        text =
            SetObjectKey(
                text,
                "network",
                "movementOutgoingMiBPerSecond",
                config.MovementOutgoingMiBPerSecond.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetObjectKey(
                text,
                "network",
                "movementIncomingMiBPerSecond",
                config.MovementIncomingMiBPerSecond.ToString(
                    CultureInfo.InvariantCulture));


        // Mod options.
        text =
            SetObjectKey(
                text,
                "modOptions",
                "battleSize",
                config.BattleSize.ToString(
                    CultureInfo.InvariantCulture));

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

        text =
            SetObjectKey(
                text,
                "modOptions",
                "looterPartySizeMultiplier",
                config.LooterPartySizeMultiplier.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "lordDefectionRetries",
                JsonSerializer.Serialize(
                    config.LordDefectionRetries));

        text =
            SetRequiredKey(
                text,
                "enableHeroExecutions",
                ToJsonBool(
                    config.EnableHeroExecutions));

        text =
            SetRequiredKey(
                text,
                "enablePlayerClanMemberExecutions",
                ToJsonBool(
                    config.EnablePlayerClanMemberExecutions));

        text =
            SetObjectKey(
                text,
                "modOptions",
                "showPlayerNameplates",
                ToJsonBool(
                    config.ShowPlayerNameplates));


        SaveWithBackup(
            path,
            text);
    }


    // ========================================================
    // JSONC HELPERS — SIMPLE WRITER (NO REGEX)
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
    /// Updates a JSONC property inside a named root object. If the property is
    /// absent, it is inserted at the beginning of that object. If the complete
    /// object is absent (for example, an older file without v0.1.3's network
    /// block), the object and property are inserted after the root opening
    /// brace. Existing comments and unrelated settings remain untouched.
    /// </summary>
    private static string SetObjectKey(
        string text,
        string objectKey,
        string key,
        string jsonValue)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var settingIndex =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: true);

        if (settingIndex >= 0)
        {
            lines[settingIndex] =
                BuildSettingLine(
                    lines[settingIndex],
                    key,
                    jsonValue,
                    commented: false);

            return
                string.Join(
                    newline,
                    lines);
        }

        var objectIndex =
            FindSettingLineIndex(
                lines,
                objectKey,
                includeCommented: false);

        var list =
            new List<string>(
                lines);

        if (objectIndex >= 0)
        {
            var openingBraceIndex =
                lines[objectIndex]
                    .IndexOf('{');

            if (
                openingBraceIndex < 0 ||
                HasContentAfterOpeningBrace(
                    lines[objectIndex],
                    openingBraceIndex))
            {
                throw new InvalidDataException(
                    $"The '{objectKey}' object must use the normal multi-line mod-config.json layout.");
            }

            var propertyIndentation =
                GetLeadingWhitespace(
                    lines[objectIndex]) +
                "  ";

            list.Insert(
                objectIndex + 1,
                $"{propertyIndentation}\"{key}\": {jsonValue},");

            return
                string.Join(
                    newline,
                    list);
        }

        var rootOpeningIndex =
            FindRootOpeningBraceIndex(
                lines);

        if (rootOpeningIndex < 0)
        {
            throw new InvalidDataException(
                "mod-config.json does not contain a root opening brace.");
        }

        var objectIndentation =
            GetLeadingWhitespace(
                lines[rootOpeningIndex]) +
            "  ";

        list.InsertRange(
            rootOpeningIndex + 1,
            new[]
            {
                $"{objectIndentation}\"{objectKey}\": {{",
                $"{objectIndentation}  \"{key}\": {jsonValue},",
                $"{objectIndentation}}},"
            });

        return
            string.Join(
                newline,
                list);
    }


    private static bool HasContentAfterOpeningBrace(
        string line,
        int openingBraceIndex)
    {
        var remainder =
            line[(openingBraceIndex + 1)..]
                .Trim();

        return
            remainder.Length > 0 &&
            !remainder.StartsWith(
                "//",
                StringComparison.Ordinal);
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


    private static int FindRootOpeningBraceIndex(
        string[] lines)
    {
        for (
            var i = 0;
            i < lines.Length;
            i++)
        {
            if (
                lines[i]
                    .TrimStart()
                    .StartsWith(
                        "{",
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


    /// <summary>
    /// Loads active v0.1.3 difficulty values while still recognizing values
    /// that were commented out by older Coop configurations.
    /// </summary>
    private static string LoadDifficultyString(
        string rawText,
        JsonElement difficulty,
        string key,
        string fallback)
    {
        if (
            difficulty.ValueKind == JsonValueKind.Object &&
            difficulty.TryGetProperty(
                key,
                out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return
                element.GetString() ??
                fallback;
        }

        return
            ReadCommentedStringValue(
                rawText,
                key) ??
            fallback;
    }


    private static bool LoadDifficultyBool(
        string rawText,
        JsonElement difficulty,
        string key,
        bool fallback)
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
            return element.GetBoolean();
        }

        return
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
            element.ValueKind == JsonValueKind.Object &&
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
            element.ValueKind == JsonValueKind.Object &&
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
            element.ValueKind == JsonValueKind.Object &&
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
            element.ValueKind != JsonValueKind.Object ||
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


    private static void ValidateMovementBandwidth(
        double value,
        string displayName)
    {
        if (
            !double.IsFinite(value) ||
            value <= 0 ||
            value > 1024)
        {
            throw new InvalidOperationException(
                $"{displayName} must be greater than 0 and no more than 1024 MiB/s.");
        }
    }
}
