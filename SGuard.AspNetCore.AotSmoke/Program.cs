using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SGuard;

// Starts the app on a free port, sends requests that fail guards, and checks the responses. Exits with 1 on a mismatch.
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, SmokeJsonContext.Default));
builder.Services.AddSGuardProblemDetails(o => o.Handle<OrderClosedException>());

var app = builder.Build();
app.UseExceptionHandler();
app.MapPost("/orders", (Order order) =>
{
    ThrowIf.NullOrEmpty(order.Name);
    ThrowIf.OutOfRange(order.Quantity, 1, 100);
    ThrowIf.NullOrEmpty(order.City);
    return Results.Ok();
});
app.MapGet("/search", (string? q) => Results.Ok(ThrowIf.NullOrWhiteSpace(q)));
app.MapGet("/closed", IResult () => throw new OrderClosedException());
app.MapGet("/bug", IResult () => throw new InvalidOperationException("A bug."));

await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
var failures = 0;

await Check("POST", "/orders", """{"name":"","quantity":1,"city":"Ankara"}""", 400, "Name", "A value is required.");
await Check("POST", "/orders", """{"name":"Book","quantity":500,"city":"Ankara"}""", 400, "Quantity", "The value is outside the allowed range.");
await Check("POST", "/orders", """{"name":"Book","quantity":1,"city":null}""", 400, "City", "A value is required.");
await Check("GET", "/search", null, 400, "q", "A value is required.");
await Check("GET", "/closed", null, 400, "", "The value isn't valid.");
await Check("GET", "/bug", null, 500, null, null);
await Check("POST", "/orders", """{"name":"Book","quantity":1,"city":"Ankara"}""", 200, null, null);

await app.StopAsync();
Console.WriteLine(failures == 0 ? "SGuard.AspNetCore AOT smoke test passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

async Task Check(string method, string path, string? body, int status, string? key, string? message)
{
    using var request = new HttpRequestMessage(new HttpMethod(method), path);
    if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    var ok = (int)response.StatusCode == status;

    if (ok && key is not null)
    {
        using var json = JsonDocument.Parse(text);
        ok = json.RootElement.GetProperty("errors").TryGetProperty(key, out var messages) &&
             messages[0].GetString() == message &&
             response.Content.Headers.ContentType?.MediaType == "application/problem+json";
    }

    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {method} {path} -> {(int)response.StatusCode} {text}");
    if (!ok) failures++;
}

internal sealed record Order(string? Name, int Quantity, string? City);

internal sealed class OrderClosedException() : Exception("The order is closed.");

[JsonSerializable(typeof(Order))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
