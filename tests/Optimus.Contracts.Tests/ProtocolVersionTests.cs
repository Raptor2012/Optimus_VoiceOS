namespace Optimus.Contracts.Tests;

using Optimus.Contracts;
using Xunit;

public class ProtocolVersionTests
{
    [Fact]
    public void VersionMatchesExpectedFormat()
    {
        Assert.Equal(ProtocolVersion.Current, $"{ProtocolVersion.Major}.{ProtocolVersion.Minor}");
        Assert.Equal("optimus.v1", ProtocolVersion.SubProtocol);
    }
}
