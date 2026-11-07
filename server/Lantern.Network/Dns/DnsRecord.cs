namespace Lantern.Network.Dns;

/// <summary>One entry of the question section.</summary>
public sealed class DnsQuestion
{
    public DnsQuestion(string name, DnsRecordType type = DnsRecordType.Any)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }

    public DnsRecordType Type { get; }

    /// <summary>Set when the sender asks for a unicast reply (the QU bit in mDNS).</summary>
    public bool UnicastResponse { get; set; }

    public override string ToString() => $"{Name} {Type}{(UnicastResponse ? " (QU)" : string.Empty)}";
}

/// <summary>Base type for every resource record this stack can read or write.</summary>
public abstract class DnsRecord
{
    protected DnsRecord(string name) => Name = name;

    public string Name { get; }

    public abstract DnsRecordType Type { get; }

    public DnsClass Class { get; set; } = DnsClass.Internet;

    /// <summary>Lifetime in seconds; 0 announces the removal of a record.</summary>
    public uint TtlSeconds { get; set; } = Core.Discovery.DiscoveryConstants.MdnsHostTtlSeconds;

    /// <summary>Set on authoritative records so resolvers replace their cached copy.</summary>
    public bool CacheFlush { get; set; }

    internal abstract void WriteData(DnsBufferWriter writer);

    internal void Write(DnsBufferWriter writer)
    {
        writer.WriteName(Name);
        writer.WriteUInt16((ushort)Type);
        writer.WriteUInt16((ushort)((ushort)Class | (CacheFlush ? 0x8000 : 0)));
        writer.WriteUInt32(TtlSeconds);

        var lengthOffset = writer.Length;
        writer.WriteUInt16(0);
        var dataStart = writer.Length;
        WriteData(writer);
        writer.PatchUInt16(lengthOffset, (ushort)(writer.Length - dataStart));
    }

    internal static DnsRecord Read(
        DnsBufferReader reader,
        string name,
        DnsRecordType type,
        DnsClass @class,
        bool cacheFlush,
        uint ttl,
        int dataLength)
    {
        var dataStart = reader.Position;

        DnsRecord record = type switch
        {
            DnsRecordType.A => new ARecord(name, reader.ReadAddress()),
            DnsRecordType.Ptr => new PtrRecord(name, reader.ReadName()),
            DnsRecordType.Srv => ReadSrv(reader, name),
            DnsRecordType.Txt => ReadTxt(reader, name, dataLength),
            _ => new UnknownRecord(name, type, reader.ReadBytes(dataLength)),
        };

        // Names inside RDATA may be pointers, so trust the length field for the cursor.
        reader.Position = dataStart + dataLength;
        record.Class = @class;
        record.TtlSeconds = ttl;
        record.CacheFlush = cacheFlush;
        return record;
    }

    private static SrvRecord ReadSrv(DnsBufferReader reader, string name)
    {
        var priority = reader.ReadUInt16();
        var weight = reader.ReadUInt16();
        var port = reader.ReadUInt16();
        var target = reader.ReadName();
        return new SrvRecord(name, priority, weight, port, target);
    }

    private static TxtRecord ReadTxt(DnsBufferReader reader, string name, int dataLength)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end = reader.Position + dataLength;

        while (reader.Position < end)
        {
            var entry = reader.ReadCharacterString();
            if (entry.Length == 0) continue;
            var separator = entry.IndexOf('=');
            if (separator < 0) values[entry] = string.Empty;
            else values[entry[..separator]] = entry[(separator + 1)..];
        }

        return new TxtRecord(name, values);
    }

    public override string ToString() => $"{Name} {Type} ttl={TtlSeconds}{(CacheFlush ? " cf" : string.Empty)}";
}

/// <summary>IPv4 address record.</summary>
public sealed class ARecord : DnsRecord
{
    public ARecord(string name, System.Net.IPAddress address) : base(name) => Address = address;

    public System.Net.IPAddress Address { get; }

    public override DnsRecordType Type => DnsRecordType.A;

    internal override void WriteData(DnsBufferWriter writer) => writer.WriteBytes(Address.GetAddressBytes());

    public override string ToString() => $"{base.ToString()} {Address}";
}

/// <summary>DNS-SD pointer record: service type (or instance) to instance/target.</summary>
public sealed class PtrRecord : DnsRecord
{
    public PtrRecord(string name, string target) : base(name) => Target = target;

    public string Target { get; }

    public override DnsRecordType Type => DnsRecordType.Ptr;

    internal override void WriteData(DnsBufferWriter writer) => writer.WriteName(Target);

    public override string ToString() => $"{base.ToString()} -> {Target}";
}

/// <summary>Service location record: host name and port of a service instance.</summary>
public sealed class SrvRecord : DnsRecord
{
    public SrvRecord(string name, ushort priority, ushort weight, ushort port, string target) : base(name)
    {
        Priority = priority;
        Weight = weight;
        Port = port;
        Target = target;
    }

    public ushort Priority { get; }

    public ushort Weight { get; }

    public ushort Port { get; }

    public string Target { get; }

    public override DnsRecordType Type => DnsRecordType.Srv;

    internal override void WriteData(DnsBufferWriter writer)
    {
        writer.WriteUInt16(Priority);
        writer.WriteUInt16(Weight);
        writer.WriteUInt16(Port);
        writer.WriteName(Target);
    }

    public override string ToString() => $"{base.ToString()} {Target}:{Port} prio={Priority} weight={Weight}";
}

/// <summary>Key/value attributes of a service instance.</summary>
public sealed class TxtRecord : DnsRecord
{
    public TxtRecord(string name, IReadOnlyDictionary<string, string>? values = null) : base(name)
    {
        Values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, string> Values { get; }

    public string? this[string key] => Values.TryGetValue(key, out var value) ? value : null;

    public override DnsRecordType Type => DnsRecordType.Txt;

    /// <summary>Reads a boolean-ish TXT value ("1", "true", "yes").</summary>
    public bool GetBool(string key, bool fallback = false)
    {
        var value = this[key];
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    internal override void WriteData(DnsBufferWriter writer)
    {
        if (Values.Count == 0)
        {
            // RDATA of a TXT record must contain at least one (possibly empty) character-string.
            writer.WriteByte(0);
            return;
        }

        foreach (var (key, value) in Values)
        {
            var entry = string.IsNullOrEmpty(value) ? key : $"{key}={value}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255) bytes = bytes[..255];
            writer.WriteByte((byte)bytes.Length);
            writer.WriteBytes(bytes);
        }
    }

    public override string ToString() =>
        $"{base.ToString()} {string.Join(", ", Values.Select(kv => $"{kv.Key}={kv.Value}"))}";
}

/// <summary>Any record type this stack does not model (kept so callers can ignore it safely).</summary>
public sealed class UnknownRecord : DnsRecord
{
    public UnknownRecord(string name, DnsRecordType type, byte[] data) : base(name)
    {
        Type = type;
        Data = data;
    }

    public override DnsRecordType Type { get; }

    public byte[] Data { get; }

    internal override void WriteData(DnsBufferWriter writer) => writer.WriteBytes(Data);
}
