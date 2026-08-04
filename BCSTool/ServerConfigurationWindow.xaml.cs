using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BCSTool.Models;
using BCSTool.Services;

namespace BCSTool;

/// <summary>
/// Dedicated server configuration editor.
/// </summary>
public partial class ServerConfigurationWindow : Window
{
    private readonly CoopConfigService _configService;

    private DedicatedServerConfig _config =
        new();

    private bool _updatingPasswordControls;


    public ServerConfigurationWindow(
        CoopConfigService configService)
    {
        InitializeComponent();

        _configService =
            configService;

        ConfigPathText.Text =
            _configService.ServerConfigPath;

        LoadConfiguration();
    }


    private void LoadConfiguration()
    {
        try
        {
            _config =
                _configService.LoadServerConfig();

            DataContext =
                _config;

            SetPasswordControls(
                _config.Password);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Server Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    /// <summary>
    /// Saves silently on success. Validation and IO failures still show an
    /// error because the user needs actionable feedback when a save fails.
    /// </summary>
    private bool SaveConfiguration()
    {
        if (HasValidationErrors(this))
        {
            MessageBox.Show(
                this,
                "One or more values are not valid. Correct the highlighted fields before saving.",
                "Server Configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        try
        {
            _configService.SaveServerConfig(
                _config);

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Server Configuration",
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


    /// <summary>
    /// Opens Bannerlord Coop's dedicated-server save directory:
    ///
    /// Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\Game Saves
    ///
    /// The base directory is derived from ServerConfigPath so it follows the
    /// same Documents-folder resolution already used by CoopConfigService.
    /// </summary>
    private void OpenSaveFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var dedicatedServerDirectory =
                Path.GetDirectoryName(
                    _configService.ServerConfigPath);

            if (string.IsNullOrWhiteSpace(dedicatedServerDirectory))
            {
                throw new InvalidOperationException(
                    "Could not determine the DedicatedServer directory.");
            }

            var saveFolder =
                Path.Combine(
                    dedicatedServerDirectory,
                    "Game Saves");

            if (!Directory.Exists(saveFolder))
            {
                MessageBox.Show(
                    this,
                    $"The save folder does not exist yet:\n\n{saveFolder}\n\n" +
                    "Start the server and create or save a game first, then try again.",
                    "Game Saves",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        saveFolder,
                    UseShellExecute =
                        true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open the save folder.\n\n{ex.Message}",
                "Game Saves",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    private void ServerPasswordBox_PasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingPasswordControls)
            return;

        _updatingPasswordControls = true;

        try
        {
            _config.Password =
                ServerPasswordBox.Password;

            VisibleServerPasswordTextBox.Text =
                ServerPasswordBox.Password;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private void VisibleServerPasswordTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_updatingPasswordControls)
            return;

        _updatingPasswordControls = true;

        try
        {
            _config.Password =
                VisibleServerPasswordTextBox.Text;

            ServerPasswordBox.Password =
                VisibleServerPasswordTextBox.Text;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private void ShowPasswordCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        var showPassword =
            ShowPasswordCheckBox.IsChecked == true;

        _updatingPasswordControls = true;

        try
        {
            if (showPassword)
            {
                VisibleServerPasswordTextBox.Text =
                    ServerPasswordBox.Password;
            }
            else
            {
                ServerPasswordBox.Password =
                    VisibleServerPasswordTextBox.Text;
            }

            ServerPasswordBox.Visibility =
                showPassword
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            VisibleServerPasswordTextBox.Visibility =
                showPassword
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
    }


    private void SetPasswordControls(
        string password)
    {
        _updatingPasswordControls = true;

        try
        {
            ServerPasswordBox.Password =
                password;

            VisibleServerPasswordTextBox.Text =
                password;
        }
        finally
        {
            _updatingPasswordControls = false;
        }
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
