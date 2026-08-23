using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MetaTube.Helpers;

public static class SubtitleMatcher
{
    public const string ChineseSubtitle = "中文字幕";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".asf", ".avi", ".divx", ".flv", ".iso", ".m2ts", ".m4v", ".mkv",
        ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".rm", ".rmvb", ".ts", ".vob",
        ".webm", ".wmv"
    };

    private static readonly Regex ExternalSubtitleRegex = new(
        @"\.(ch[ist]|zho?(-(cn|hk|sg|tw))?)\.(ass|srt|ssa|smi|sub|idx|psb|vtt)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TagSplitRegex = new(@"[-_\s]", RegexOptions.Compiled);

    public static bool HasChineseSubtitle(BaseItem item)
    {
        if (item == null)
            return false;

        return HasEmbeddedChineseSubtitle(item.FileNameWithoutExtension) ||
               HasExternalChineseSubtitle(item.Path);
    }

    public static bool HasEmbeddedChineseSubtitle(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return false;

        // For STRM files preserving the source extension, FileNameWithoutExtension only
        // removes .strm. Normalize ABC-123-C.mp4 to ABC-123-C before reading tags.
        filename = RemovePreservedVideoExtension(filename);

        return filename.Contains(ChineseSubtitle) || HasTag(filename, "C", "UC", "ch");
    }

    public static string RemovePreservedVideoExtension(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return filename;

        var normalized = filename;

        // Accept both the value exposed by BaseItem.FileNameWithoutExtension
        // (ABC-123-C.mp4) and a complete nested STRM name (ABC-123-C.mp4.strm).
        if (Path.GetExtension(normalized).Equals(".strm", StringComparison.OrdinalIgnoreCase))
            normalized = Path.GetFileNameWithoutExtension(normalized);

        var extension = Path.GetExtension(normalized);
        return VideoExtensions.Contains(extension)
            ? Path.GetFileNameWithoutExtension(normalized)
            : normalized;
    }

    public static bool HasExternalChineseSubtitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var parent = Directory.GetParent(path);
            if (parent == null || !parent.Exists)
                return false;

            return HasExternalChineseSubtitle(Path.GetFileNameWithoutExtension(path),
                parent.GetFiles().Select(info => info.Name));
        }
        catch
        {
            return false;
        }
    }

    public static bool HasExternalChineseSubtitle(string basename, IEnumerable<string> files)
    {
        if (string.IsNullOrWhiteSpace(basename) || files == null)
            return false;

        var normalizedBasename = RemovePreservedVideoExtension(basename);

        return files.Any(name =>
        {
            if (!ExternalSubtitleRegex.IsMatch(name))
                return false;

            var subBasename = ExternalSubtitleRegex.Replace(name, string.Empty);
            var normalizedSubBasename = RemovePreservedVideoExtension(subBasename);

            return string.Equals(normalizedSubBasename, normalizedBasename, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(subBasename, basename, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool IsBadgedVersionOf(string currentImageUrl, string expectedUnbadgedUrl)
    {
        if (!TryCreateHttpUri(currentImageUrl, out var currentUri) ||
            !TryCreateHttpUri(expectedUnbadgedUrl, out var expectedUri))
            return false;

        if (!currentUri.Scheme.Equals(expectedUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !currentUri.Host.Equals(expectedUri.Host, StringComparison.OrdinalIgnoreCase) ||
            currentUri.Port != expectedUri.Port ||
            !currentUri.AbsolutePath.Equals(expectedUri.AbsolutePath, StringComparison.Ordinal))
            return false;

        var query = System.Web.HttpUtility.ParseQueryString(currentUri.Query);
        return !string.IsNullOrWhiteSpace(query.Get("badge"));
    }

    private static bool TryCreateHttpUri(string value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool HasTag(string filename, string tag)
    {
        return TagSplitRegex.Split(filename).Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasTag(string filename, params string[] tags)
    {
        return tags.Any(tag => HasTag(filename, tag));
    }
}
