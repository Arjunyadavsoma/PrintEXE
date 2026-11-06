namespace PrintShopCompanion.Backend;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrintShopCompanion.Backend.Data;
using PrintShopCompanion.Backend.Endpoints;

/// <summary>
/// Minimal mock backend for local testing. Implements just enough of the
/// PrintShop companion API for the PrintShop Companion app to pair, send
/// heartbeats, queue a test job, download a sample PDF, and report status.
///
/// <para>Run:</para>
/// <code>dotnet run --project src\PrintShopCompanion.Backend\PrintShopCompanion.Backend.csproj</code>
/// <para>The companion's appsettings.json should set <c>Backend:BaseUrl</c> to
/// <c>https://localhost:5241</c> and <c>Backend:BypassTlsVerification</c> to true
/// for local testing only.</para>
/// </summary>
public static class Program
{
    // CA1861: hoisted to a static readonly field so the array is allocated once
    // rather than on every request to the root endpoint.
    private static readonly string[] EndpointList =
    {
        "/pair", "/heartbeat", "/jobs/pending",
        "/jobs/{id}/ack", "/jobs/{id}/status", "/jobs/{id}/pdf"
    };

    private static readonly string[] CorsOrigins =
    {
        "https://app.printshop.local",
        "http://localhost:5173"
    };

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<MockStore>();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins(CorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()));
        builder.Services.AddHostedService<JobGeneratorService>();

        var app = builder.Build();
        app.UseCors();
        app.MapPairingEndpoints();
        app.MapDeviceEndpoints();
        app.MapJobEndpoints();
        app.MapGet("/", () => Results.Ok(new
        {
            name = "PrintShop Companion Mock Backend",
            version = "0.1.0-mock",
            endpoints = EndpointList
        }));
        app.Run();
    }
}
