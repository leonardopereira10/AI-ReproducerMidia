using CATRA.Core.Enums;
using CATRA.Services.Library;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Library;

/// <summary>
/// RF-02: the four filename patterns with priority P1 &gt; P2 &gt; P3 &gt; fallback,
/// validated against the real examples from the spec.
/// </summary>
public class FilenameParserTests
{
    private readonly FilenameParser _parser = new();

    // ── P1 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_P1SpecExample_ExtractsNormalizedNameAndEpisode()
    {
        // Arrange
        var fileName = "[AniDong][A Record of a Mortal_s Journey] - Episódio 26.mp4";

        // Act
        var result = _parser.Parse(fileName);

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern1);
        result.SeriesName.Should().Be("A Record of a Mortal's Journey");
        result.EpisodeNumber.Should().Be(26);
        result.SeasonNumber.Should().BeNull();
    }

    [Fact]
    public void Parse_P1WithoutExtension_MatchesTheSameWay()
    {
        // Act
        var result = _parser.Parse("[AniDong][Some Series] - Episódio 3");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern1);
        result.SeriesName.Should().Be("Some Series");
        result.EpisodeNumber.Should().Be(3);
    }

    [Theory]
    [InlineData("Episódio 12")] // ó (accented, as downloaded)
    [InlineData("Episodio 12")] // o (common typo variant)
    public void Parse_P1AccentVariants_BothMatch(string episodePart)
    {
        // Act
        var result = _parser.Parse($"[Site][Name X] - {episodePart}.mkv");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern1);
        result.EpisodeNumber.Should().Be(12);
    }

    // ── P2 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_P2SpecExample_ExtractsNameEpisodeAndIgnoresQualityTag()
    {
        // Arrange
        var fileName =
            "[AnimeFire.io] Saikyou Degarashi Ouji no Anyaku Teii Arasoi - Episódio 4 (HD).mp4";

        // Act
        var result = _parser.Parse(fileName);

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern2);
        result.SeriesName.Should().Be("Saikyou Degarashi Ouji no Anyaku Teii Arasoi");
        result.EpisodeNumber.Should().Be(4);
        result.SeasonNumber.Should().BeNull();
    }

    // ── P3 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_P3SpecExample_ExtractsSeasonAndEpisode()
    {
        // Act
        var result = _parser.Parse("ACSADRGT01EP07.mp4");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern3);
        result.SeasonNumber.Should().Be(1);
        result.EpisodeNumber.Should().Be(7);
        result.SeriesName.Should().BeNull();
        result.RawName.Should().Be("ACSADRGT01EP07");
    }

    [Fact]
    public void Parse_P3HigherNumbers_ParsesBothGroups()
    {
        // Act
        var result = _parser.Parse("PREFIX12EP35.avi");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern3);
        result.SeasonNumber.Should().Be(12);
        result.EpisodeNumber.Should().Be(35);
    }

    // ── P4 fallback ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_P4SpecExample_FallsBackWithRawName()
    {
        // Act
        var result = _parser.Parse("SUACLPLNDRS.mp4");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Fallback);
        result.RawName.Should().Be("SUACLPLNDRS");
        result.SeriesName.Should().BeNull();
        result.SeasonNumber.Should().BeNull();
        result.EpisodeNumber.Should().BeNull();
    }

    [Fact]
    public void Parse_RandomName_FallsBack()
    {
        // Act
        var result = _parser.Parse("movie final cut.mkv");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Fallback);
        result.RawName.Should().Be("movie final cut");
    }

    // ── priority + input handling ───────────────────────────────────────────

    [Fact]
    public void Parse_P1Input_WinsOverP2()
    {
        // P2 could also match (capturing the inner brackets); P1 must win.
        // Act
        var result = _parser.Parse("[AniDong][A Record of a Mortal_s Journey] - Episódio 26.mp4");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern1);
        result.SeriesName.Should().NotContain("[");
        result.SeriesName.Should().NotContain("]");
    }

    [Fact]
    public void Parse_InputWithPath_UsesOnlyTheFileName()
    {
        // Act
        var result = _parser.Parse(@"C:\Media\Animes\Serie\[AniDong][Name] - Episódio 9.mp4");

        // Assert
        result.PatternUsed.Should().Be(FilenamePattern.Pattern1);
        result.EpisodeNumber.Should().Be(9);
        result.RawName.Should().Be("[AniDong][Name] - Episódio 9");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_Throws(string? fileName)
    {
        // Act
        var act = () => _parser.Parse(fileName!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
