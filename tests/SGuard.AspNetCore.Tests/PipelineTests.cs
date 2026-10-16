using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SGuard.AspNetCore.Tests;

public sealed record SignUp(string? Email, string? Password);

[ApiController]
[Route("signups")]
public sealed class SignUpController : ControllerBase
{
    [HttpPost]
    public IActionResult Create(SignUp signUp)
    {
        ThrowIf.NullOrEmpty(signUp.Email);
        ThrowIf.ShorterThan(signUp.Password!, 12);
        return Ok();
    }
}

internal sealed class CatchAllHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = 503;
        await httpContext.Response.WriteAsync("catch-all", cancellationToken);
        return true;
    }
}

/// <summary>
/// How the handler fits in a real pipeline: controllers, other exception handlers, content negotiation and logging.
/// </summary>
[Collection(nameof(SGuardOptionsCollection))]
public sealed class PipelineTests
{
    private static void MapSearch(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)
        => app.MapGet("/search", (string? q) => Results.Ok(ThrowIf.NullOrWhiteSpace(q)));

    [Fact]
    public async Task Controllers_GetTheSameResponse()
    {
        await using var app = await TestApp.StartAsync(
            a => a.MapControllers(),
            services: s => s.AddControllers().AddApplicationPart(typeof(SignUpController).Assembly));

        var problem = await app.Client.PostProblemAsync("/signups", new SignUp("", "a long enough password"));

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(["A value is required."], problem.Errors["Email"]);
    }

    [Fact]
    public async Task NullValue_ForAGuardThatNeedsOne_IsNotABadRequest()
    {
        // ShorterThan throws the BCL ArgumentNullException for null: the guard was called with nothing to check.
        await using var app = await TestApp.StartAsync(
            a => a.MapControllers(),
            services: s => s.AddControllers().AddApplicationPart(typeof(SignUpController).Assembly));

        var response = await app.Client.PostAsJsonAsync("/signups", new SignUp("a@b.c", null));

        Assert.Equal(500, (int)response.StatusCode);
    }

    [Fact]
    public async Task BrowserAcceptHeader_GetsTheJson()
    {
        await using var app = await TestApp.StartAsync(MapSearch);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var problem = await Problem.ReadAsync(await app.Client.SendAsync(request));

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(["A value is required."], problem.Errors["q"]);
    }

    [Fact]
    public async Task OnlyXml_GetsTheStatusCodeWithoutABody()
    {
        await using var app = await TestApp.StartAsync(MapSearch);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        var problem = await Problem.ReadAsync(await app.Client.SendAsync(request));

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal("", problem.Body);
    }

    [Fact]
    public async Task HandlerRegisteredAfter_GetsTheRest()
    {
        await using var app = await TestApp.StartAsync(
            a =>
            {
                MapSearch(a);
                a.MapGet("/bug", IResult () => throw new InvalidOperationException());
            },
            afterServices: s => s.AddExceptionHandler<CatchAllHandler>());

        var guard = await app.Client.GetProblemAsync("/search");
        var bug = await app.Client.GetAsync("/bug");

        Assert.Equal(400, guard.StatusCode);
        Assert.Equal(503, (int)bug.StatusCode);
        Assert.Equal("catch-all", await bug.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HandlerRegisteredBefore_Wins()
    {
        await using var app = await TestApp.StartAsync(MapSearch, services: s => s.AddExceptionHandler<CatchAllHandler>());

        var response = await app.Client.GetAsync("/search");

        Assert.Equal(503, (int)response.StatusCode);
    }

    [Fact]
    public async Task NullKeyAndMessage_FallBackToTheDefaults()
    {
        await using var app = await TestApp.StartAsync(MapSearch, o =>
        {
            o.ErrorKey = _ => null;
            o.ErrorMessage = _ => null;
        });

        var problem = await app.Client.GetProblemAsync("/search");

        Assert.Equal(["A value is required."], problem.Errors[""]);
    }

    [Fact]
    public async Task Log_LeavesOutTheMessageAndValues()
    {
        var included = SGuardOptions.IncludeValuesInExceptions;
        SGuardOptions.IncludeValuesInExceptions = true;
        var logs = new LogCollector();

        try
        {
            await using var app = await TestApp.StartAsync(
                a => a.MapGet("/limit", (int n) => Results.Ok(ThrowIf.GreaterThan(n, 10))),
                services: s => s.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug)));

            var problem = await app.Client.GetProblemAsync("/limit?n=98765");

            Assert.Equal(400, problem.StatusCode);
            var entry = Assert.Single(logs.Entries, e => e.Category == typeof(SGuardExceptionHandler).FullName);
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Equal("Returned SGuard.Exceptions.GreaterThanException for 'n' as status 400.", entry.Message);
            Assert.Null(entry.Exception);
        }
        finally
        {
            SGuardOptions.IncludeValuesInExceptions = included;
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class LogCollector : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose() { }

        private sealed class Logger(string category, LogCollector collector) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                lock (collector._entries)
                {
                    collector._entries.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
                }
            }
        }
    }
}
