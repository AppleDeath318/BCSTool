using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BCSTool.Models;
using BCSTool.ViewModels;

namespace BCSTool;

public partial class ScheduledBroadcastsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private string _savedSnapshot;

    public ObservableCollection<ScheduledBroadcastEntry> Entries { get; } = new();

    public ScheduledBroadcastsWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = this;

        foreach (var entry in _viewModel.GetScheduledBroadcasts())
            Entries.Add(Clone(entry));

        _savedSnapshot = CaptureSnapshot();
        UpdateCountText();
    }

    private void Add_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (Entries.Count >= ScheduledBroadcastEntry.MaximumEntryCount)
        {
            MessageBox.Show(
                this,
                $"No more than {ScheduledBroadcastEntry.MaximumEntryCount} scheduled broadcasts are allowed.",
                "Scheduled Broadcasts",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var editor =
            new ScheduledBroadcastEditorWindow
            {
                Owner = this
            };

        if (editor.ShowDialog() != true || editor.Result is null)
            return;

        Entries.Add(editor.Result);
        BroadcastGrid.SelectedItem = editor.Result;
        BroadcastGrid.ScrollIntoView(editor.Result);
        UpdateCountText();
    }

    private void Edit_Click(
        object sender,
        RoutedEventArgs e)
    {
        EditSelectedEntry();
    }

    private void BroadcastGrid_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        EditSelectedEntry();
    }

    private void EditSelectedEntry()
    {
        if (BroadcastGrid.SelectedItem is not ScheduledBroadcastEntry selected)
            return;

        var editor =
            new ScheduledBroadcastEditorWindow(Clone(selected))
            {
                Owner = this
            };

        if (editor.ShowDialog() != true || editor.Result is null)
            return;

        var index = Entries.IndexOf(selected);

        if (index < 0)
            return;

        Entries[index] = editor.Result;
        BroadcastGrid.SelectedItem = editor.Result;
        UpdateCountText();
    }

    private void Remove_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (BroadcastGrid.SelectedItem is not ScheduledBroadcastEntry selected)
            return;

        var result = MessageBox.Show(
            this,
            $"Remove this scheduled broadcast?\n\n{selected.Message}",
            "Remove Scheduled Broadcast",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        Entries.Remove(selected);
        UpdateCountText();
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
            await _viewModel.ApplyScheduledBroadcastsAsync(
                Entries.Select(Clone));

            _savedSnapshot = CaptureSnapshot();
            UpdateCountText();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not apply the scheduled broadcasts.\n\n{ex.Message}",
                "Scheduled Broadcasts",
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
            "Close without applying the scheduled broadcast changes?",
            "Unsaved Scheduled Broadcasts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void UpdateCountText()
    {
        var enabled = Entries.Count(entry => entry.Enabled);
        CountText.Text = $"{enabled} enabled / {Entries.Count} total";
    }

    private string CaptureSnapshot() =>
        JsonSerializer.Serialize(Entries);

    private static ScheduledBroadcastEntry Clone(
        ScheduledBroadcastEntry entry) =>
        new()
        {
            Id = entry.Id,
            Enabled = entry.Enabled,
            IntervalMinutes = entry.IntervalMinutes,
            Message = entry.Message
        };
}
