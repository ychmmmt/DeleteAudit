using DeleteAudit.Domain;

namespace DeleteAudit.Application.Presentation;

public static class ImportStatusPresentation
{
    public static string Label(ImportStatus status) => status switch
    {
        ImportStatus.Completed => "导入完成",
        ImportStatus.AlreadyImported => "文件已导入（already_imported）",
        ImportStatus.PartialFailure => "部分导入成功（partial）",
        ImportStatus.Failed => "导入失败（failed）",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
