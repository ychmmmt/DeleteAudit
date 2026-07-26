namespace DeleteAudit.Application.Presentation;

public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _errorMessage;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnBusyStateChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    protected async Task RunSafelyAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "操作已取消。";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void ShowUnexpectedError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ErrorMessage = exception.Message;
    }

    protected virtual void OnBusyStateChanged()
    {
    }
}
