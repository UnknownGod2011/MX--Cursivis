#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.Text;

public static class FuzzyMatcher
{
    public static double Score(string query, string candidate)
    {
        var left = Normalize(query);
        var right = Normalize(candidate);
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (left == right)
            return 1;
        if (right.StartsWith(left + " ", StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal))
            return 0.95;
        if (right.Contains(left, StringComparison.Ordinal))
            return Math.Clamp(0.9 - Math.Max(0, right.Length - left.Length) * 0.003, 0.78, 0.9);

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokenScores = leftTokens
            .Select(token => rightTokens.Max(candidateToken => Similarity(token, candidateToken)))
            .ToArray();
        var tokenAverage = tokenScores.Length == 0 ? 0 : tokenScores.Average();
        var tokenCoverage = leftTokens.Count(token => rightTokens.Contains(token, StringComparer.Ordinal))
            / (double)Math.Max(1, leftTokens.Length);
        var whole = Similarity(left, right);
        var acronym = Acronym(rightTokens);
        var acronymScore = acronym.Length > 1 ? Similarity(left.Replace(" ", ""), acronym) : 0;

        return Math.Clamp(
            whole * 0.38
            + tokenAverage * 0.42
            + tokenCoverage * 0.12
            + acronymScore * 0.08,
            0,
            0.94);
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        var previousSpace = true;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                builder.Append(' ');
                previousSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static double Similarity(string left, string right)
    {
        if (left == right)
            return 1;
        var distance = LevenshteinDistance(left, right);
        return 1 - distance / (double)Math.Max(left.Length, right.Length);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string Acronym(IEnumerable<string> tokens) =>
        new(tokens.Where(token => token.Length > 0).Select(token => token[0]).ToArray());
}

