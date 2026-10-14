using System.Text.RegularExpressions;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Builds the "that one is taken, here is the next" names used when a file or folder
/// already exists at the destination.
/// </summary>
/// <remarks>
/// Splits on the last extension, the way Explorer does, so <c>照片.jpg</c> becomes
/// <c>照片 (2).jpg</c> and <c>archive.tar.gz</c> becomes <c>archive.tar (2).gz</c>. The
/// alternative — inserting before the whole remaining name — would give
/// <c>archive (2).tar.gz</c>, which reads as though the archive itself were the second
/// copy of something.
///
/// The point of this over prompting is a folder of two hundred files: a conflict that
/// stops and asks has to be answered two hundred times, and the answer is the same every
/// time.
/// </remarks>
public static class UniqueName
{
    /// <summary>The first number appended. " (1)" is skipped: it reads as if there is no original.</summary>
    public const int FirstSuffix = 2;

    /// <summary>
    /// How many candidates to try before giving up. Reaching this means the directory
    /// holds ~10,000 same-named items, which is a mess to fix rather than a name to invent.
    /// </summary>
    public const int MaxAttempts = 9999;

    private static readonly Regex TrailingSuffix = new(@" \((\d+)\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The name with any trailing " (n)" removed: <c>照片 (2).jpg</c> gives <c>照片.jpg</c>.
    /// A name that already looks like a base name comes back unchanged.
    /// </summary>
    public static string WithoutSuffix(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var (stem, extension) = Split(name);
        return TrailingSuffix.Replace(stem, string.Empty) + extension;
    }

    /// <summary>
    /// <paramref name="name"/> with <paramref name="index"/> appended before its extension:
    /// <c>照片.jpg</c> and 2 give <c>照片 (2).jpg</c>.
    /// </summary>
    public static string WithSuffix(string name, int index)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, FirstSuffix);
        var (stem, extension) = Split(name);
        return $"{TrailingSuffix.Replace(stem, string.Empty)} ({index}){extension}";
    }

    /// <summary>
    /// Splits a name into stem and extension. A leading dot belongs to the stem, so
    /// <c>.gitignore</c> is not treated as an extension of nothing.
    /// </summary>
    private static (string Stem, string Extension) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
    }
}
