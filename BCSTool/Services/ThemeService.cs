using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Applies the selected application resource palette and persists the choice.
/// Theme brushes use DynamicResource references, so every open window updates
/// immediately without being recreated.
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settingsService;

    public ThemeService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public IReadOnlyList<ApplicationTheme> AvailableThemes { get; } =
        Enum.GetValues<ApplicationTheme>();

    public ApplicationTheme CurrentTheme { get; private set; } =
        ApplicationTheme.Light;

    public void Initialize()
    {
        var savedTheme =
            _settingsService.LoadApplicationTheme();

        ApplyThemeResources(savedTheme);
        CurrentTheme = savedTheme;
    }

    public async Task SetThemeAsync(ApplicationTheme theme)
    {
        if (theme == CurrentTheme)
            return;

        var previousTheme = CurrentTheme;

        ApplyThemeResources(theme);
        CurrentTheme = theme;

        try
        {
            await _settingsService.SaveApplicationThemeAsync(theme);
        }
        catch
        {
            ApplyThemeResources(previousTheme);
            CurrentTheme = previousTheme;
            throw;
        }
    }

    private static void ApplyThemeResources(ApplicationTheme theme)
    {
        var resources =
            Application.Current.Resources.MergedDictionaries;
        var themeDictionary = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Themes/{theme}Theme.xaml",
                UriKind.Absolute)
        };

        for (var index = resources.Count - 1; index >= 0; index--)
        {
            var source =
                resources[index].Source?.OriginalString;

            if (
                source is not null &&
                source.Contains(
                    "Themes/",
                    StringComparison.OrdinalIgnoreCase) &&
                source.EndsWith(
                    "Theme.xaml",
                    StringComparison.OrdinalIgnoreCase))
            {
                resources.RemoveAt(index);
            }
        }

        resources.Insert(0, themeDictionary);
    }
}
