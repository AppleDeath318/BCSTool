using System.Windows.Input;

namespace BCSTool.Infrastructure;

/// <summary>
/// ICommand implementation for asynchronous UI operations.
///
/// Server operations such as start, save, stop, and restart take time.
/// AsyncRelayCommand lets a Button invoke an async Task without blocking
/// the WPF UI thread.
///
/// The command disables itself while running so the user cannot accidentally
/// start the exact same operation twice at the same time.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        // Do not allow another click while the current asynchronous
        // operation is still running.
        return !_isExecuting && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            // Mark the command busy and ask WPF to refresh button states.
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
