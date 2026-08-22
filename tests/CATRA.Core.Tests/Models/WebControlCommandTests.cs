using System.Text.Json;
using CATRA.Core.Models;
using FluentAssertions;
using Xunit;

namespace CATRA.Core.Tests.Models;

/// <summary>
/// Unit tests for <see cref="WebControlCommand.FromJson"/> (ST-10): one parse
/// test per command type, optional-field handling, case-insensitive property
/// matching and invalid-payload rejection.
/// </summary>
public sealed class WebControlCommandTests
{
    [Fact]
    public void FromJson_Play_ParsesType()
    {
        // Arrange
        var json = """{"type":"play"}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().Be("play");
        command.Position.Should().BeNull();
        command.Level.Should().BeNull();
    }

    [Fact]
    public void FromJson_Pause_ParsesType()
    {
        // Arrange
        var json = """{"type":"pause"}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().Be("pause");
    }

    [Theory]
    [InlineData("skipIntro")]
    [InlineData("nextEpisode")]
    [InlineData("previousEpisode")]
    public void FromJson_SimpleCommands_ParseType(string type)
    {
        // Arrange
        var json = $$"""{"type":"{{type}}"}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().Be(type);
    }

    [Fact]
    public void FromJson_Seek_ParsesPosition()
    {
        // Arrange
        var json = """{"type":"seek","position":123.5}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().Be("seek");
        command.Position.Should().Be(123.5);
        command.Level.Should().BeNull();
    }

    [Fact]
    public void FromJson_Volume_ParsesLevel()
    {
        // Arrange
        var json = """{"type":"volume","level":42}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().Be("volume");
        command.Level.Should().Be(42);
        command.Position.Should().BeNull();
    }

    [Fact]
    public void FromJson_OptionalFieldsAbsent_AreNull()
    {
        // Arrange — payload with only the required field.
        var json = """{"type":"play"}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Position.Should().BeNull();
        command.Level.Should().BeNull();
    }

    [Fact]
    public void FromJson_PropertyNamesCaseInsensitive()
    {
        // Arrange — uppercase property names (browser clients vary).
        var json = """{"Type":"seek","Position":10}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().Be("seek");
        command.Position.Should().Be(10);
    }

    [Fact]
    public void FromJson_InvalidJson_ThrowsJsonException()
    {
        // Arrange
        var json = "this is not json";

        // Act
        var act = () => WebControlCommand.FromJson(json);

        // Assert
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void FromJson_NullLiteral_ThrowsJsonException()
    {
        // Arrange — a JSON null deserializes to null and must be rejected.
        var json = "null";

        // Act
        var act = () => WebControlCommand.FromJson(json);

        // Assert
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void FromJson_MissingType_DoesNotThrow_TypeIsNull()
    {
        // Arrange — required constructor parameter 'Type' absent. STJ does not
        // enforce this at runtime; the service-side switch no-ops on null Type.
        var json = """{"position":10}""";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().BeNull();
    }

    [Fact]
    public void FromJson_EmptyObject_DoesNotThrow_TypeIsNull()
    {
        // Arrange
        var json = "{}";

        // Act
        var command = WebControlCommand.FromJson(json);

        // Assert
        command.Type.Should().BeNull();
        command.Position.Should().BeNull();
        command.Level.Should().BeNull();
    }
}
