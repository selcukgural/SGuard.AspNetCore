# SGuard.AspNetCore

[![CI](https://github.com/selcukgural/SGuard.AspNetCore/actions/workflows/ci.yml/badge.svg)](https://github.com/selcukgural/SGuard.AspNetCore/actions)
[![NuGet](https://img.shields.io/nuget/v/SGuard.AspNetCore.svg)](https://www.nuget.org/packages/SGuard.AspNetCore)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Returns [SGuard](https://github.com/selcukgural/SGuard) guard failures from ASP.NET Core as `400 Bad Request`
validation problem details ([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)), for minimal APIs and controllers alike.
Targets .NET 8, 9 and 10, works with trimming and Native AOT (CI publishes and runs a Native AOT app), and needs
SGuard 3.0 or later. In a Native AOT app, check members directly instead of with a selector: SGuard's `NullOrEmpty`
selectors don't support AOT.

```csharp
app.MapPost("/orders", (CreateOrder command) =>
{
    ThrowIf.NullOrEmpty(command.CustomerId);
    ThrowIf.OutOfRange(command.Quantity, 1, 100);
    ThrowIf.NullOrEmpty(command, c => c.ShippingAddress!.City);
    // ...
});
```

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "Quantity": ["The value is outside the allowed range."] },
  "traceId": "00-84b3c6f1e0a5c2d9b1f3e7a4c6d8e0f2-1a2b3c4d5e6f7a8b-00"
}
```

## 📦 Installation

```bash
dotnet add package SGuard.AspNetCore
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSGuardProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
```

`AddSGuardProblemDetails` registers the problem details services and an
[`IExceptionHandler`](https://learn.microsoft.com/aspnet/core/fundamentals/error-handling#iexceptionhandler);
`UseExceptionHandler` turns the exception handler middleware on. Put it first in the pipeline, so it catches the
exceptions of everything after it. If you call `AddProblemDetails` yourself, its options, such as
`CustomizeProblemDetails`, apply to these responses too.

Exception handlers run in the order they are registered, and the first one that handles an exception wins. If you have
a catch-all handler of your own, register it after `AddSGuardProblemDetails`:

```csharp
builder.Services.AddSGuardProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>(); // gets everything SGuard.AspNetCore doesn't handle
```

## 🔒 What the client sees

A guard exception's message is written for you, the developer: it names the expression from your source code, such as
`command.Quantity`, and includes the checked value when `SGuardOptions.IncludeValuesInExceptions` is on. None of that
reaches the client, unless you copy it there yourself: a `CustomizeProblemDetails` callback or an `IProblemDetailsWriter`
of your own can read the exception from `ProblemDetailsContext.Exception`.

- **The key** is the member the guard checked: `command.Quantity` becomes `Quantity`, `command.ShippingAddress!.City`
  and the selector `c => c.ShippingAddress!.City` become `ShippingAddress.City`, and a parameter such as `quantity`
  stays `quantity`. Expressions that aren't a member, such as `name.Trim()`, and the collection guards `ThrowIf.Any` and
  `ThrowIf.All` use the empty key `""`, which is what ASP.NET Core uses for errors that don't belong to one field.
  The key comes from the guard's first argument, so pass the checked member first (`ThrowIf.Equal(order.Status,
  OrderStatus.Closed)`), and inside the request type's own methods write `this.Address.City`: the first part of a path
  is always taken as the variable that holds the object.
- **The message** is fixed per guard and never includes a value:

| Guard | Message |
|---|---|
| `Null`, `NullOrEmpty`, `NullOrWhiteSpace` | A value is required. |
| `GreaterThan`, `GreaterThanOrEqual` | The value is too large. |
| `LessThan`, `LessThanOrEqual` | The value is too small. |
| `OutOfRange` | The value is outside the allowed range. |
| `Between` | The value is in a range that isn't allowed. |
| `Negative` | The value must not be negative. |
| `NegativeOrZero` | The value must be greater than zero. |
| `Zero` | The value must not be zero. |
| `LongerThan` / `ShorterThan` | The value is too long. / The value is too short. |
| `NotDefined` | The value isn't one of the allowed values. |
| `Equal` / `NotEqual` | The value isn't allowed. / The value doesn't match the expected value. |
| `Any`, `All` | The collection contains items that aren't allowed. |

A client whose `Accept` header allows neither JSON nor `*/*`, such as one that asks only for `text/html`, gets the status
code with an empty body. Browsers send `*/*` too, so they get the JSON.

## ⚙️ Options

```csharp
builder.Services.AddSGuardProblemDetails(options =>
{
    // 422 Unprocessable Content instead of 400.
    options.StatusCode = StatusCodes.Status422UnprocessableEntity;

    // camelCase keys, to match the JSON of the request.
    options.ErrorKey = e => JsonNamingPolicy.CamelCase.ConvertName(SGuardProblemDetailsOptions.DefaultErrorKey(e));

    // Also handle your own exception, thrown by a guard's factory, and show its message, which you wrote for clients.
    options.Handle<OrderValidationException>();
    options.ErrorMessage = e => e is OrderValidationException
        ? e.Message
        : SGuardProblemDetailsOptions.DefaultErrorMessage(e);
});
```

`camelCase` here converts the whole path at once (`ShippingAddress.City` becomes `shippingAddress.City`); split it on
`.` first to convert each part.

## Which exceptions are handled

SGuard's guard exceptions, the types in `SGuard.Exceptions`, and the types you add with `options.Handle<T>()`,
including the types derived from them. Everything else goes on to the next exception handler, and by default becomes a
`500`. That includes the BCL exceptions, such as an `ArgumentNullException` thrown by your own code or a library, and
the `InvalidOperationException` SGuard throws when an exception factory returns `null`: they point to a bug, not to a
bad request.

That also covers the `ArgumentNullException` that `ThrowIf.LongerThan`, `ShorterThan`, `GreaterThan`, `LessThan`,
`Between`, `Any` and `All` throw when the value itself is `null`: it means the guard was called with nothing to check.
When a request field can be missing, check it first, and the client gets a `400` for it:

```csharp
ThrowIf.NullOrEmpty(command.Password);   // missing -> 400, "Password": "A value is required."
ThrowIf.ShorterThan(command.Password, 12); // short   -> 400, "Password": "The value is too short."
```

A guard that checks your application's own state rather than the request, such as a configuration value, throws a
guard exception too, and would turn into a `400`. Give such guards an exception of your own:

```csharp
ThrowIf.NullOrEmpty(_options.ConnectionString, () => new InvalidOperationException("The connection string is missing."));
```

## Logging

The handler logs each failure it handles at `Debug` level, with the exception type, the error key and the status code,
but not the message. Separately, the exception handler middleware of ASP.NET Core 8 and 9 logs every exception at
`Error` level, including those a handler returns as a `400`, with the exception's message; ASP.NET Core 10 no longer
logs exceptions that a handler handled. On 8 and 9, filter the `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware`
category if those entries are too noisy, and keep `SGuardOptions.IncludeValuesInExceptions` off if your logs must not
hold request values.

## License

[MIT](https://github.com/selcukgural/SGuard.AspNetCore/blob/main/LICENSE)
