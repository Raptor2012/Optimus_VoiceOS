namespace Optimus.Service.Tests;

using System;
using System.Linq;
using Optimus.Service;
using Xunit;

public class ScaffoldBoundaryTests
{
    [Fact]
    public void AssemblyContainsNoEndpointsHubsControllersOrMiddleware()
    {
        var types = typeof(Program).Assembly.GetTypes();
        var invalidTypes = types
            .Where(t => t.Name.EndsWith("Endpoint", StringComparison.Ordinal)
                     || t.Name.EndsWith("Hub", StringComparison.Ordinal)
                     || t.Name.EndsWith("Controller", StringComparison.Ordinal)
                     || t.Name.EndsWith("Middleware", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(invalidTypes);
    }
}
