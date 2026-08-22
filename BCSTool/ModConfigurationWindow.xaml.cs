using System;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    private string _savedConfigurationSnapshot = "";
    private bool _configurationLoaded;


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

            _savedConfigurationSnapshot =
                CreateConfigurationSnapshot();

            _configurationLoaded =
                true;
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

            _savedConfigurationSnapshot =
                CreateConfigurationSnapshot();

            _configurationLoaded =
                true;

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


    private string CreateConfigurationSnapshot()
    {
        return
            JsonSerializer.Serialize(
                _config);
    }


    private bool HasUnsavedChanges()
    {
        if (!_configurationLoaded)
            return false;

        return
            !string.Equals(
                _savedConfigurationSnapshot,
                CreateConfigurationSnapshot(),
                StringComparison.Ordinal);
    }


    /// <summary>
    /// Runs for both the Close button and the title-bar X.
    /// </summary>
    private void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!HasUnsavedChanges())
            return;

        var result =
            MessageBox.Show(
                this,
                "You have unsaved Mod Configuration changes.\n\n" +
                "Close without saving them?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            e.Cancel =
                true;
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
