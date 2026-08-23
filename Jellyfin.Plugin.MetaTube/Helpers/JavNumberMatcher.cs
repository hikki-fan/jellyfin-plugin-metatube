using System.Text;

namespace Jellyfin.Plugin.MetaTube.Helpers;

internal static class JavNumberMatcher
{
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

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return null;

        var normalized = new StringBuilder(value.Length);
        var hasLetter = false;
        var hasDigit = false;

        foreach (var character in value.Trim())
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
}
