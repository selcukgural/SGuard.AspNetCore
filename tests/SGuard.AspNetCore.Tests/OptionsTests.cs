using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SGuard.Exceptions;

namespace SGuard.AspNetCore.Tests;

public sealed class OptionsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("name", "name")]
    [InlineData("@class", "class")]
    [InlineData("request.Name", "Name")]
    [InlineData("this.Name", "Name")]
    [InlineData("request.Address.City", "Address.City")]
    [InlineData("request?.Address!.City", "Address.City")]
    [InlineData("request.Address!.City!", "Address.City")]
    [InlineData("r => r.Address!.City", "Address.City")]
    [InlineData("(r) => r.Name", "Name")]
    [InlineData("r => r", "")]
    [InlineData("r => other.Name", "")]
    [InlineData("(a, b) => a.Name", "")]
    [InlineData("request.Name.Trim()", "")]
    [InlineData("items[0]", "")]
    [InlineData("\"abcdef\"", "")]
    [InlineData("(DayOfWeek)42", "")]
    [InlineData("a + b", "")]
    [InlineData("_settings.Max_Retries2", "Max_Retries2")]
    [InlineData("şehir.Adı", "Adı")]
    [InlineData("order\n        .Name", "Name")]
    [InlineData("request . Address ?. City", "Address.City")]
    [InlineData("r =>\n    r.Address!\n    .City", "Address.City")]
    [InlineData("static o => o.Name", "Name")]
    [InlineData("(Order o) => o.Name", "Name")]
    [InlineData("static (Order o) => o.Name", "Name")]
    [InlineData("@r => r.Name", "Name")]
    [InlineData("_ => _.Name", "Name")]
    [InlineData("staticr => staticr.Name", "Name")]
    [InlineData("base.Name", "Name")]
    [InlineData("this", "")]
    [InlineData("true", "")]
    [InlineData("null", "")]
    [InlineData("default", "")]
    [InlineData("new Order", "")]
    [InlineData("order.new", "")]
    [InlineData("@true", "true")]
    [InlineData("order.@class", "class")]
    [InlineData("this => this.Name", "")]
    [InlineData("nameof(order.Name)", "")]
    [InlineData("$\"{order.Name}\"", "")]
    public void DefaultErrorKey_IsTheMemberPath(string? paramName, string key)
    {
        Exception exception = paramName is null ? new NullOrEmptyException("message") : new ArgumentException("message", paramName);

        Assert.Equal(key, SGuardProblemDetailsOptions.DefaultErrorKey(exception));
    }

    [Fact]
    public void DefaultErrorKey_UsesTheGuardsParamName()
    {
        var request = new { Address = new { City = "" } };

        var exception = Assert.Throws<NullOrEmptyException>(() => ThrowIf.NullOrEmpty(request.Address.City));

        Assert.Equal("Address.City", SGuardProblemDetailsOptions.DefaultErrorKey(exception));
    }

    public static TheoryData<Action, string> GuardMessages => new()
    {
        { () => ThrowIf.Null((string?)null), "A value is required." },
        { () => ThrowIf.NullOrEmpty(""), "A value is required." },
        { () => ThrowIf.NullOrWhiteSpace(" "), "A value is required." },
        { () => ThrowIf.GreaterThan(2, 1), "The value is too large." },
        { () => ThrowIf.GreaterThanOrEqual(1, 1), "The value is too large." },
        { () => ThrowIf.LessThan(1, 2), "The value is too small." },
        { () => ThrowIf.LessThanOrEqual(1, 1), "The value is too small." },
        { () => ThrowIf.OutOfRange(0, 1, 10), "The value is outside the allowed range." },
        { () => ThrowIf.Between(5, 1, 10), "The value is in a range that isn't allowed." },
        { () => ThrowIf.Negative(-1), "The value must not be negative." },
        { () => ThrowIf.NegativeOrZero(0), "The value must be greater than zero." },
        { () => ThrowIf.Zero(0), "The value must not be zero." },
        { () => ThrowIf.LongerThan("abc", 2), "The value is too long." },
        { () => ThrowIf.ShorterThan("a", 2), "The value is too short." },
        { () => ThrowIf.NotDefined((DayOfWeek)42), "The value isn't one of the allowed values." },
        { () => ThrowIf.Equal(1, 1), "The value isn't allowed." },
        { () => ThrowIf.NotEqual(1, 2), "The value doesn't match the expected value." },
        { () => ThrowIf.Any(new[] { 1 }, i => i > 0), "The collection contains items that aren't allowed." },
        { () => ThrowIf.All(new[] { 1 }, i => i > 0), "The collection contains items that aren't allowed." },
    };

    [Theory]
    [MemberData(nameof(GuardMessages))]
    public void EveryGuard_IsHandled_WithItsOwnMessage(Action guard, string message)
    {
        var exception = Assert.ThrowsAny<Exception>(guard);
        var options = new SGuardProblemDetailsOptions();

        Assert.True(options.CanHandle(exception));
        Assert.Equal(message, SGuardProblemDetailsOptions.DefaultErrorMessage(exception));
    }

    [Fact]
    public void EveryGuardException_HasAMessageOfItsOwn()
    {
        // A guard exception added to SGuard later must get a message here too, not the generic fallback.
        var types = typeof(ThrowIf).Assembly.GetTypes()
                                   .Where(t => t.Namespace == "SGuard.Exceptions" && typeof(Exception).IsAssignableFrom(t))
                                   .ToList();

        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            var exception = (Exception)Activator.CreateInstance(type, "message")!;
            Assert.NotEqual("The value isn't valid.", SGuardProblemDetailsOptions.DefaultErrorMessage(exception));
        }
    }

    [Fact]
    public void Handle_IncludesDerivedTypes_AndOnlyThose()
    {
        var options = new SGuardProblemDetailsOptions().Handle<ArgumentOutOfRangeException>();

        Assert.True(options.CanHandle(new ArgumentOutOfRangeException()));
        Assert.True(options.CanHandle(new GreaterThanException("message")));
        Assert.False(options.CanHandle(new ArgumentException()));
        Assert.False(options.CanHandle(new InvalidOperationException()));
    }

    [Fact]
    public void BclExceptions_AreNotHandledByDefault()
    {
        var options = new SGuardProblemDetailsOptions();

        Assert.False(options.CanHandle(new ArgumentNullException("name")));
        Assert.False(options.CanHandle(new ArgumentOutOfRangeException("count")));
        Assert.False(options.CanHandle(new InvalidOperationException("The exception factory returned null.")));
    }

    [Fact]
    public void NullDelegates_AreRejected()
    {
        var options = new SGuardProblemDetailsOptions();

        Assert.Throws<SGuard.Exceptions.NullException>(() => options.ErrorKey = null!);
        Assert.Throws<SGuard.Exceptions.NullException>(() => options.ErrorMessage = null!);
    }

    [Fact]
    public async Task Handle_IsSafeWhileRequestsCheckExceptions()
    {
        var options = new SGuardProblemDetailsOptions();
        var exception = new MyValidationException("message");
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                options.CanHandle(exception);
            }
        });

        options.Handle<InvalidOperationException>().Handle<FormatException>().Handle<TimeoutException>()
               .Handle<MyValidationException>().Handle<MyValidationException>();
        stop.Cancel();
        await reader;

        Assert.True(options.CanHandle(exception));
    }

    [Theory]
    [InlineData(399)]
    [InlineData(600)]
    [InlineData(200)]
    public void StatusCode_OutsideErrorCodes_Throws(int statusCode)
    {
        var options = new SGuardProblemDetailsOptions();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.StatusCode = statusCode);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(400, options.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    [InlineData(599)]
    public void StatusCode_AcceptsErrorCodes(int statusCode)
    {
        var options = new SGuardProblemDetailsOptions { StatusCode = statusCode };

        Assert.Equal(statusCode, options.StatusCode);
    }

    [Fact]
    public async Task Handler_DoesNothing_WhenTheResponseHasStarted()
    {
        var handler = CreateHandler();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        var handled = await handler.TryHandleAsync(context, new NullOrEmptyException("message"), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public void AddSGuardProblemDetails_Twice_RegistersTheHandlerOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSGuardProblemDetails().AddSGuardProblemDetails();

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IExceptionHandler>().OfType<SGuardExceptionHandler>());
    }

    [Fact]
    public void AddSGuardProblemDetails_AppliesTheConfiguration()
    {
        var services = new ServiceCollection();

        services.AddSGuardProblemDetails(o => o.StatusCode = 422);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(422, provider.GetRequiredService<IOptions<SGuardProblemDetailsOptions>>().Value.StatusCode);
    }

    private static SGuardExceptionHandler CreateHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSGuardProblemDetails();
        var provider = services.BuildServiceProvider();

        return new SGuardExceptionHandler(provider.GetRequiredService<IProblemDetailsService>(),
                                          Options.Create(new SGuardProblemDetailsOptions()),
                                          NullLogger<SGuardExceptionHandler>.Instance);
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
