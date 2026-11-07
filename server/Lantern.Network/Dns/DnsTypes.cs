namespace Lantern.Network.Dns;

/// <summary>DNS resource record / query types used by DNS-SD.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    Ns = 2,
    CName = 5,
    Soa = 6,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

/// <summary>DNS class.</summary>
public enum DnsClass : ushort
{
    Internet = 1,
    Any = 255,
}

/// <summary>Header bits of a DNS message.</summary>
[Flags]
public enum DnsFlags : ushort
{
    None = 0,

    /// <summary>Set on responses.</summary>
    Response = 0x8000,

    /// <summary>Set when the responder owns the records (mDNS always sets it).</summary>
    Authoritative = 0x0400,

    Truncated = 0x0200,

    /// <summary>Always 0 for mDNS (RFC 6762 section 18.3).</summary>
    RecursionDesired = 0x0100,

    RecursionAvailable = 0x0080,
}

/// <summary>Thrown when a datagram cannot be parsed as a DNS message.</summary>
public sealed class DnsFormatException : Exception
{
    public DnsFormatException(string message) : base(message) { }

    public DnsFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Helpers for DNS names (dotted, label based, case insensitive).</summary>
public static class DnsName
{
    /// <summary>Maximum length of a single label.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>Maximum length of an encoded name (255 bytes including length octets).</summary>
    public const int MaxNameLength = 255;

    /// <summary>Splits a dotted name into labels, ignoring a trailing root dot.</summary>
    public static string[] Split(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var trimmed = name.TrimEnd('.');
        return trimmed.Length == 0 ? [] : trimmed.Split('.');
    }

    /// <summary>Normalizes a name for comparisons (lower case, no trailing dot).</summary>
    public static string Canonical(string name) => name.TrimEnd('.').ToLowerInvariant();

    /// <summary>True when <paramref name="name"/> equals or sits below <paramref name="domain"/>.</summary>
    public static bool IsWithin(string name, string domain)
    {
        var n = Canonical(name);
        var d = Canonical(domain);
        return n == d || n.EndsWith("." + d, StringComparison.Ordinal);
    }

    /// <summary>Makes a string safe to use as one label (dots and whitespace would break the encoding).</summary>
    public static string SanitizeLabel(string label)
    {
        var builder = new System.Text.StringBuilder(label.Length);
        foreach (var ch in label.Trim())
        {
            builder.Append(ch switch
            {
                '.' => '_',
                '\\' => '_',
                '\r' or '\n' or '\t' => ' ',
                _ => ch,
            });
        }

        var result = builder.ToString().Trim();
        if (result.Length == 0) result = "lantern";
        return result.Length <= MaxLabelLength ? result : result[..MaxLabelLength];
    }

    /// <summary>Builds an instance name for a DNS-SD service, e.g. "My PC._lantern._tcp.local".</summary>
    public static string ServiceInstance(string instanceName, string serviceTypeFqdn) =>
        SanitizeLabel(instanceName) + "." + Canonical(serviceTypeFqdn);
}
