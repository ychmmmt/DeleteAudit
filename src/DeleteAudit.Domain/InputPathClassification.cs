namespace DeleteAudit.Domain;

/// <summary>
/// How an offline import input path is treated before anything touches the
/// filesystem. The kinds are ordered by the decision they drive, not by risk.
/// </summary>
public enum InputPathKind
{
    /// <summary>
    /// Not a path this application accepts as an explicit input file (empty,
    /// relative, or rooted but not fully qualified). Existing invalid-path
    /// handling applies unchanged.
    /// </summary>
    Other = 0,

    /// <summary>
    /// Win32 device namespace (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>), including
    /// <c>\\?\UNC\server\share\…</c>. Always rejected outright; never offered for
    /// confirmation.
    /// </summary>
    DeviceNamespace = 1,

    /// <summary>
    /// A plain UNC share such as <c>\\server\share\file.evtx</c>. Reading one
    /// contacts a remote host, so it needs an explicit per-import confirmation.
    /// </summary>
    NetworkShare = 2,

    /// <summary>
    /// A fully qualified path that is not a device path and not a literal UNC
    /// share. This is the normal flow. A mapped network drive letter looks like
    /// this and is deliberately not detected here — see the class remarks.
    /// </summary>
    LocalFullyQualified = 3
}

/// <summary>
/// Classifies an import input path from its text alone.
/// </summary>
/// <remarks>
/// <para>
/// Classification performs no I/O whatsoever: it never probes the path, never
/// resolves a link, never enumerates a directory or a volume, and never contacts
/// a server. That is what makes it safe to run before the confirmation prompt —
/// deciding that a path is remote must not itself reach the network.
/// </para>
/// <para>
/// Both the UI and the import service boundary use this one implementation so the
/// two cannot drift apart.
/// </para>
/// <para>
/// Scope: only literal UNC syntax is recognised. A mapped network drive (for
/// example <c>Z:\</c>) is textually indistinguishable from a local volume and is
/// classified as <see cref="InputPathKind.LocalFullyQualified"/>. Detecting one
/// would require querying the drive, which is exactly the I/O this type refuses
/// to do. No path is ever rejected because of its drive letter.
/// </para>
/// </remarks>
public static class InputPathClassifier
{
    /// <summary>
    /// Classifies <paramref name="path"/> using string analysis only.
    /// </summary>
    public static InputPathKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return InputPathKind.Other;
        }

        // Order matters. A device path must be recognised first, otherwise
        // \\?\UNC\server\share\file.evtx would look like a confirmable share.
        if (IsDeviceNamespace(path))
        {
            return InputPathKind.DeviceNamespace;
        }

        if (IsNetworkShare(path))
        {
            return InputPathKind.NetworkShare;
        }

        return Path.IsPathFullyQualified(path)
            ? InputPathKind.LocalFullyQualified
            : InputPathKind.Other;
    }

    private static bool IsDeviceNamespace(string path)
    {
        // \\?\  \\.\  and the forward-slash spellings Windows also accepts.
        if (path.Length >= 4
            && IsSeparator(path[0])
            && IsSeparator(path[1])
            && (path[2] == '?' || path[2] == '.')
            && IsSeparator(path[3]))
        {
            return true;
        }

        // \??\ — the NT object namespace prefix, which has a single leading
        // separator and therefore never matches the share shape below.
        return path.Length >= 4
            && IsSeparator(path[0])
            && path[1] == '?'
            && path[2] == '?'
            && IsSeparator(path[3]);
    }

    private static bool IsNetworkShare(string path) =>
        // Two leading separators followed by a real first component. Device paths
        // are already excluded by the caller, so anything left that starts this
        // way names a remote host.
        path.Length >= 3
        && IsSeparator(path[0])
        && IsSeparator(path[1])
        && !IsSeparator(path[2]);

    private static bool IsSeparator(char value) =>
        value is '\\' or '/';
}
