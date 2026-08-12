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
    [InlineData("4451 telfair blvd", "4451 Telfair Blvd")]
    [InlineData("4451 Telfair Blvd", "4451 Telfair Blvd")] // idempotent
    [InlineData("TEST ORIGIN 4451", "Test Origin 4451")]
    [InlineData("1 apple park way", "1 Apple Park Way")]
    [InlineData("1600 amphitheatre pkwy", "1600 Amphitheatre Pkwy")]
    public void ToTitleCase_NormalisesMixedCasing(string input, string expected)
    {
        TextFormatting.ToTitleCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("1600 AMPHITHEATRE PKWY", "1600 Amphitheatre Pkwy")]
    [InlineData("PO box 1234", "PO Box 1234")]
    [InlineData("PO BOX 1234", "PO Box 1234")]
    [InlineData("100 NW 42nd AVE", "100 NW 42nd Ave")] // 2-letter "NW" is preserved; 3-letter "AVE" is not
    [InlineData("US 101", "US 101")]
    [InlineData("SE Belmont St", "SE Belmont St")]
    public void ToTitleCase_PreservesTwoLetterAcronyms(string input, string expected)
    {
        TextFormatting.ToTitleCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ToTitleCase_HandlesEmptyAndNull(string? input)
    {
        TextFormatting.ToTitleCase(input).Should().Be(string.Empty);
    }

    [Fact]
    public void ToTitleCase_PreservesDigits()
    {
        // House numbers, ZIPs etc. must remain digit-led and verbatim — they
        // are not words and any case-shaping on them would be junk.
        TextFormatting.ToTitleCase("12345").Should().Be("12345");
        TextFormatting.ToTitleCase("4451 telfair").Should().Be("4451 Telfair");
    }
}
