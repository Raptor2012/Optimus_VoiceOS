namespace Optimus.Providers.Tests;

using Optimus.Providers;
using Xunit;

public class ProvidersPlaceholderTests
{
    [Fact]
    public void ProvidersPlaceholderOwnedByT016()
    {
        Assert.Equal("T016", ProvidersPlaceholder.OwnedBy);
    }
}
