namespace Lantern.Core.Dtos;

/// <summary>
/// Uniform error body used by every Lantern HTTP endpoint. The Android client parses
/// this to show a readable message instead of a raw status code.
/// </summary>
public sealed class ApiErrorDto
{
    /// <summary>Stable machine readable code, e.g. <c>unauthorized</c>, <c>not_found</c>.</summary>
    public required string Code { get; set; }

    public required string Message { get; set; }

    /// <summary>Optional technical detail (never shown as the primary message).</summary>
    public string? Detail { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
