using DeleteAudit.Application.Importing;
using DeleteAudit.Application.Presentation;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;

namespace DeleteAudit.UnitTests.Presentation;

public sealed class ViewerPresentationTests
{
    [Theory]
    [InlineData(ImportStatus.Completed, "导入完成")]
    [InlineData(ImportStatus.AlreadyImported, "already_imported")]
    [InlineData(ImportStatus.PartialFailure, "partial")]
    [InlineData(ImportStatus.Failed, "failed")]
    public void ImportStatusLabelsAreExplicit(
        ImportStatus status,
        string expectedText)
    {
        var label = ImportStatusPresentation.Label(status);

        Assert.Contains(expectedText, label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledFileSelectionDoesNotImport()
    {
        var importService = new FakeImportService();
        var viewModel = new DashboardViewModel(
            new FakeQueryService(),
            importService,
            new FakeFilePicker(null));

        await viewModel.ImportSelectedFileAsync();

        Assert.Equal(0, importService.CallCount);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ImportExceptionBecomesVisibleError()
    {
        var importService = new FakeImportService
        {
            Exception = new InvalidOperationException("fixture import failure")
        };
        var viewModel = new DashboardViewModel(
            new FakeQueryService(),
            importService,
            new FakeFilePicker(@"C:\Fixtures\offline.xml"));

        await viewModel.ImportSelectedFileAsync();

        Assert.True(viewModel.HasError);
        Assert.Equal("fixture import failure", viewModel.ErrorMessage);
    }

    [Fact]
    public void MissingDisplayValuesUseUnknown()
    {
        Assert.Equal(ViewerDisplay.Unknown, ViewerDisplay.Value((string?)null));
        Assert.Equal(ViewerDisplay.Unknown, ViewerDisplay.Value("  "));
        Assert.Equal(ViewerDisplay.Unknown, ViewerDisplay.Value((int?)null));
    }

    [Fact]
    public async Task PaginationRequestsOnlyDefaultPageSize()
    {
        var queryService = new FakeQueryService
        {
            Imports = query =>
            {
                var items = Enumerable
                    .Range(query.Page.Offset, query.Page.Limit)
                    .Select(CreateImportRow)
                    .ToArray();
                return new PageResult<ImportHistoryRow>(
                    items,
                    120,
                    query.Page.Offset,
                    query.Page.Limit);
            }
        };
        var viewModel = new ImportHistoryViewModel(queryService);

        await viewModel.LoadAsync(resetPage: true);
        await viewModel.NextPageCommand.ExecuteAsync();

        Assert.Equal(2, queryService.ImportQueries.Count);
        Assert.All(
            queryService.ImportQueries,
            query => Assert.Equal(
                PagedViewModelBase<ImportHistoryRow>.DefaultPageSize,
                query.Page.Limit));
        Assert.Equal(50, viewModel.Items.Count);
        Assert.Equal(50, viewModel.Offset);
        Assert.True(viewModel.HasPrevious);
        Assert.True(viewModel.HasNext);
    }

    [Fact]
    public async Task OversizedPageIsRejectedInsteadOfLoaded()
    {
        var queryService = new FakeQueryService
        {
            Imports = query =>
            {
                var items = Enumerable
                    .Range(0, query.Page.Limit + 1)
                    .Select(CreateImportRow)
                    .ToArray();
                return new PageResult<ImportHistoryRow>(
                    items,
                    items.Length,
                    query.Page.Offset,
                    query.Page.Limit);
            }
        };
        var viewModel = new ImportHistoryViewModel(queryService);

        await viewModel.LoadAsync(resetPage: true);

        Assert.Empty(viewModel.Items);
        Assert.True(viewModel.HasError);
        Assert.Contains("超过每页", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawXmlPreviewIsGetterOnlyAndPreserved()
    {
        const string xml = "<Event><System /></Event>";
        var queryService = new FakeQueryService
        {
            RawXml = RawXmlDocument.CreatePreview("delete-1", xml, xml.Length)
        };
        var viewModel = new RawXmlViewModel(queryService, new FakeClipboard());

        await viewModel.LoadAsync("delete-1");

        var previewProperty = typeof(RawXmlViewModel)
            .GetProperty(nameof(RawXmlViewModel.PreviewText));
        Assert.NotNull(previewProperty);
        Assert.False(previewProperty.CanWrite);
        Assert.True(viewModel.IsReadOnly);
        Assert.Equal(xml, viewModel.PreviewText);
        Assert.True(viewModel.IsAvailable);
        Assert.False(viewModel.IsTruncated);
        Assert.Equal(string.Empty, viewModel.TruncationNotice);
    }

    [Fact]
    public async Task TruncatedRawXmlShowsExplicitNoticeAndLengths()
    {
        var preview = new string('x', RawXmlDocument.MaxPreviewCharacters);
        var queryService = new FakeQueryService
        {
            RawXml = RawXmlDocument.CreatePreview(
                "delete-1",
                preview,
                RawXmlDocument.MaxPreviewCharacters + 1_000L)
        };
        var viewModel = new RawXmlViewModel(queryService, new FakeClipboard());

        await viewModel.LoadAsync("delete-1");

        Assert.True(viewModel.IsTruncated);
        Assert.Equal(
            "内容较大，当前仅显示前 262,144 个字符；数据库中的原始证据未被修改。",
            viewModel.TruncationNotice);
        Assert.Equal(
            "原始字符数：263,144；当前预览字符数：262,144",
            viewModel.LengthSummary);
    }

    [Fact]
    public async Task NotTruncatedRawXmlShowsNoTruncationWarning()
    {
        const string xml = "<Event id=\"small\" />";
        var queryService = new FakeQueryService
        {
            RawXml = RawXmlDocument.CreatePreview("delete-1", xml, xml.Length)
        };
        var viewModel = new RawXmlViewModel(queryService, new FakeClipboard());

        await viewModel.LoadAsync("delete-1");

        Assert.False(viewModel.IsTruncated);
        Assert.Equal(string.Empty, viewModel.TruncationNotice);
        Assert.Equal(
            "原始字符数：20；当前预览字符数：20",
            viewModel.LengthSummary);
    }

    [Fact]
    public async Task CopyPreviewCommandCopiesExactlyThePreviewText()
    {
        var preview = $"<Event>{new string('y', 128)}</Event>";
        var clipboard = new FakeClipboard();
        var queryService = new FakeQueryService
        {
            RawXml = RawXmlDocument.CreatePreview(
                "delete-1",
                preview,
                preview.Length + 5_000L)
        };
        var viewModel = new RawXmlViewModel(queryService, clipboard);

        await viewModel.LoadAsync("delete-1");
        await viewModel.CopyPreviewCommand.ExecuteAsync();

        Assert.Equal(preview, Assert.Single(clipboard.CopiedTexts));
    }

    [Fact]
    public async Task MissingRawXmlKeepsSafeEmptyStateAndDisablesCopy()
    {
        var clipboard = new FakeClipboard();
        var queryService = new FakeQueryService { RawXml = null };
        var viewModel = new RawXmlViewModel(queryService, clipboard);

        await viewModel.LoadAsync("delete-missing");
        await viewModel.CopyPreviewCommand.ExecuteAsync();

        Assert.True(viewModel.HasError);
        Assert.Equal("找不到所选删除事件的原始 XML。", viewModel.ErrorMessage);
        Assert.False(viewModel.IsAvailable);
        Assert.False(viewModel.IsTruncated);
        Assert.False(viewModel.CopyPreviewCommand.CanExecute(null));
        Assert.Empty(clipboard.CopiedTexts);
    }

    [Fact]
    public async Task RawXmlQueryFailureRestoresBusyAndShowsVisibleError()
    {
        var queryService = new FakeQueryService
        {
            RawXmlException = new InvalidOperationException("fixture raw xml failure")
        };
        var viewModel = new RawXmlViewModel(queryService, new FakeClipboard());

        await viewModel.LoadAsync("delete-1");

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasError);
        Assert.Equal("fixture raw xml failure", viewModel.ErrorMessage);
        Assert.False(viewModel.IsAvailable);
    }

    private static ImportHistoryRow CreateImportRow(int index) =>
        new(
            $"import-{index}",
            "multi_xml",
            $"fixture-{index}.xml",
            $@"C:\Fixtures\fixture-{index}.xml",
            100,
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 12, 0, 1, TimeSpan.Zero),
            1,
            1,
            0,
            0,
            "1.2.0-test",
            2,
            "completed",
            "complete",
            null,
            null);

    private sealed class FakeFilePicker : IOfflineFilePicker
    {
        private readonly string? _selectedPath;

        public FakeFilePicker(string? selectedPath)
        {
            _selectedPath = selectedPath;
        }

        public Task<string?> PickSingleFileAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_selectedPath);
        }
    }

    private sealed class FakeImportService : IOfflineViewerImportService
    {
        public int CallCount { get; private set; }

        public Exception? Exception { get; init; }

        public Task<ImportResult> ImportAsync(
            string inputFilePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (Exception is not null)
            {
                return Task.FromException<ImportResult>(Exception);
            }

            return Task.FromResult(CreateImportResult(ImportStatus.Completed));
        }

        private static ImportResult CreateImportResult(ImportStatus status) =>
            new(
                status,
                null,
                new ImportReport(
                    null,
                    0,
                    0,
                    new Dictionary<int, int>(),
                    0,
                    new Dictionary<CorrelationConfidence, int>(),
                    0,
                    0,
                    [],
                    []),
                false,
                null,
                null);
    }

    private sealed class FakeQueryService : IViewerQueryService
    {
        public Func<ImportHistoryQuery, PageResult<ImportHistoryRow>>? Imports
        {
            get;
            init;
        }

        public List<ImportHistoryQuery> ImportQueries { get; } = [];

        public RawXmlDocument? RawXml { get; init; }

        public Exception? RawXmlException { get; init; }

        public Task<ViewerDatabaseStatus> GetDatabaseStatusAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ViewerDatabaseStatus(
                    ViewerDatabaseState.Ready,
                    "数据库已就绪。",
                    []));
        }

        public Task<DashboardSummary> GetDashboardAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new DashboardSummary(0, 0, 0, 0, 0, 0, 0, null));
        }

        public Task<PageResult<ImportHistoryRow>> GetImportsAsync(
            ImportHistoryQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportQueries.Add(query);
            return Task.FromResult(
                Imports?.Invoke(query)
                ?? new PageResult<ImportHistoryRow>(
                    [],
                    0,
                    query.Page.Offset,
                    query.Page.Limit));
        }

        public Task<PageResult<DeleteSessionRow>> GetSessionsAsync(
            AuditQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new PageResult<DeleteSessionRow>(
                    [],
                    0,
                    query.Page.Offset,
                    query.Page.Limit));
        }

        public Task<PageResult<DeleteEventRow>> GetEventsAsync(
            AuditQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new PageResult<DeleteEventRow>(
                    [],
                    0,
                    query.Page.Offset,
                    query.Page.Limit));
        }

        public Task<PageResult<DiagnosticRow>> GetDiagnosticsAsync(
            DiagnosticQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new PageResult<DiagnosticRow>(
                    [],
                    0,
                    query.Page.Offset,
                    query.Page.Limit));
        }

        public Task<RawXmlDocument?> GetDeleteEventRawXmlAsync(
            string deleteEventId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RawXmlException is not null)
            {
                return Task.FromException<RawXmlDocument?>(RawXmlException);
            }

            return Task.FromResult(RawXml);
        }
    }

    private sealed class FakeClipboard : IRawXmlPreviewClipboard
    {
        public List<string> CopiedTexts { get; } = [];

        public void SetPreviewText(string previewText)
        {
            ArgumentNullException.ThrowIfNull(previewText);
            CopiedTexts.Add(previewText);
        }
    }
}
