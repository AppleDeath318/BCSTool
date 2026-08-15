using System;
using System.Windows;
using BCSTool.Models;

namespace BCSTool;

public partial class ScheduledBroadcastEditorWindow : Window
{
    private readonly Guid _entryId;
    private readonly int _maximumIntervalMinutes;

    public ScheduledBroadcastEntry? Result { get; private set; }

    public ScheduledBroadcastEditorWindow(
        int maximumIntervalMinutes,
        ScheduledBroadcastEntry? entry = null)
    {
        InitializeComponent();

        _maximumIntervalMinutes = maximumIntervalMinutes;

        _entryId =
            entry is not null && entry.Id != Guid.Empty
                ? entry.Id
                : Guid.NewGuid();
        EnabledCheckBox.IsChecked = entry?.Enabled ?? true;
        IntervalTextBox.Text =
            (entry?.IntervalMinutes ?? 10).ToString();
        MessageTextBox.Text = entry?.Message ?? "";
    }

    private void Save_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (
            !int.TryParse(
                IntervalTextBox.Text.Trim(),
                out var intervalMinutes) ||
            intervalMinutes < ScheduledBroadcastEntry.MinimumIntervalMinutes ||
            intervalMinutes > _maximumIntervalMinutes)
        {
            MessageBox.Show(
                this,
                $"The interval is invalid. Enter a value between " +
                $"{ScheduledBroadcastEntry.MinimumIntervalMinutes} and " +
                $"{_maximumIntervalMinutes} minutes.",
                "Scheduled Broadcast",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            IntervalTextBox.Focus();
            IntervalTextBox.SelectAll();
            return;
        }

        var message =
            ScheduledBroadcastEntry.NormalizeMessage(
                MessageTextBox.Text);

        if (message.Length == 0)
        {
            MessageBox.Show(
                this,
                "Enter a broadcast message.",
                "Scheduled Broadcast",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            MessageTextBox.Focus();
            return;
        }

        if (message.Length > ScheduledBroadcastEntry.MaximumMessageLength)
        {
            MessageBox.Show(
                this,
                $"Broadcast messages cannot exceed " +
                $"{ScheduledBroadcastEntry.MaximumMessageLength} characters.",
                "Scheduled Broadcast",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            MessageTextBox.Focus();
            return;
        }

        Result =
            new ScheduledBroadcastEntry
            {
                Id = _entryId,
                Enabled = EnabledCheckBox.IsChecked == true,
                IntervalMinutes = intervalMinutes,
                Message = message
            };

        DialogResult = true;
    }

    private void Cancel_Click(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
