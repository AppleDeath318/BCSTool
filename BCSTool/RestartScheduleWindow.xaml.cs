using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BCSTool.ViewModels;

namespace BCSTool;

public partial class RestartScheduleWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private string _savedSnapshot;

    public IReadOnlyList<int> RestartHourOptions { get; } =
        Enumerable.Range(1, 24).ToArray();

    public IReadOnlyList<int> MinuteOptions { get; } =
        Enumerable.Range(0, 60).ToArray();

    public IReadOnlyList<int> WarningMinuteOptions { get; } =
        Enumerable.Range(0, 11).ToArray();

    public int RestartEveryHours { get; set; }
    public int RestartMinute { get; set; }
    public int WarningMinutesBefore { get; set; }

    public RestartScheduleWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        RestartEveryHours = viewModel.Settings.RestartEveryHours;
        RestartMinute = viewModel.Settings.RestartMinute;
        WarningMinutesBefore = viewModel.Settings.WarningMinutesBefore;
        DataContext = this;
        _savedSnapshot = CaptureSnapshot();
    }

    private async void Apply_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ApplyAsync();
    }

    private async void ApplyAndClose_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!await ApplyAsync())
            return;

        _allowClose = true;
        Close();
    }

    private async Task<bool> ApplyAsync()
    {
        try
        {
            await _viewModel.ApplyRestartScheduleAsync(
                RestartEveryHours,
                RestartMinute,
                WarningMinutesBefore);

            _savedSnapshot = CaptureSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not apply the restart schedule.\n\n{ex.Message}",
                "Restart Schedule",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void Close_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (
            _allowClose ||
            string.Equals(
                _savedSnapshot,
                CaptureSnapshot(),
                StringComparison.Ordinal))
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Close without applying the restart schedule changes?",
            "Unsaved Restart Schedule",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private string CaptureSnapshot() =>
        $"{RestartEveryHours}|{RestartMinute}|{WarningMinutesBefore}";
}
