using CATRA.Services.Library;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Library;

/// <summary>
/// Unit tests for <see cref="FfprobeOutputParser"/> using captured ffprobe JSON
/// (<c>-print_format json -show_format -show_streams</c>) — no ffprobe binary
/// required, which is why the probe pipeline is testable in CI.
/// </summary>
public class FfprobeOutputParserTests
{
    private const string RealisticOutput = """
        {
          "streams": [
            {
              "index": 0,
              "codec_name": "h264",
              "codec_type": "video",
              "width": 1920,
              "height": 1080,
              "r_frame_rate": "24000/1001",
              "avg_frame_rate": "24000/1001"
            },
            {
              "index": 1,
              "codec_name": "aac",
              "codec_type": "audio",
              "sample_rate": "48000"
            }
          ],
          "format": {
            "filename": "episode.mp4",
            "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
            "duration": "1440.500000",
            "tags": { "title": "Episode 1" }
          }
        }
        """;

    [Fact]
    public void Parse_RealisticOutput_ExtractsDurationFpsResolutionAndTitle()
    {
        // Act
        var result = FfprobeOutputParser.Parse(RealisticOutput);

        // Assert
        result.Should().NotBeNull();
        result!.DurationSec.Should().Be(1440.5);
        result.Fps.Should().BeApproximately(24000d / 1001d, 0.0001);
        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
        result.ContainerTitle.Should().Be("Episode 1");
    }

    [Fact]
    public void Parse_UnknownAvgFrameRate_FallsBackToRFrameRate()
    {
        // Arrange — "0/0" is ffprobe's way of saying "unknown".
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "width": 640, "height": 480,
                  "avg_frame_rate": "0/0", "r_frame_rate": "30/1" }
              ],
              "format": { "duration": "10.0" }
            }
            """;

        // Act
        var result = FfprobeOutputParser.Parse(json);

        // Assert
        result.Should().NotBeNull();
        result!.Fps.Should().Be(30);
    }

    [Fact]
    public void Parse_AudioOnlyStream_LeavesVideoFieldsNull()
    {
        // Arrange
        const string json = """
            {
              "streams": [ { "codec_type": "audio", "sample_rate": "48000" } ],
              "format": { "duration": "95.25" }
            }
            """;

        // Act
        var result = FfprobeOutputParser.Parse(json);

        // Assert — duration still extracted; video metadata absent.
        result.Should().NotBeNull();
        result!.DurationSec.Should().Be(95.25);
        result.Fps.Should().BeNull();
        result.Width.Should().BeNull();
        result.Height.Should().BeNull();
        result.ContainerTitle.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void Parse_InvalidInput_ReturnsNull(string? json)
    {
        FfprobeOutputParser.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_NoUsableFields_ReturnsNull()
    {
        FfprobeOutputParser.Parse("""{ "format": { "filename": "x.mp4" } }""")
            .Should().BeNull();
    }
}
