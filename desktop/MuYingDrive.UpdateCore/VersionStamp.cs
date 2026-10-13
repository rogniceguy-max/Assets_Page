namespace MuYingDrive.UpdateCore;

/// <summary>
/// Normalizes a Windows version resource string to the strict three-component
/// (X.Y.Z) form the update protocol requires.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.FileVersionInfo.ProductVersion"/> carries the
/// assembly informational version, which the .NET SDK suffixes with
/// "+&lt;source revision&gt;" unless source-revision stamping is disabled. Parsing
/// that raw value as three dot-separated integers fails, so every read site must
/// go through <see cref="NormalizeProductVersion"/> rather than splitting the
/// string itself. <c>FileVersion</c> has a fourth component ("X.Y.Z.0"), which is
/// why the first three components are taken rather than requiring exactly three.
/// </remarks>
public static class VersionStamp
{
    /// <summary>
    /// Returns the first three numeric components of <paramref name="raw"/> with any
    /// "+metadata" suffix removed, or <see langword="null"/> when the value does not
    /// contain three numeric components.
    /// </summary>
    public static string? NormalizeProductVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var withoutBuildMetadata = raw.Split('+', 2)[0];
        var components = withoutBuildMetadata.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 3)
            return null;

        var firstThree = components.Take(3).ToArray();
        // char.IsAsciiDigit rather than char.IsDigit: int/parse semantics accept
        // non-ASCII digits, which must never reach the manifest version comparison.
        if (firstThree.Any(component => component.Length == 0 || !component.All(char.IsAsciiDigit)))
            return null;

        return string.Join('.', firstThree);
    }
}
