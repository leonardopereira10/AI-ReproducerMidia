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
        // Full behavior is covered by LibraryServiceTests; the facade now
        // requires repository/scanner dependencies, so assert the contract.
        typeof(LibraryService).Should().Implement<ILibraryService>();
    }
}
