using System.Net.Http.Json;
using System.Text.Json;
using Lantern.Core.Api;
using Lantern.Core.Dtos;

namespace Lantern.Server.Services;

/// <summary>
/// Response helpers shared by every endpoint group. Payloads are serialized with
/// <see cref="ApiJson.Options"/> instead of the minimal API serializer so the wire
/// format (camelCase, string enums, explicit nulls) cannot drift from the client.
/// </summary>
internal static class ApiResults
{
    public static IResult Ok<T>(T value) => Results.Json(value, ApiJson.Options);

    public static IResult Created<T>(T value) =>
        Results.Json(value, ApiJson.Options, statusCode: StatusCodes.Status201Created);

    public static IResult Accepted<T>(T value) =>
        Results.Json(value, ApiJson.Options, statusCode: StatusCodes.Status202Accepted);

    public static IResult NoContent() => Results.StatusCode(StatusCodes.Status204NoContent);

    public static IResult Error(int status, string code, string message, string? detail = null) =>
        Results.Json(
            new ApiErrorDto { Code = code, Message = message, Detail = detail },
            ApiJson.Options,
            statusCode: status);

    public static IResult BadRequest(string message, string code = "bad_request", string? detail = null) =>
        Error(StatusCodes.Status400BadRequest, code, message, detail);

    public static IResult Unauthorized(string? message = null) =>
        Error(
            StatusCodes.Status401Unauthorized,
            "unauthorized",
            message ?? "This server requires a paired device. Approve this device in Lantern on the PC.");

    public static IResult Forbidden(string message) =>
        Error(StatusCodes.Status403Forbidden, "forbidden", message);

    public static IResult NotFound(string message, string code = "not_found") =>
        Error(StatusCodes.Status404NotFound, code, message);

    public static IResult Conflict(string message, string code = "conflict") =>
        Error(StatusCodes.Status409Conflict, code, message);

    public static IResult TooMany(string message, string code = "too_many_requests") =>
        Error(StatusCodes.Status429TooManyRequests, code, message);

    public static IResult Unavailable(string message, string code = "unavailable", string? detail = null) =>
        Error(StatusCodes.Status503ServiceUnavailable, code, message, detail);

    /// <summary>Reads a JSON body, tolerating an absent or malformed one (returns null then).</summary>
    public static async Task<T?> BodyAsync<T>(HttpContext http, CancellationToken ct = default) where T : class
    {
        if (http.Request.ContentLength is 0) return null;

        var contentType = http.Request.ContentType;
        if (string.IsNullOrWhiteSpace(contentType)
            || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await http.Request.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int QueryInt(HttpContext http, string name, int fallback) =>
        int.TryParse(http.Request.Query[name], out var value) ? value : fallback;

    public static long? QueryLong(HttpContext http, string name) =>
        long.TryParse(http.Request.Query[name], out var value) ? value : null;

    public static bool? QueryBool(HttpContext http, string name) =>
        bool.TryParse(http.Request.Query[name], out var value) ? value : null;

    public static string? QueryText(HttpContext http, string name)
    {
        var value = http.Request.Query[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
