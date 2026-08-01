namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Subsequence matcher behind the command palette. Runs over the handful of commands and
/// routes already in memory, so it costs nothing per keystroke and needs no index.
///
/// <para>
/// Scoring rewards matches that land where a human would look: at the start of the string
/// and at word boundaries, and in unbroken runs. That is what makes "hw" find
/// "Home → Work" ahead of a route that merely contains an h and a w somewhere.
/// </para>
/// </summary>
public static class FuzzyMatch
{
    private const int ConsecutiveBonus = 8;
    private const int WordStartBonus = 10;
    private const int StringStartBonus = 12;
    private const int GapPenalty = 1;

    /// <summary>
    /// Score for <paramref name="query"/> against <paramref name="candidate"/>, or null when
    /// the query is not a subsequence of the candidate. Higher is better. An empty query
    /// matches everything with a neutral score, so the palette lists all commands on open.
    /// </summary>
    public static int? Score(string candidate, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 0;
        if (string.IsNullOrEmpty(candidate))
            return null;

        int score = 0;
        int candidateIndex = 0;
        int lastMatch = -2;

        foreach (char q in query)
        {
            if (char.IsWhiteSpace(q))
                continue;

            int found = IndexOfIgnoreCase(candidate, q, candidateIndex);
            if (found < 0)
                return null;

            if (found == 0)
                score += StringStartBonus;
            else if (!char.IsLetterOrDigit(candidate[found - 1]))
                score += WordStartBonus;

            if (found == lastMatch + 1)
                score += ConsecutiveBonus;
            else
                score -= Math.Min(found - lastMatch - 1, 10) * GapPenalty;

            lastMatch = found;
            candidateIndex = found + 1;
        }

        // Shorter candidates that satisfy the same query are the better match.
        return score - (candidate.Length / 12);
    }

    private static int IndexOfIgnoreCase(string haystack, char needle, int startIndex)
    {
        for (int i = startIndex; i < haystack.Length; i++)
        {
            if (char.ToLowerInvariant(haystack[i]) == char.ToLowerInvariant(needle))
                return i;
        }
        return -1;
    }
}
