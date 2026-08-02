using CATRA.Core.Library;
using CATRA.Services.Library;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests;

/// <summary>
/// Placeholder tests validating the Services skeleton.
/// </summary>
public class ServicesSkeletonTests
{
    [Fact]
    public void LibraryService_ShouldImplementILibraryService()
    {
        var service = new LibraryService();

        service.Should().BeAssignableTo<ILibraryService>();
    }
}
