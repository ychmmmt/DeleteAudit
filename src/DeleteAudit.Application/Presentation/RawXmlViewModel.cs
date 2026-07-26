using System.Globalization;
using DeleteAudit.Application.Viewing;

namespace DeleteAudit.Application.Presentation;

public sealed class RawXmlViewModel : ViewModelBase
{
    private readonly IViewerQueryService _queryService;
    private readonly IRawXmlPreviewClipboard _previewClipboard;
    private RawXmlDocument? _document;

    public RawXmlViewModel(
        IViewerQueryService queryService,
        IRawXmlPreviewClipboard previewClipboard)
    {
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
        _previewClipboard = previewClipboard
            ?? throw new ArgumentNullException(nameof(previewClipboard));
        CopyPreviewCommand = new AsyncCommand(
            CopyPreviewAsync,
            () => !IsBusy && _document?.IsAvailable == true,
            ShowUnexpectedError);
    }

    public string ResourceId => ViewerDisplay.Value(_document?.ResourceId);

    public string PreviewText =>
        _document?.IsAvailable == true
            ? ViewerDisplay.Value(_document.PreviewText)
            : ViewerDisplay.Value(_document?.UnavailableReason);

    public bool IsAvailable => _document?.IsAvailable == true;

    public bool IsTruncated => _document?.IsTruncated == true;

    public string TruncationNotice =>
        _document?.IsTruncated == true
            ? string.Format(
                CultureInfo.InvariantCulture,
                "内容较大，当前仅显示前 {0:N0} 个字符；数据库中的原始证据未被修改。",
                _document.PreviewLimit)
            : string.Empty;

    public string LengthSummary =>
        _document?.IsAvailable == true
            ? string.Format(
                CultureInfo.InvariantCulture,
                "原始字符数：{0:N0}；当前预览字符数：{1:N0}",
                _document.OriginalLength,
                _document.PreviewLength)
            : string.Empty;

    public bool IsReadOnly { get; } = true;

    public AsyncCommand CopyPreviewCommand { get; }

    public Task LoadAsync(string deleteEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteEventId);
        return RunSafelyAsync(async () =>
        {
            _document = null;
            NotifyDocumentChanged();

            _document = await _queryService
                .GetDeleteEventRawXmlAsync(deleteEventId)
                .ConfigureAwait(true);
            if (_document is null)
            {
                ErrorMessage = "找不到所选删除事件的原始 XML。";
            }

            NotifyDocumentChanged();
        });
    }

    protected override void OnBusyStateChanged()
    {
        CopyPreviewCommand.NotifyCanExecuteChanged();
        base.OnBusyStateChanged();
    }

    private Task CopyPreviewAsync()
    {
        if (_document is { IsAvailable: true, PreviewText: not null })
        {
            _previewClipboard.SetPreviewText(_document.PreviewText);
        }

        return Task.CompletedTask;
    }

    private void NotifyDocumentChanged()
    {
        OnPropertyChanged(nameof(ResourceId));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(TruncationNotice));
        OnPropertyChanged(nameof(LengthSummary));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }
}
