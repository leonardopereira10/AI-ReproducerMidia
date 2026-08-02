using CATRA.Core.Library;
using CATRA.Core.Navigation;
using FluentAssertions;
using Xunit;

namespace CATRA.Core.Tests;

/// <summary>
/// Placeholder tests validating the Core skeleton contracts.
/// </summary>
public class CoreSkeletonTests
{
    [Fact]
    public void INavigationService_ShouldBeAnInterface()
        => typeof(INavigationService).IsInterface.Should().BeTrue();

    [Fact]
    public void ILibraryService_ShouldBeAnInterface()
        => typeof(ILibraryService).IsInterface.Should().BeTrue();
}
