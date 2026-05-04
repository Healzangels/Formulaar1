using Formulaar1;
using Xunit;

namespace Formulaar1.Tests;

public class DetectSeriesTests
{
    [Theory]
    [InlineData("Formula1 2026 Round04 USA Miami Race", "Formula 1", 387219)]
    [InlineData("Formula 1 2026 Round04 USA Miami Race", "Formula 1", 387219)]
    [InlineData("Formula 1 2025x20 Mexico Race", "Formula 1", 387219)]
    [InlineData("Formula1 S2026E17 Round02 China Sprint", "Formula 1", 387219)]
    [InlineData("Formula 2 2025 Round01 Bahrain Sprint Race", "Formula 2", 392717)]
    [InlineData("Formula2 2025 Round01 Bahrain Feature Race", "Formula 2", 392717)]
    [InlineData("Formula 3 2025 Round01 Bahrain Qualifying", "Formula 3", 396724)]
    [InlineData("Formula3 2025 Round01 Bahrain Practice", "Formula 3", 396724)]
    public void DetectSeries_ReturnsCorrectInfo(string normalisedTitle, string expectedTitle, int expectedTvdbId)
    {
        var result = Helpers.DetectSeries(normalisedTitle);
        Assert.NotNull(result);
        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(expectedTvdbId, result.TvdbId);
    }

    [Fact]
    public void DetectSeries_ReturnsNull_ForUnknownTitle()
    {
        Assert.Null(Helpers.DetectSeries("MotoGP 2025 Round01 Qatar Race"));
    }

    [Fact]
    public void DetectSeries_PrefersF2_OverF1_WhenBothMatch()
    {
        var result = Helpers.DetectSeries("Formula 2 2025 Round01");
        Assert.NotNull(result);
        Assert.Equal("Formula 2", result.Title);
    }
}

public class NormaliseShowTypeTests
{
    [Theory]
    [InlineData("Formula1 2026 Round04 USA Miami Race", "Race")]
    [InlineData("Formula1 2026 Round04 Qatar Qualifying", "Qualifying")]
    [InlineData("Formula1 2026 Round04 Qatar Qually", "Qualifying")]
    [InlineData("Formula1 2026 Round04 Qatar Sprint", "Sprint")]
    [InlineData("Formula1 2026 Round04 Qatar Sprint Race", "Sprint Race")]
    [InlineData("Formula1 2026 Round04 Qatar Sprint Qualifying", "Sprint Shootout")]
    [InlineData("Formula1 2026 Round04 Qatar Sprint Shootout", "Sprint Shootout")]
    [InlineData("Formula1 2026 Round04 Qatar Shootout", "Sprint Shootout")]
    [InlineData("Formula1 2026 Round04 Qatar Feature Race", "Feature Race")]
    [InlineData("Formula1 2026 Round04 Qatar Practice 1", "Practice 1")]
    [InlineData("Formula1 2026 Round04 Qatar Practice 2", "Practice 2")]
    [InlineData("Formula1 2026 Round04 Qatar Practice 3", "Practice 3")]
    [InlineData("Formula1 2026 Round04 Qatar Practice One", "Practice 1")]
    [InlineData("Formula1 2026 Round04 Qatar FP1", "Practice 1")]
    [InlineData("Formula1 2026 Round04 Qatar FP2", "Practice 2")]
    [InlineData("Formula1 2026 Round04 Qatar FP3", "Practice 3")]
    public void NormaliseShowType_ReturnsExpected(string normalisedTitle, string expected)
    {
        var result = Helpers.NormaliseShowType(normalisedTitle);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Formula1 2025 Round22 USA Las Vegas Pre Qualifying Show")]
    public void NormaliseShowType_ReturnsNull_ForDroppedSessions(string normalisedTitle)
    {
        Assert.Null(Helpers.NormaliseShowType(normalisedTitle));
    }

    [Theory]
    [InlineData("Formula1 2026 China Grand Prix Sprint Race", "Sprint Race")]
    [InlineData("Formula1 2026 China Grand Prix Race", "Race")]
    [InlineData("Formula1 2026 China Grand Prix Sprint Qualifying", "Sprint Shootout")]
    public void NormaliseShowType_HandlesGrandPrixInTitle(string normalisedTitle, string expected)
    {
        var result = Helpers.NormaliseShowType(normalisedTitle);
        Assert.Equal(expected, result);
    }
}

public class CountriesTests
{
    [Theory]
    [InlineData("Qatar")]
    [InlineData("USA")]
    [InlineData("COTA")]
    [InlineData("Austin")]
    [InlineData("LasVegas")]
    [InlineData("Las Vegas")]
    [InlineData("AbuDhabi")]
    [InlineData("Abu Dhabi")]
    [InlineData("UAE")]
    [InlineData("UnitedArabEmirates")]
    [InlineData("Imola")]
    [InlineData("British")]
    [InlineData("Dutch")]
    [InlineData("Italian")]
    [InlineData("Belgian")]
    public void Countries_ContainsExpectedKey(string key)
    {
        Assert.True(Helpers.Countries.ContainsKey(key), $"Missing country key: {key}");
    }
}
