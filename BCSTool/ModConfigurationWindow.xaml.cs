using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BCSTool.Models;
using BCSTool.Services;

namespace BCSTool;

/// <summary>
/// Bannerlord Coop mod configuration editor.
/// </summary>
public partial class ModConfigurationWindow : Window
{
    private readonly CoopConfigService _configService;

    private CoopModConfig _config =
        new();

    private bool _synchronizingOverrideAll;


    public ModConfigurationWindow(
        CoopConfigService configService)
    {
        InitializeComponent();

        _configService =
            configService;

        ConfigPathText.Text =
            _configService.ModConfigPath;

        LoadConfiguration();
    }


    private void LoadConfiguration()
    {
        try
        {
            _config =
                _configService.LoadModConfig();

            DataContext =
                _config;

            SyncOverrideAllFromChildren();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Mod Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    /// <summary>
    /// Saves silently on success. Validation and IO failures remain visible so
    /// the user is never left assuming a failed write succeeded.
    /// </summary>
    private bool SaveConfiguration()
    {
        if (HasValidationErrors(this))
        {
            MessageBox.Show(
                this,
                "One or more numeric values are not valid. Correct the highlighted fields before saving.",
                "Mod Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        try
        {
            _configService.SaveModConfig(
                _config);

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Mod Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }
    }


    private void Save_Click(
        object sender,
        RoutedEventArgs e)
    {
        SaveConfiguration();
    }


    private void SaveAndClose_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SaveConfiguration())
        {
            Close();
        }
    }


    private void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }


    private void OverrideAll_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (_synchronizingOverrideAll)
            return;

        SetAllOverrides(
            true);
    }


    private void OverrideAll_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (_synchronizingOverrideAll)
            return;

        SetAllOverrides(
            false);
    }


    private void IndividualOverride_Click(
        object sender,
        RoutedEventArgs e)
    {
        SyncOverrideAllFromChildren();
    }


    private void SetAllOverrides(
        bool enabled)
    {
        _synchronizingOverrideAll = true;

        try
        {
            // Update the model explicitly so the master checkbox never has
            // to replace or detach the individual WPF binding expressions.
            _config.PlayerReceivedDamageOverride = enabled;
            _config.PlayerTroopsReceivedDamageOverride = enabled;
            _config.CombatAIDifficultyOverride = enabled;
            _config.RecruitmentDifficultyOverride = enabled;
            _config.PlayerMapMovementSpeedOverride = enabled;
            _config.StealthAndDisguiseDifficultyOverride = enabled;
            _config.PersuasionSuccessChanceOverride = enabled;
            _config.ClanMemberDeathChanceOverride = enabled;
            _config.BattleDeathOverride = enabled;
            _config.BirthAndDeathOverride = enabled;
            _config.AutoAllocateClanMemberPerksOverride = enabled;

            foreach (var checkBox in GetOverrideBoxes())
            {
                checkBox
                    .GetBindingExpression(
                        ToggleButton.IsCheckedProperty)
                    ?.UpdateTarget();
            }

            OverrideAllBox.IsChecked =
                enabled;
        }
        finally
        {
            _synchronizingOverrideAll = false;
        }
    }


    private void SyncOverrideAllFromChildren()
    {
        _synchronizingOverrideAll = true;

        try
        {
            OverrideAllBox.IsChecked =
                GetOverrideBoxes()
                    .All(
                        checkBox =>
                            checkBox.IsChecked == true);
        }
        finally
        {
            _synchronizingOverrideAll = false;
        }
    }


    private IEnumerable<CheckBox> GetOverrideBoxes()
    {
        yield return PlayerReceivedDamageOverrideBox;
        yield return PlayerTroopsReceivedDamageOverrideBox;
        yield return CombatAIOverrideBox;
        yield return RecruitmentOverrideBox;
        yield return MovementOverrideBox;
        yield return StealthOverrideBox;
        yield return PersuasionOverrideBox;
        yield return ClanDeathOverrideBox;
        yield return BattleDeathOverrideBox;
        yield return BirthDeathOverrideBox;
        yield return AutoPerksOverrideBox;
    }


    private static bool HasValidationErrors(
        DependencyObject root)
    {
        if (Validation.GetHasError(root))
            return true;

        for (
            var i = 0;
            i < VisualTreeHelper.GetChildrenCount(root);
            i++)
        {
            if (
                HasValidationErrors(
                    VisualTreeHelper.GetChild(
                        root,
                        i)))
            {
                return true;
            }
        }

        return false;
    }
}
