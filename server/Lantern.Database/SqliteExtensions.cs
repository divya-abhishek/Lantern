using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lantern.Database;

/// <summary>
/// Small ADO.NET conveniences: parameter binding that normalises .NET types to
/// SQLite storage types and null-safe reader accessors.
/// </summary>
internal static class SqliteExtensions
{
    public static SqliteCommand AddParam(this SqliteCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name.StartsWith('$') || name.StartsWith('@') ? name : "$" + name;
        p.Value = ToDbValue(value);
        command.Parameters.Add(p);
        return command;
    }

    public static object ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
        TimeSpan ts => (long)ts.TotalMilliseconds,
        _ => value,
    };

    public static string? GetStringOrNull(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? null : Convert.ToString(r[name], CultureInfo.InvariantCulture);

    public static long GetInt64(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? 0 : Convert.ToInt64(r[name], CultureInfo.InvariantCulture);

    public static long? GetInt64OrNull(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? null : Convert.ToInt64(r[name], CultureInfo.InvariantCulture);

    public static int GetInt32(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? 0 : Convert.ToInt32(r[name], CultureInfo.InvariantCulture);

    public static int? GetInt32OrNull(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? null : Convert.ToInt32(r[name], CultureInfo.InvariantCulture);

    public static double GetDouble(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? 0 : Convert.ToDouble(r[name], CultureInfo.InvariantCulture);

    public static double? GetDoubleOrNull(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? null : Convert.ToDouble(r[name], CultureInfo.InvariantCulture);

    public static float GetSingle(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? 0f : Convert.ToSingle(r[name], CultureInfo.InvariantCulture);

    public static bool GetBool(this IDataRecord r, string name) =>
        r[name] is not (DBNull or null) && Convert.ToInt64(r[name], CultureInfo.InvariantCulture) != 0;

    public static bool? GetBoolOrNull(this IDataRecord r, string name) =>
        r[name] is DBNull or null ? null : Convert.ToInt64(r[name], CultureInfo.InvariantCulture) != 0;

    public static DateTime GetDateTime(this IDataRecord r, string name) =>
        r.GetDateTimeOrNull(name) ?? default;

    public static DateTime? GetDateTimeOrNull(this IDataRecord r, string name)
    {
        if (r[name] is DBNull or null) return null;
        var text = Convert.ToString(r[name], CultureInfo.InvariantCulture);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;
    }

    public static TEnum GetEnum<TEnum>(this IDataRecord r, string name, TEnum fallback = default!)
        where TEnum : struct, Enum
    {
        if (r[name] is DBNull or null) return fallback;

        var value = r[name];

        // SQLite may hand the enum back as text when the column is not strictly typed.
        if (value is string text)
        {
            return Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : fallback;
        }

        var numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);

        // Enum.IsDefined requires the value boxed as the enum's own underlying type; SQLite
        // always reports integers as Int64, which throws for an int-backed enum.
        var typed = Convert.ChangeType(numeric, Enum.GetUnderlyingType(typeof(TEnum)), CultureInfo.InvariantCulture);
        return typed is not null && Enum.IsDefined(typeof(TEnum), typed)
            ? (TEnum)Enum.ToObject(typeof(TEnum), numeric)
            : fallback;
    }

    public static string ToIso(this DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string? ToIsoOrNull(this DateTime? value) => value?.ToIso();
}
