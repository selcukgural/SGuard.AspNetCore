using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SGuard.AspNetCore.Tests;

/// <summary>
/// Starts a minimal API app on <see cref="TestServer"/> with <c>AddSGuardProblemDetails</c> and
/// <c>UseExceptionHandler</c>.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<TestApp> StartAsync(Action<IEndpointRouteBuilder> endpoints,
                                                 Action<SGuardProblemDetailsOptions>? configure = null,
                                                 Action<IServiceCollection>? services = null,
                                                 Action<IServiceCollection>? afterServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services);
        builder.Services.AddSGuardProblemDetails(configure);
        afterServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseExceptionHandler();
        endpoints(app);

        await app.StartAsync();
        return new TestApp(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// A response read back as a problem details document.
/// </summary>
internal sealed record Problem(int StatusCode, string? ContentType, string Body, JsonElement Json)
{
    public static async Task<Problem> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var json = body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
        return new Problem((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, json);
    }

    public Dictionary<string, string[]> Errors
        => Json.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
}

internal static class HttpClientExtensions
{
    public static async Task<Problem> GetProblemAsync(this HttpClient client, string path)
        => await Problem.ReadAsync(await client.GetAsync(path));

    public static async Task<Problem> PostProblemAsync<T>(this HttpClient client, string path, T body)
        => await Problem.ReadAsync(await client.PostAsJsonAsync(path, body));
}
