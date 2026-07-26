using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DeleteAudit.Domain;

namespace DeleteAudit.Infrastructure.Parsing;

public sealed partial class WindowsEventXmlParser(TimeProvider? timeProvider = null)
{
    private const long DeleteMask = 0x0001_0000;
    private const long DeleteChildMask = 0x0000_0040;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public WindowsEventParseResult Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4 * 1024 * 1024
            };

            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            return Failure(
                ParseErrorCode.MalformedXml,
                $"Malformed Windows event XML: {exception.Message}",
                xml);
        }

        var system = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "System");
        if (system is null)
        {
            return Failure(
                ParseErrorCode.MissingSystemSection,
                "The event does not contain a System section.",
                xml);
        }

        var eventIdText = ChildValue(system, "EventID");
        if (eventIdText is null)
        {
            return Failure(ParseErrorCode.MissingEventId, "EventID is missing.", xml);
        }

        if (!int.TryParse(eventIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId))
        {
            return Failure(ParseErrorCode.InvalidEventId, "EventID is not a valid integer.", xml);
        }

        var dataWarnings = new List<string>();
        var eventData = ReadEventData(document, dataWarnings);
        var timestampText = GetValue(eventData, "UtcTime")
            ?? system
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == "TimeCreated")
                ?.Attribute("SystemTime")
                ?.Value;

        if (!TryParseUtc(timestampText, out var eventTimeUtc))
        {
            return Failure(
                ParseErrorCode.InvalidTimestamp,
                "The event does not contain a valid UTC timestamp.",
                xml);
        }

        var computer = NullIfWhiteSpace(ChildValue(system, "Computer"));
        var channel = NullIfWhiteSpace(ChildValue(system, "Channel"));
        var provider = system
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Provider")
            ?.Attribute("Name")
            ?.Value;
        var source = GetSource(eventId, provider);
        var recordId = ParseInt64(ChildValue(system, "EventRecordID"));
        var rawEventId = CreateStableId(xml);
        var rawEvent = new RawWindowsEvent(
            rawEventId,
            source,
            computer,
            channel,
            NullIfWhiteSpace(provider),
            eventId,
            recordId,
            eventTimeUtc,
            _timeProvider.GetUtcNow(),
            xml,
            eventData,
            dataWarnings);

        return source switch
        {
            WindowsEventSource.SysmonDelete => ParseSysmonDelete(rawEvent),
            WindowsEventSource.SysmonProcess => ParseSysmonProcess(rawEvent),
            WindowsEventSource.Security4663 => ParseSecurity4663(rawEvent),
            _ => new WindowsEventParseResult(
                rawEvent,
                null,
                null,
                null,
                new WindowsEventParseError(
                    ParseErrorCode.UnsupportedEvent,
                    $"Event ID {eventId} is not supported by the offline parser.",
                    xml))
        };
    }

    private static WindowsEventParseResult ParseSysmonDelete(RawWindowsEvent raw)
    {
        var data = raw.EventData;
        var processId = ParseProcessId(GetValue(data, "ProcessId"));
        var fullPath = NullIfWhiteSpace(GetValue(data, "TargetFilename"));
        var processPath = NullIfWhiteSpace(GetValue(data, "Image"));
        var processGuid = NullIfWhiteSpace(GetValue(data, "ProcessGuid"));
        var userName = NullIfWhiteSpace(GetValue(data, "User"));

        var missing = Missing(
            ("fullPath", fullPath),
            ("processId", processId),
            ("processPath", processPath),
            ("processGuid", processGuid),
            ("commandLine", null),
            ("parentProcess", null),
            ("userName", userName),
            ("userSid", null),
            ("deletePermission", null));

        var deleteEvent = new NormalizedDeleteEvent(
            $"del-{raw.RawEventId}",
            raw.RawEventId,
            raw.ComputerName,
            raw.EventId,
            raw.EventRecordId,
            raw.EventTimeUtc,
            fullPath,
            AuditObjectKind.Unknown,
            processId,
            processPath,
            processGuid,
            null,
            null,
            null,
            null,
            userName,
            null,
            DeletePermissionType.NotObserved,
            raw.EventId == 23,
            missing);

        return new WindowsEventParseResult(raw, deleteEvent, null, null, null);
    }

    private static WindowsEventParseResult ParseSysmonProcess(RawWindowsEvent raw)
    {
        var data = raw.EventData;
        var processId = ParseProcessId(GetValue(data, "ProcessId"));
        var processGuid = NullIfWhiteSpace(GetValue(data, "ProcessGuid"));
        var processPath = NullIfWhiteSpace(GetValue(data, "Image"));
        var commandLine = NullIfWhiteSpace(GetValue(data, "CommandLine"));
        var parentProcessId = ParseProcessId(GetValue(data, "ParentProcessId"));
        var parentProcessPath = NullIfWhiteSpace(GetValue(data, "ParentImage"));
        var parentProcessGuid = NullIfWhiteSpace(GetValue(data, "ParentProcessGuid"));
        var userName = NullIfWhiteSpace(GetValue(data, "User"));

        var missing = Missing(
            ("processId", processId),
            ("processGuid", processGuid),
            ("processPath", processPath),
            ("commandLine", commandLine),
            ("parentProcessId", parentProcessId),
            ("parentProcessPath", parentProcessPath),
            ("parentProcessGuid", parentProcessGuid),
            ("userName", userName),
            ("userSid", null));

        var process = new ProcessContextEvent(
            raw.RawEventId,
            raw.ComputerName,
            raw.EventRecordId,
            raw.EventTimeUtc,
            processId,
            processGuid,
            processPath,
            commandLine,
            parentProcessId,
            parentProcessPath,
            parentProcessGuid,
            userName,
            null,
            missing);

        return new WindowsEventParseResult(raw, null, process, null, null);
    }

    private static WindowsEventParseResult ParseSecurity4663(RawWindowsEvent raw)
    {
        var data = raw.EventData;
        var accessList = GetValue(data, "AccessList");
        var accessMask = ParseInt64(GetValue(data, "AccessMask"));
        var permission = ParseDeletePermission(accessList, accessMask);

        if (permission == DeletePermissionType.NotObserved)
        {
            return new WindowsEventParseResult(raw, null, null, null, null);
        }

        var objectPath = NullIfWhiteSpace(GetValue(data, "ObjectName"));
        var processId = ParseProcessId(GetValue(data, "ProcessId"));
        var processPath = NullIfWhiteSpace(GetValue(data, "ProcessName"));
        var userName = NullIfWhiteSpace(GetValue(data, "SubjectUserName"));
        var userSid = NullIfWhiteSpace(GetValue(data, "SubjectUserSid"));
        var missing = Missing(
            ("objectPath", objectPath),
            ("processId", processId),
            ("processPath", processPath),
            ("userName", userName),
            ("userSid", userSid));

        var evidence = new SecurityDeleteEvidence(
            raw.RawEventId,
            raw.ComputerName,
            raw.EventRecordId,
            raw.EventTimeUtc,
            objectPath,
            processId,
            processPath,
            userName,
            userSid,
            permission,
            missing);

        return new WindowsEventParseResult(raw, null, null, evidence, null);
    }

    private static DeletePermissionType ParseDeletePermission(string? accessList, long? accessMask)
    {
        var hasDelete = accessMask is not null && (accessMask.Value & DeleteMask) != 0;
        var hasDeleteChild = accessMask is not null && (accessMask.Value & DeleteChildMask) != 0;

        if (!string.IsNullOrWhiteSpace(accessList))
        {
            hasDelete |= accessList.Contains("%%1537", StringComparison.OrdinalIgnoreCase)
                || DeleteTokenRegex().IsMatch(accessList);
            hasDeleteChild |= accessList.Contains("%%4424", StringComparison.OrdinalIgnoreCase)
                || DeleteChildTokenRegex().IsMatch(accessList);
        }

        return (hasDelete, hasDeleteChild) switch
        {
            (true, true) => DeletePermissionType.DeleteAndDeleteChild,
            (true, false) => DeletePermissionType.Delete,
            (false, true) => DeletePermissionType.DeleteChild,
            _ => DeletePermissionType.NotObserved
        };
    }

    private static Dictionary<string, string?> ReadEventData(
        XDocument document,
        List<string> warnings)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var eventData = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "EventData");

        if (eventData is null)
        {
            return values;
        }

        foreach (var element in eventData.Elements().Where(item => item.Name.LocalName == "Data"))
        {
            var name = NullIfWhiteSpace(element.Attribute("Name")?.Value);
            if (name is null)
            {
                warnings.Add("event_data_without_name");
                continue;
            }

            if (!values.TryAdd(name, element.Value))
            {
                warnings.Add($"duplicate_event_data_field:{name}");
            }
        }

        return values;
    }

    private static WindowsEventSource GetSource(int eventId, string? provider)
    {
        if (string.Equals(
            provider,
            "Microsoft-Windows-Sysmon",
            StringComparison.OrdinalIgnoreCase))
        {
            return eventId switch
            {
                23 or 26 => WindowsEventSource.SysmonDelete,
                1 => WindowsEventSource.SysmonProcess,
                _ => WindowsEventSource.Unsupported
            };
        }

        return eventId == 4663
            && string.Equals(
                provider,
                "Microsoft-Windows-Security-Auditing",
                StringComparison.OrdinalIgnoreCase)
                ? WindowsEventSource.Security4663
                : WindowsEventSource.Unsupported;
    }

    private static WindowsEventParseResult Failure(
        ParseErrorCode code,
        string message,
        string xml) =>
        new(null, null, null, null, new WindowsEventParseError(code, message, xml));

    private static string? ChildValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value;

    private static string? GetValue(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParseProcessId(string? value)
    {
        var parsed = ParseInt64(value);
        return parsed is >= 0 and <= int.MaxValue ? (int)parsed.Value : null;
    }

    private static long? ParseInt64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var style = NumberStyles.Integer;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
            style = NumberStyles.AllowHexSpecifier;
        }

        return long.TryParse(trimmed, style, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryParseUtc(string? value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp))
        {
            timestamp = timestamp.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static string[] Missing(params (string Name, object? Value)[] values) =>
        values
            .Where(item => item.Value is null)
            .Select(item => item.Name)
            .ToArray();

    private static string CreateStableId(string xml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();

    [GeneratedRegex(@"(?<![A-Z_])DELETE(?![A-Z_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteTokenRegex();

    [GeneratedRegex(@"DELETE[_ ]?CHILD", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteChildTokenRegex();
}
