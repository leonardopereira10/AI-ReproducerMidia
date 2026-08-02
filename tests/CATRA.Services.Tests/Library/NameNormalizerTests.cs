using CATRA.Services.Library;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Library;

/// <summary>
/// RN-06: possessive underscore, [Site] tag removal, title case, raw preserved
/// elsewhere by the scanner (not this class).
/// </summary>
public class NameNormalizerTests
{
    // ── possessive underscore ───────────────────────────────────────────────

    [Fact]
    public void Normalize_Underscore_BecomesPossessiveApostrophe()
    {
        NameNormalizer.Normalize("Mortal_s").Should().Be("Mortal's");
    }

    [Fact]
    public void Normalize_ApostropheWord_NeverCapitalizesAfterApostrophe()
    {
        NameNormalizer.Normalize("a record of a mortal_s journey")
            .Should().Be("A Record of a Mortal's Journey");
    }

    // ── tag removal ─────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_SiteTagPrefix_IsRemoved()
    {
        NameNormalizer.Normalize("[AnimeFire.io] attack on titan")
            .Should().Be("Attack on Titan");
    }

    [Fact]
    public void Normalize_MultipleTags_AllRemoved()
    {
        NameNormalizer.Normalize("[AniDong] [1080p] fullmetal alchemist")
            .Should().Be("Fullmetal Alchemist");
    }

    [Fact]
    public void Normalize_OnlyTags_ReturnsEmpty()
    {
        NameNormalizer.Normalize("[AniDong][1080p]").Should().BeEmpty();
    }

    // ── title case ──────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_SmallWords_StayLowercaseInsideTheTitle()
    {
        NameNormalizer.Normalize("A Record of a Mortal_s Journey")
            .Should().Be("A Record of a Mortal's Journey");
    }

    [Fact]
    public void Normalize_JapaneseParticleNo_StaysLowercase()
    {
        NameNormalizer.Normalize("SAIKYOU DEGARASHI OUJI NO ANYAKU TEII ARASOI")
            .Should().Be("Saikyou Degarashi Ouji no Anyaku Teii Arasoi");
    }

    [Fact]
    public void ToTitleCase_AllUppercaseWord_IsPreservedAsAcronym()
    {
        NameNormalizer.ToTitleCase("SUACLPLNDRS").Should().Be("SUACLPLNDRS");
    }

    [Fact]
    public void Normalize_Whitespace_IsCollapsedAndTrimmed()
    {
        NameNormalizer.Normalize("   multiple   spaces   here  ")
            .Should().Be("Multiple Spaces Here");
    }

    // ── null / empty handling ───────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrBlank_ReturnsEmpty(string? input)
    {
        NameNormalizer.Normalize(input).Should().BeEmpty();
    }

    [Fact]
    public void ToTitleCase_NullOrBlank_ReturnsEmpty()
    {
        NameNormalizer.ToTitleCase(null).Should().BeEmpty();
        NameNormalizer.ToTitleCase("  ").Should().BeEmpty();
    }
}
