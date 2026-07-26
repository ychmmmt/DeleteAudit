using System.Windows.Input;

namespace DeleteAudit.Application.Presentation;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isExecuting;

    public AsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _onError?.Invoke(exception);
        }
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _isExecuting = true;
        NotifyCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
