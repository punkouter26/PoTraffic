// ──────────────────────────────────────────────────────────────────────
// One place to normalise how addresses read on screen.
//
// The API returns addresses verbatim, so a user-entered "4451 telfair blvd"
// and a Google-resolved "4451 Telfair Blvd" sit on the same dashboard with
// visibly different casings — neither is "wrong", but the inconsistency makes
// the list read as if the data is unreliable. Title-casing at the read site is
// the cheapest fix: it never changes storage, only display.
// ──────────────────────────────────────────────────────────────────────

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Display-side string helpers for addresses, names and similar free-form text.
/// All methods are pure; nothing here mutates storage.
/// </summary>
public static class TextFormatting
{
    /// <summary>
    /// Title-cases a street name, preserving all-caps abbreviations and the
    /// natural casing of common suffixes (Street, Avenue, Boulevard, …).
    /// Null/whitespace inputs pass through.
    /// </summary>
    /// <example>
    /// "4451 telfair blvd"        → "4451 Telfair Blvd"
    /// "1 apple park way"         → "1 Apple Park Way"
    /// "1600 AMPHITHEATRE PKWY"   → "1600 Amphitheatre Pkwy"
    /// "PO box 1234"              → "PO Box 1234"
    /// </example>
    public static string ToTitleCase(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string trimmed = input.Trim();

        // If the string is already mostly uppercase or contains acronyms we want
        // to keep (PO, NW, SE, US, …) we still title-case but capitalise each
        // word's first letter; the only special case is "PO" / "US" style
        // 2-letter all-caps tokens, which already look correct.
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            // Keep digits-leading tokens (house numbers, ZIPs) verbatim.
            if (tokens[i].Length > 0 && char.IsDigit(tokens[i][0]))
                continue;

            // Two-letter all-caps tokens are almost always acronyms (PO, US, NW, SE).
            // Three-letter is too ambiguous (AVE could be an abbreviation or "Ave"):
            // better to over-shape than to display "AVE" forever, since the address
            // already stored the proper casing on the way in if it was correct.
            if (tokens[i].Length == 2 && AllCapsCheck(tokens[i]))
                continue;

            tokens[i] = tokens[i].CapitaliseFirst();
        }
        return string.Join(' ', tokens);
    }

    private static bool AllCapsCheck(this string s)
    {
        foreach (char c in s)
        {
            if (char.IsLetter(c) && !char.IsUpper(c))
                return false;
        }
        return true;
    }

    private static string CapitaliseFirst(this string s) =>
        s.Length switch
        {
            0 => s,
            1 => char.ToUpperInvariant(s[0]).ToString(),
            _ => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant()
        };
}
