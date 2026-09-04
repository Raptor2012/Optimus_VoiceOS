namespace Optimus.Core.Tests;

using Optimus.Core;
using Xunit;

public class CorePlaceholderTests
{
    [Fact]
    public void CorePlaceholderOwnedByT018()
    {
        Assert.Equal("T018", CorePlaceholder.OwnedBy);
    }
}
