using System.Windows.Input;

namespace BCSTool.Infrastructure;

/// <summary>
/// ICommand implementation for synchronous UI actions.
///
/// WPF buttons bind to ICommand instead of directly calling methods.
/// RelayCommand lets us wrap an Action and an optional "can execute" test.
///
/// Example:
///     new RelayCommand(OpenLogs)
///
/// This is intentionally tiny so the MVVM plumbing stays transparent.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    // If no can-execute function was supplied, the command is always enabled.
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    // CommandManager.RequerySuggested is WPF's built-in signal telling
    // buttons and menu items to re-check whether they should be enabled.
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
