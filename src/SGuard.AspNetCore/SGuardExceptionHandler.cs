using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SGuard.AspNetCore;

/// <summary>
/// An <see cref="IExceptionHandler"/> that returns a guard failure as a validation problem details response
/// (RFC 9457): status 400 by default, with an <c>errors</c> object that maps the failing member to a message.
/// </summary>
/// <remarks>
/// Register it with <c>AddSGuardProblemDetails</c> and enable the exception handler middleware with
/// <c>app.UseExceptionHandler()</c>. Exceptions it doesn't handle, see
/// <see cref="SGuardProblemDetailsOptions.CanHandle"/>, go on to the next handler.
/// </remarks>
public sealed partial class SGuardExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly IOptions<SGuardProblemDetailsOptions> _options;
    private readonly ILogger<SGuardExceptionHandler> _logger;

    /// <summary>
    /// Creates the handler.
    /// </summary>
    /// <param name="problemDetails">Writes the response body.</param>
    /// <param name="options">The handler's options.</param>
    /// <param name="logger">Logs each handled failure at debug level.</param>
    public SGuardExceptionHandler(IProblemDetailsService problemDetails, IOptions<SGuardProblemDetailsOptions> options,
                                  ILogger<SGuardExceptionHandler> logger)
    {
        _problemDetails = ThrowIf.Null(problemDetails);
        _options = ThrowIf.Null(options);
        _logger = ThrowIf.Null(logger);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ThrowIf.Null(httpContext);
        ThrowIf.Null(exception);

        var options = _options.Value;

        if (httpContext.Response.HasStarted || !options.CanHandle(exception))
        {
            return false;
        }

        var key = options.ErrorKey(exception) ?? string.Empty;
        var message = options.ErrorMessage(exception) ?? SGuardProblemDetailsOptions.DefaultErrorMessage(exception);

        httpContext.Response.StatusCode = options.StatusCode;

        var problem = new HttpValidationProblemDetails(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [key] = [message]
        })
        {
            Status = options.StatusCode
        };

        // ASP.NET Core 9 and later add the trace ID themselves; 8 doesn't, unlike the validation problems of controllers.
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // The exception's message is left out of the log: it can include the checked values.
        LogHandled(exception.GetType().FullName, key, options.StatusCode);

        // A client whose Accept header allows neither JSON nor */* still gets the status code, with an empty body.
        await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });

        return true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Returned {ExceptionType} for '{ErrorKey}' as status {StatusCode}.")]
    private partial void LogHandled(string? exceptionType, string errorKey, int statusCode);
}
