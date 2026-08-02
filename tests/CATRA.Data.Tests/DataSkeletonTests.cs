using CATRA.Data.Repositories;
using FluentAssertions;
using Xunit;

namespace CATRA.Data.Tests;

/// <summary>
/// Placeholder tests validating the Data skeleton.
/// </summary>
public class DataSkeletonTests
{
    private sealed class FakeEntity
    {
    }

    private sealed class FakeRepository : IRepository<FakeEntity>
    {
    }

    [Fact]
    public void IRepository_ShouldBeImplementable()
    {
        var repository = new FakeRepository();

        repository.Should().BeAssignableTo<IRepository<FakeEntity>>();
    }
}
