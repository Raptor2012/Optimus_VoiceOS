namespace Optimus.Core.Tests.ExecutionPolicy;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.ExecutionPolicy;
using Xunit;

public sealed class AntigravityChatDriverTests
{
    [Fact]
    public void Driver_ResolvesAgyCliPath_Or_UsesCustomPath()
    {
        string defaultPath = AntigravityHeadlessChatDriver.ResolveAgyCliPath();
        Assert.False(string.IsNullOrWhiteSpace(defaultPath));
        Assert.EndsWith("agy.exe", defaultPath, StringComparison.OrdinalIgnoreCase);

        var customDriver = new AntigravityHeadlessChatDriver("C:\\tools\\custom-agy.exe");
        Assert.Equal("C:\\tools\\custom-agy.exe", customDriver.CliPath);
    }

    [Fact]
    public async Task Driver_ReturnsError_WhenCliDoesNotExist()
    {
        using var missingDriver = new AntigravityHeadlessChatDriver("C:\\nonexistent\\agy.exe");
        Assert.False(missingDriver.IsCliAvailable);

        var options = new AntigravityChatOptions("Test prompt");
        AntigravityChatResponse response = await missingDriver.ExecuteAsync(options);

        Assert.False(response.Success);
        Assert.Equal(-1, response.ExitCode);
        Assert.Contains("not found", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Driver_StopOutgoing_TerminatesWithoutThrowing()
    {
        using var driver = new AntigravityHeadlessChatDriver();

        // Calling StopOutgoing when no process is active must be safe and idempotent
        driver.StopOutgoing();
        driver.StopOutgoing();
    }

    [Fact]
    public async Task Driver_ThrowsOnInvalidOptions()
    {
        using var driver = new AntigravityHeadlessChatDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() => driver.ExecuteAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => driver.ExecuteAsync(new AntigravityChatOptions("")));
    }
}
