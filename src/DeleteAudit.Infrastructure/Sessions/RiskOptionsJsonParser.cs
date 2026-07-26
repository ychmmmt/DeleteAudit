using System.Text.Json;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Sessions;

public static class RiskOptionsJsonParser
{
    public static AuditRiskOptions Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var correlation = document.RootElement.GetProperty("Correlation");
        var options = new AuditRiskOptions(
            TimeSpan.FromSeconds(correlation.GetProperty("SessionIdleSeconds").GetInt32()),
            correlation.GetProperty("WarningCount").GetInt32(),
            correlation.GetProperty("CriticalCount").GetInt32());
        options.Validate();
        return options;
    }
}
