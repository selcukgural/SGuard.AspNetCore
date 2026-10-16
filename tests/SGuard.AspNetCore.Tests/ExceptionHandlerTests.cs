using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SGuard.AspNetCore.Tests;

public sealed record Address(string? City);

public sealed record Order(string? Name, int Quantity, Address? Address, List<int>? Lines);

public sealed class MyValidationException(string message) : Exception(message);

public sealed class DerivedValidationException(string message) : Exception(message);

// SGuardOptions is process-wide; this collection runs the tests that change it on their own.
[CollectionDefinition(nameof(SGuardOptionsCollection), DisableParallelization = true)]
public sealed class SGuardOptionsCollection;

[Collection(nameof(SGuardOptionsCollection))]
public sealed class ExceptionHandlerTests
{
    private static Task<TestApp> StartOrdersAsync(Action<SGuardProblemDetailsOptions>? configure = null)
        => TestApp.StartAsync(app =>
        {
            app.MapPost("/orders", (Order order) =>
            {
                ThrowIf.NullOrEmpty(order.Name);
                ThrowIf.GreaterThan(order.Quantity, 100);
                ThrowIf.NullOrEmpty(order, o => o.Address!.City);
                ThrowIf.Any(order.Lines!, l => l < 0);
                return Results.Ok();
            });
            app.MapGet("/search", (string? q) => Results.Ok(ThrowIf.NullOrWhiteSpace(q)));
            app.MapGet("/trimmed", (string? q) => Results.Ok(ThrowIf.NullOrEmpty((q ?? "").Trim())));
            app.MapGet("/bug", IResult () => throw new InvalidOperationException("Not a guard."));
            app.MapGet("/argument", IResult () => throw new ArgumentException("Not a guard either.", "id"));
            app.MapGet("/custom", IResult () => throw new MyValidationException("The order is closed."));
        }, configure);

    private static Order ValidOrder => new("Book", 1, new Address("Ankara"), [1, 2]);

    [Fact]
    public async Task GuardFailure_IsA400ValidationProblem()
    {
        await using var app = await StartOrdersAsync();

        var problem = await app.Client.PostProblemAsync("/orders", ValidOrder with { Name = "" });

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal("application/problem+json", problem.ContentType);
        Assert.Equal("One or more validation errors occurred.", problem.Json.GetProperty("title").GetString());
        Assert.Equal(400, problem.Json.GetProperty("status").GetInt32());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", problem.Json.GetProperty("type").GetString());
        Assert.True(problem.Json.TryGetProperty("traceId", out _));
        var error = Assert.Single(problem.Errors);
        Assert.Equal("Name", error.Key);
        Assert.Equal(["A value is required."], error.Value);
    }

    [Fact]
    public async Task Response_ExposesNoValuesOrCode()
    {
        var included = SGuardOptions.IncludeValuesInExceptions;
        SGuardOptions.IncludeValuesInExceptions = true;

        try
        {
            await using var app = await StartOrdersAsync();

            var problem = await app.Client.PostProblemAsync("/orders", ValidOrder with { Quantity = 98765 });

            Assert.Equal(["The value is too large."], problem.Errors["Quantity"]);
            Assert.DoesNotContain("98765", problem.Body);
            Assert.DoesNotContain("order.", problem.Body);
            Assert.DoesNotContain("greater than", problem.Body);
            Assert.False(problem.Json.TryGetProperty("detail", out _));
            Assert.False(problem.Json.TryGetProperty("exception", out _));
        }
        finally
        {
            SGuardOptions.IncludeValuesInExceptions = included;
        }
    }

    [Theory]
    [InlineData("/search", "q", "A value is required.")]
    [InlineData("/trimmed", "", "A value is required.")]
    public async Task Key_FollowsTheGuardedExpression(string path, string key, string message)
    {
        await using var app = await StartOrdersAsync();

        var problem = await app.Client.GetProblemAsync(path);

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal([message], problem.Errors[key]);
    }

    [Fact]
    public async Task Selector_IsKeyedByTheSelectedMember()
    {
        await using var app = await StartOrdersAsync();

        var problem = await app.Client.PostProblemAsync("/orders", ValidOrder with { Address = new Address(null) });

        Assert.Equal(["A value is required."], problem.Errors["Address.City"]);
    }

    [Fact]
    public async Task GuardWithoutParamName_UsesTheEmptyKey()
    {
        await using var app = await StartOrdersAsync();

        var problem = await app.Client.PostProblemAsync("/orders", ValidOrder with { Lines = [1, -1] });

        Assert.Equal(["The collection contains items that aren't allowed."], problem.Errors[""]);
    }

    [Theory]
    [InlineData("/bug")]
    [InlineData("/argument")]
    [InlineData("/custom")]
    public async Task OtherExceptions_AreNotHandled(string path)
    {
        await using var app = await StartOrdersAsync();

        var problem = await app.Client.GetProblemAsync(path);

        Assert.Equal(500, problem.StatusCode);
        Assert.False(problem.Json.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     problem.Json.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task RegisteredException_IsHandled_WithTheGenericMessage()
    {
        await using var app = await StartOrdersAsync(o => o.Handle<MyValidationException>());

        var problem = await app.Client.GetProblemAsync("/custom");

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(["The value isn't valid."], problem.Errors[""]);
        Assert.DoesNotContain("closed", problem.Body);
    }

    [Fact]
    public async Task ErrorMessage_CanReturnYourOwnExceptionsMessage()
    {
        await using var app = await StartOrdersAsync(o =>
        {
            o.Handle<MyValidationException>();
            o.ErrorMessage = e => e is MyValidationException ? e.Message : SGuardProblemDetailsOptions.DefaultErrorMessage(e);
            o.ErrorKey = e => e is MyValidationException ? "order" : SGuardProblemDetailsOptions.DefaultErrorKey(e);
        });

        var custom = await app.Client.GetProblemAsync("/custom");
        var guard = await app.Client.PostProblemAsync("/orders", ValidOrder with { Name = null });

        Assert.Equal(["The order is closed."], custom.Errors["order"]);
        Assert.Equal(["A value is required."], guard.Errors["Name"]);
    }

    [Fact]
    public async Task StatusCode_CanBe422()
    {
        await using var app = await StartOrdersAsync(o => o.StatusCode = StatusCodes.Status422UnprocessableEntity);

        var problem = await app.Client.PostProblemAsync("/orders", ValidOrder with { Name = "" });

        Assert.Equal(422, problem.StatusCode);
        Assert.Equal(422, problem.Json.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", problem.Json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ClientThatDoesNotAcceptJson_GetsTheStatusCodeWithoutABody()
    {
        await using var app = await StartOrdersAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var problem = await Problem.ReadAsync(await app.Client.SendAsync(request));

        Assert.Equal(400, problem.StatusCode);
        Assert.Equal("", problem.Body);
    }

    [Fact]
    public async Task CustomizeProblemDetails_StillApplies()
    {
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/search", (string? q) => Results.Ok(ThrowIf.NullOrWhiteSpace(q))),
            services: s => s.AddProblemDetails(o => o.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["api"] = "v1"));

        var problem = await app.Client.GetProblemAsync("/search");

        Assert.Equal("v1", problem.Json.GetProperty("api").GetString());
        Assert.Equal(["A value is required."], problem.Errors["q"]);
    }

    [Fact]
    public async Task PassingGuards_DontAffectTheResponse()
    {
        await using var app = await StartOrdersAsync();

        var response = await app.Client.PostAsync("/orders", System.Net.Http.Json.JsonContent.Create(ValidOrder));

        Assert.Equal(200, (int)response.StatusCode);
    }
}
