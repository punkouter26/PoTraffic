using FluentAssertions;
using PoTraffic.Client.Infrastructure;

namespace PoTraffic.UnitTests.Infrastructure;

/// <summary>
/// Guards the display-side string helpers that normalize place names on the
/// dashboard and route detail. The contract: streets read with consistent
/// casing on screen regardless of how the address was first entered, while
/// house numbers, ZIPs, and 2-letter all-caps tokens (PO, NW) stay untouched.
/// </summary>
public sealed class TextFormattingTests
{
    [Theory]
    [InlineData("4451 telfair blvd", "4451 Telfair Blvd")]   // lower -> title
    [InlineData("4451 Telfair Blvd", "4451 Telfair Blvd")]   // idempotent
    [InlineData("TEST ORIGIN 4451", "Test Origin 4451")]     // upper -> title
    public void ToTitleCase_NormalisesMixedCasing(string input, string expected)
    {
        TextFormatting.ToTitleCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("PO BOX 1234", "PO Box 1234")]         // 2-letter acronym kept, word re-cased
    [InlineData("100 NW 42nd AVE", "100 NW 42nd Ave")] // 2-letter "NW" preserved; 3-letter "AVE" is not
    [InlineData("US 101", "US 101")]                   // acronym + number, untouched
    public void ToTitleCase_PreservesTwoLetterAcronyms(string input, string expected)
    {
        TextFormatting.ToTitleCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ToTitleCase_HandlesEmptyAndNull(string? input)
    {
        TextFormatting.ToTitleCase(input).Should().Be(string.Empty);
    }

}
