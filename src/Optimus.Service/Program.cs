namespace Optimus.Service;

using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

public static class Program
{
    public static int Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Kestrel configured to listen on 127.0.0.1 port 0 only.
        // Must never call UseUrls with a wildcard host and must never bind 0.0.0.0 or [::].
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();

        // Maps no endpoints. Device endpoint is owned by T004.

        if (args.Contains("--smoke"))
        {
            app.Start();

            var server = app.Services.GetRequiredService<IServer>();
            var addressesFeature = server.Features.Get<IServerAddressesFeature>();
            var address = addressesFeature?.Addresses.FirstOrDefault();

            int port = 0;
            if (!string.IsNullOrEmpty(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                port = uri.Port;
            }

            Console.WriteLine($"listening:{port}");

            app.StopAsync().GetAwaiter().GetResult();
            return 0;
        }

        app.Run();
        return 0;
    }
}
