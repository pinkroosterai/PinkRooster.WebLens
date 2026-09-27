using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using PinkRooster.WebLens.Fetch;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Errors;

/// <summary>
/// The error mapping for MCP: the same problem types as <c>/v1</c>, as a tool result flagged as an error, so an agent can tell a
/// refused URL from a broken tool. Text only: an error result must not claim to match a tool's output schema. The first
/// line is always <c>type: title</c>; the lines after it carry the detail, the field at fault and when to retry.
/// </summary>
internal static class ToolErrors
{
    public static CallToolResult From(SearchException ex, ILogger logger)
    {
        var (status, type, title) = SearchExceptionHandler.Classify(ex.Kind);
        SearchExceptionHandler.Log(logger, status, ex);
        var field = ex.Kind == SearchErrorKind.InvalidQuery ? McpFields.Search(ex.Field) : null;
        return Result(type, title, ex.Message, field, SearchExceptionHandler.RetryAfterSeconds(ex, status));
    }

    public static CallToolResult From(FetchException ex, ILogger logger)
    {
        var (status, type, title) = FetchExceptionHandler.Classify(ex.Kind);
        FetchExceptionHandler.Log(logger, status, ex);
        var field = ex.Kind == FetchErrorKind.InvalidRequest ? McpFields.Fetch(ex.Field) : null;

        // A conversion failure is our bug: its detail stays out of the result like any other 500.
        var detail = status == StatusCodes.Status500InternalServerError ? null : ex.Message;
        return Result(type, title, detail, field, FetchExceptionHandler.RetryAfterSeconds(ex, status));
    }

    public static CallToolResult Validation(string field, string message) =>
        Result(ProblemTypes.Validation, "The request is not valid.", message, field, null);

    public static CallToolResult RateLimited(int seconds) =>
        Result(ProblemTypes.RateLimited, "This API key has sent too many requests.", null, null, seconds);

    public static CallToolResult PageChanged() =>
        Result(ProblemTypes.PageChanged, "The page changed since its first part was read.", "Start again from start=0 without a fingerprint.", null, null);

    private static CallToolResult Result(string type, string title, string? detail, string? field, int? retryAfterSeconds)
    {
        var text = new StringBuilder().Append(type).Append(": ").Append(title);
        if (detail is not null)
        {
            text.Append('\n').Append(detail);
        }

        if (field is not null)
        {
            text.Append("\nArgument: ").Append(field);
        }

        if (retryAfterSeconds is { } seconds)
        {
            text.Append("\nRetry after ").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(" second(s).");
        }

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text.ToString() }] };
    }
}

/// <summary>A refusal decided in the host (rate limit, argument rules, paging), carried out of a tool to the call-tool filter.</summary>
internal sealed class ToolRefusal(CallToolResult result) : Exception("The tool call was refused.")
{
    public CallToolResult Result { get; } = result;
}

/// <summary>The tool argument a module's <c>Field</c> refers to, named as the tool's input schema names it.</summary>
internal static class McpFields
{
    public static string Search(string? moduleField) => Camel(Endpoints.SearchMapping.ToContractField(moduleField));

    public static string Fetch(string? moduleField)
    {
        var contract = Endpoints.FetchMapping.ToContractField(moduleField);
        var dot = contract.LastIndexOf('.');
        return Camel(dot < 0 ? contract : contract[(dot + 1)..]);
    }

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
