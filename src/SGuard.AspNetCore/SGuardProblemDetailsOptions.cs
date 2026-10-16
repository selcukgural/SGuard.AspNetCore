using Microsoft.AspNetCore.Http;
using SGuard.Exceptions;

namespace SGuard.AspNetCore;

/// <summary>
/// Configures how <see cref="SGuardExceptionHandler"/> turns a guard failure into a validation problem details response.
/// </summary>
public sealed class SGuardProblemDetailsOptions
{
    // Replaced, never changed, so that CanHandle can read it while Handle adds a type.
    private readonly object _gate = new();
    private volatile Type[] _exceptionTypes = [];
    private int _statusCode = StatusCodes.Status400BadRequest;
    private Func<Exception, string?> _errorKey = DefaultErrorKey;
    private Func<Exception, string?> _errorMessage = DefaultErrorMessage;

    /// <summary>
    /// The status code of the response. The default is 400 Bad Request; 422 Unprocessable Content is the other common
    /// choice. It must be a client or server error code (400–599).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 400 or greater than 599.</exception>
    public int StatusCode
    {
        get => _statusCode;
        set => _statusCode = ThrowIf.OutOfRange(value, 400, 599, () => new ArgumentOutOfRangeException(
                                                    nameof(value), value, "The status code must be between 400 and 599."));
    }

    /// <summary>
    /// Returns the key of the error in the response's <c>errors</c> object. The default is
    /// <see cref="DefaultErrorKey"/>. Returning <see langword="null"/> uses the empty key.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Func<Exception, string?> ErrorKey
    {
        get => _errorKey;
        set => _errorKey = ThrowIf.Null(value);
    }

    /// <summary>
    /// Returns the message of the error in the response's <c>errors</c> object. The default is
    /// <see cref="DefaultErrorMessage"/>, a fixed message per guard that never includes the checked value or code.
    /// Returning <see langword="null"/> uses <see cref="DefaultErrorMessage"/>.
    /// </summary>
    /// <remarks>
    /// An exception's own <see cref="Exception.Message"/> names the expression from your source code, such as
    /// <c>request.Amount</c>, and includes the checked values when <c>SGuardOptions</c> is set to include them. Return it
    /// only for exceptions whose messages you write yourself.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Func<Exception, string?> ErrorMessage
    {
        get => _errorMessage;
        set => _errorMessage = ThrowIf.Null(value);
    }

    /// <summary>
    /// Also handles <typeparamref name="TException"/> and the exceptions derived from it, such as the exception your
    /// guards create with a factory: <c>ThrowIf.NullOrEmpty(name, () =&gt; new MyValidationException(...))</c>. SGuard's
    /// own guard exceptions are always handled.
    /// </summary>
    /// <typeparam name="TException">The exception type to handle.</typeparam>
    /// <returns>These options, to chain calls.</returns>
    public SGuardProblemDetailsOptions Handle<TException>() where TException : Exception
    {
        lock (_gate)
        {
            var types = _exceptionTypes;

            if (Array.IndexOf(types, typeof(TException)) < 0)
            {
                _exceptionTypes = [..types, typeof(TException)];
            }
        }

        return this;
    }

    /// <summary>
    /// Returns whether the handler turns <paramref name="exception"/> into a response: it is one of SGuard's guard
    /// exceptions, or a type registered with <see cref="Handle{TException}"/>.
    /// </summary>
    /// <param name="exception">The exception to check.</param>
    /// <returns><see langword="true"/> if the exception is handled.</returns>
    public bool CanHandle(Exception exception)
    {
        ThrowIf.Null(exception);

        if (IsGuardException(exception))
        {
            return true;
        }

        foreach (var type in _exceptionTypes)
        {
            if (type.IsInstanceOfType(exception))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The default <see cref="ErrorKey"/>: the member the guard checked, taken from the exception's
    /// <see cref="ArgumentException.ParamName"/>, which SGuard sets to the expression at the call site.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>name</c> stays <c>name</c>.</item>
    ///   <item><c>request.Address.City</c> becomes <c>Address.City</c>: the first part is the variable that holds the
    ///   object, not a member of it.</item>
    ///   <item>A selector, <c>r =&gt; r.Address!.City</c>, becomes <c>Address.City</c>.</item>
    ///   <item>Anything else, such as a method call, a literal or a cast, becomes the empty key, as do exceptions without
    ///   a <see cref="ArgumentException.ParamName"/>, such as those of <c>ThrowIf.Any</c> and <c>ThrowIf.All</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="exception">The guard exception.</param>
    /// <returns>The key of the error.</returns>
    public static string DefaultErrorKey(Exception exception)
    {
        ThrowIf.Null(exception);
        return MemberPath.FromExpression((exception as ArgumentException)?.ParamName);
    }

    /// <summary>
    /// The default <see cref="ErrorMessage"/>: a fixed message per guard, such as "A value is required." for
    /// <c>ThrowIf.NullOrEmpty</c>, that never includes the checked value or code.
    /// </summary>
    /// <param name="exception">The guard exception.</param>
    /// <returns>The message of the error.</returns>
    public static string DefaultErrorMessage(Exception exception)
    {
        ThrowIf.Null(exception);

        return exception switch
        {
            NullException or NullOrEmptyException or NullOrWhiteSpaceException => "A value is required.",
            GreaterThanException or GreaterThanOrEqualException => "The value is too large.",
            LessThanException or LessThanOrEqualException => "The value is too small.",
            OutOfRangeException => "The value is outside the allowed range.",
            BetweenException => "The value is in a range that isn't allowed.",
            NegativeException => "The value must not be negative.",
            NegativeOrZeroException => "The value must be greater than zero.",
            ZeroException => "The value must not be zero.",
            LongerThanException => "The value is too long.",
            ShorterThanException => "The value is too short.",
            NotDefinedException => "The value isn't one of the allowed values.",
            EqualException => "The value isn't allowed.",
            NotEqualException => "The value doesn't match the expected value.",
            AnyException or AllException => "The collection contains items that aren't allowed.",
            _ => "The value isn't valid."
        };
    }

    // SGuard's guard exceptions all live in SGuard.Exceptions and derive from ArgumentException. Other exceptions SGuard
    // throws, such as InvalidOperationException when a factory returns null, point to a bug and stay 500s.
    private static bool IsGuardException(Exception exception)
    {
        var type = exception.GetType();
        return exception is ArgumentException && type.Assembly == typeof(ThrowIf).Assembly &&
               type.Namespace == "SGuard.Exceptions";
    }
}
