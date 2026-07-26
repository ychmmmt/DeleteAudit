using System.Text.Json;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Importing;

public static class OfflineImportOptionsJsonParser
{
    public static OfflineImportOptions Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var section = document.RootElement.GetProperty("OfflineImport");
        var options = new OfflineImportOptions(
            section.GetProperty("MaximumFileSizeBytes").GetInt64(),
            section.GetProperty("JsonlOutputDirectory").GetString() ?? string.Empty,
            section.GetProperty("SchemaVersion").GetInt32());
        options.Validate();
        return options;
    }
}
