using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lantern.Core.Api;

/// <summary>Shared JSON configuration so PC and Android see identical payloads.</summary>
public static class ApiJson
{
    /// <summary>camelCase, string enums, nulls kept (the client relies on explicit nulls for resume).</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    public static JsonSerializerOptions Create(bool indented = false) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = indented,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? IndentedOptions : Options);

    private static readonly JsonSerializerOptions IndentedOptions = Create(indented: true);
}
