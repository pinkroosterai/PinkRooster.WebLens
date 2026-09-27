using Microsoft.AspNetCore.Diagnostics;

namespace PinkRooster.WebLens.Api.Errors;

/// <summary>A caller that hung up is not an error and nobody is left to read a response, so nothing is written.</summary>
internal sealed class ClientDisconnectedExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) =>
        ValueTask.FromResult(exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested);
}
