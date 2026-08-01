using FluentAssertions;

using PoTraffic.Client.Infrastructure;

namespace PoTraffic.UnitTests.Infrastructure;

/// <summary>Ranking rules behind the command palette's route and command search.</summary>
public sealed class FuzzyMatchTests
{
    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        // The palette lists all commands before anything is typed.
        FuzzyMatch.Score("Add route", string.Empty).Should().Be(0);
    }

    [Fact]
    public void NonSubsequence_DoesNotMatch()
    {
        FuzzyMatch.Score("Dashboard", "xyz").Should().BeNull();
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        FuzzyMatch.Score("Dashboard", "DASH").Should().NotBeNull();
        FuzzyMatch.Score("Dashboard", "dash").Should().NotBeNull();
    }

    [Fact]
    public void OutOfOrderCharacters_DoNotMatch()
    {
        // Subsequence, not bag-of-characters: "hsad" is not in order within "Dashboard".
        FuzzyMatch.Score("Dashboard", "hsad").Should().BeNull();
    }

    [Fact]
    public void WordInitials_OutrankScatteredLetters()
    {
        int? initials = FuzzyMatch.Score("Home → Work", "hw");
        int? scattered = FuzzyMatch.Score("Cheshunt Highway", "hw");

        initials.Should().NotBeNull();
        scattered.Should().NotBeNull();
        initials!.Value.Should().BeGreaterThan(scattered!.Value);
    }

    [Fact]
    public void ConsecutiveRun_OutranksTheSameLettersSpreadOut()
    {
        int? run = FuzzyMatch.Score("settings", "sett");
        int? spread = FuzzyMatch.Score("select the tab", "sett");

        run.Should().NotBeNull();
        spread.Should().NotBeNull();
        run!.Value.Should().BeGreaterThan(spread!.Value);
    }

    [Fact]
    public void ShorterCandidate_WinsWhenBothMatchEquallyWell()
    {
        int? shortName = FuzzyMatch.Score("Dashboard", "dash");
        int? longName = FuzzyMatch.Score("Dashboard for every saved route", "dash");

        shortName!.Value.Should().BeGreaterThan(longName!.Value);
    }

    [Fact]
    public void WhitespaceInQuery_IsIgnored()
    {
        FuzzyMatch.Score("Check now: Home → Work", "ch now").Should().NotBeNull();
    }

    [Fact]
    public void EmptyCandidate_DoesNotMatchARealQuery()
    {
        FuzzyMatch.Score(string.Empty, "a").Should().BeNull();
    }
}
