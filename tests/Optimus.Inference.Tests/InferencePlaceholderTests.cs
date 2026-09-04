namespace Optimus.Inference.Tests;

using Optimus.Inference;
using Xunit;

public class InferencePlaceholderTests
{
    [Fact]
    public void InferencePlaceholderOwnedByT007()
    {
        Assert.Equal("T007", InferencePlaceholder.OwnedBy);
    }
}
