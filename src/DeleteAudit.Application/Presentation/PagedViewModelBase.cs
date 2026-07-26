using System.Collections.ObjectModel;
using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public abstract class PagedViewModelBase<T> : ViewModelBase
{
    public const int DefaultPageSize = 50;

    private readonly ObservableCollection<T> _items = [];
    private long _totalCount;
    private int _offset;

    protected PagedViewModelBase()
    {
        Items = new ReadOnlyObservableCollection<T>(_items);
        RefreshCommand = new AsyncCommand(
            () => LoadAsync(resetPage: false),
            () => !IsBusy,
            ShowUnexpectedError);
        ApplyFiltersCommand = new AsyncCommand(
            () => LoadAsync(resetPage: true),
            () => !IsBusy,
            ShowUnexpectedError);
        PreviousPageCommand = new AsyncCommand(
            PreviousPageAsync,
            () => HasPrevious && !IsBusy,
            ShowUnexpectedError);
        NextPageCommand = new AsyncCommand(
            NextPageAsync,
            () => HasNext && !IsBusy,
            ShowUnexpectedError);
    }

    public ReadOnlyObservableCollection<T> Items { get; }

    public int PageSize => DefaultPageSize;

    public long TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int Offset
    {
        get => _offset;
        private set => SetProperty(ref _offset, value);
    }

    public bool HasPrevious => Offset > 0;

    public bool HasNext => Offset + Items.Count < TotalCount;

    public long CurrentPage =>
        TotalCount == 0 ? 0 : (Offset / PageSize) + 1;

    public long PageCount =>
        TotalCount == 0 ? 0 : ((TotalCount - 1) / PageSize) + 1;

    public string PageStatus =>
        TotalCount == 0
            ? "0 项"
            : $"第 {CurrentPage} / {PageCount} 页，共 {TotalCount} 项";

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand ApplyFiltersCommand { get; }

    public AsyncCommand PreviousPageCommand { get; }

    public AsyncCommand NextPageCommand { get; }

    public Task LoadAsync(bool resetPage = false) =>
        RunSafelyAsync(async () =>
        {
            if (resetPage)
            {
                Offset = 0;
            }

            var page = await QueryPageAsync(
                    new PageRequest(Offset, PageSize))
                .ConfigureAwait(true);
            if (page.Items.Count > PageSize)
            {
                throw new InvalidOperationException(
                    $"查询返回 {page.Items.Count} 项，超过每页 {PageSize} 项的限制。");
            }

            _items.Clear();
            foreach (var item in page.Items)
            {
                _items.Add(item);
            }

            TotalCount = page.TotalCount;
            Offset = page.Offset;
            NotifyPageStateChanged();
        });

    protected abstract Task<PageResult<T>> QueryPageAsync(PageRequest page);

    protected override void OnBusyStateChanged()
    {
        NotifyCommands();
        base.OnBusyStateChanged();
    }

    private Task PreviousPageAsync()
    {
        if (HasPrevious)
        {
            Offset = Math.Max(0, Offset - PageSize);
        }

        return LoadAsync();
    }

    private Task NextPageAsync()
    {
        if (HasNext)
        {
            Offset += PageSize;
        }

        return LoadAsync();
    }

    private void NotifyPageStateChanged()
    {
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageStatus));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyFiltersCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }
}
