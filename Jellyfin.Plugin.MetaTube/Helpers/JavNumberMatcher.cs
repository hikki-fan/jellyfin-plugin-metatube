using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaTube.Helpers;

internal static class JavNumberMatcher
{
    private static readonly string[] MediaExtensions =
        { ".strm", ".mp4", ".mkv", ".avi", ".rmvb", ".wmv", ".mov", ".ts", ".m2ts" };

    private static readonly Regex WesternSceneRegex = new(
        @"^([A-Za-z0-9]*[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)*)\.(\d{2})\.(0[1-9]|1[0-2])\.(0[1-9]|[12]\d|3[01])(?:[-_](?:c|uc))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static List<T> ExactMatchesFirst<T>(
        IEnumerable<T> results,
        string query,
        Func<T, string> numberSelector)
    {
        var list = results.ToList();
        var queryKey = Normalize(query);
        if (queryKey == null)
            return list;

        return list.OrderBy(result =>
                string.Equals(Normalize(numberSelector(result)), queryKey, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
    }

    public static bool IsWesternSceneNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;

        return WesternSceneRegex.IsMatch(StripMediaExtensions(value.Trim()));
    }

    public static bool IsExactMatch(string query, string number)
    {
        var queryKey = Normalize(query);
        if (queryKey == null)
            return false;

        var numberKey = Normalize(number);
        if (numberKey == null)
            return false;

        return string.Equals(queryKey, numberKey, StringComparison.Ordinal);
    }

    public static T SelectMetadataResult<T>(
        string query,
        IReadOnlyList<T> results,
        Func<T, string> numberSelector)
    {
        if (results == null || results.Count == 0)
            return default;

        if (IsWesternSceneNumber(query))
        {
            return results.FirstOrDefault(result =>
                IsExactMatch(query, numberSelector(result)));
        }

        return results.FirstOrDefault();
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return null;

        var trimmed = value.Trim();
        var westernMatch = WesternSceneRegex.Match(StripMediaExtensions(trimmed));
        if (westernMatch.Success)
        {
            return $"{westernMatch.Groups[1].Value.ToUpperInvariant()}.{westernMatch.Groups[2].Value}.{westernMatch.Groups[3].Value}.{westernMatch.Groups[4].Value}";
        }

        var normalized = new StringBuilder(trimmed.Length);
        var hasLetter = false;
        var hasDigit = false;

        foreach (var character in trimmed)
        {
            if (char.IsAsciiLetter(character))
            {
                hasLetter = true;
                normalized.Append(char.ToUpperInvariant(character));
            }
            else if (char.IsAsciiDigit(character))
            {
                hasDigit = true;
                normalized.Append(character);
            }
            else if (character is not ('-' or '_' or ' '))
            {
                // Reject titles, paths, extensions and tags rather than guessing.
                return null;
            }
        }

        return hasLetter && hasDigit && normalized.Length >= 3
            ? normalized.ToString()
            : null;
    }

    private static string StripMediaExtensions(string value)
    {
        // A STRM item is commonly named Scene.mp4.strm. Strip known media
        // extensions only; a date component such as .09 is never an extension.
        for (var i = 0; i < 2; i++)
        {
            var extension = MediaExtensions.FirstOrDefault(candidate =>
                value.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (extension == null)
                break;
            value = value[..^extension.Length];
        }

        return value;
    }
}
