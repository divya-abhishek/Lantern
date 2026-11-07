namespace Lantern.Network.Dns;

/// <summary>
/// A complete DNS / mDNS message. Used both for queries ("who offers _lantern._tcp?")
/// and for responses (the records a responder knows).
/// </summary>
public sealed class DnsMessage
{
    private const int HeaderLength = 12;

    public ushort Id { get; set; }

    public DnsFlags Flags { get; set; }

    public List<DnsQuestion> Questions { get; } = [];

    public List<DnsRecord> Answers { get; } = [];

    public List<DnsRecord> Authorities { get; } = [];

    public List<DnsRecord> Additionals { get; } = [];

    public bool IsResponse => Flags.HasFlag(DnsFlags.Response);

    /// <summary>Builds a query for one name/type. mDNS queries use id 0.</summary>
    public static DnsMessage CreateQuery(string name, DnsRecordType type = DnsRecordType.Any, bool unicastResponse = true, ushort id = 0)
    {
        var message = new DnsMessage { Id = id, Flags = DnsFlags.None };
        message.Questions.Add(new DnsQuestion(name, type) { UnicastResponse = unicastResponse });
        return message;
    }

    /// <summary>Builds the authoritative response to a query (the caller fills the sections).</summary>
    public DnsMessage ToResponse()
    {
        var response = new DnsMessage
        {
            Id = Id,
            Flags = DnsFlags.Response | DnsFlags.Authoritative,
        };
        return response;
    }

    public DnsMessage AddAnswers(params IEnumerable<DnsRecord> records)
    {
        Answers.AddRange(records);
        return this;
    }

    public DnsMessage AddAdditionals(params IEnumerable<DnsRecord> records)
    {
        Additionals.AddRange(records);
        return this;
    }

    /// <summary>Serializes the message. Compression is only used for responses.</summary>
    public byte[] ToBytes(bool? compressNames = null)
    {
        var writer = new DnsBufferWriter(HeaderLength + 64)
        {
            CompressNames = compressNames ?? IsResponse,
        };

        writer.WriteUInt16(Id);
        writer.WriteUInt16((ushort)Flags);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);

        foreach (var question in Questions)
        {
            writer.WriteName(question.Name);
            writer.WriteUInt16((ushort)question.Type);
            writer.WriteUInt16((ushort)((ushort)DnsClass.Internet | (question.UnicastResponse ? 0x8000 : 0)));
        }

        var answerCount = WriteRecords(writer, Answers);
        var authorityCount = WriteRecords(writer, Authorities);
        var additionalCount = WriteRecords(writer, Additionals);

        writer.PatchUInt16(4, (ushort)Questions.Count);
        writer.PatchUInt16(6, answerCount);
        writer.PatchUInt16(8, authorityCount);
        writer.PatchUInt16(10, additionalCount);

        return writer.ToArray();
    }

    /// <summary>Parses a received datagram. Throws <see cref="DnsFormatException"/> when it is not DNS.</summary>
    public static DnsMessage Parse(byte[] datagram)
    {
        if (datagram.Length < HeaderLength) throw new DnsFormatException("Message shorter than the DNS header.");

        var reader = new DnsBufferReader(datagram);
        var message = new DnsMessage
        {
            Id = reader.ReadUInt16(),
            Flags = (DnsFlags)reader.ReadUInt16(),
        };

        var questionCount = reader.ReadUInt16();
        var answerCount = reader.ReadUInt16();
        var authorityCount = reader.ReadUInt16();
        var additionalCount = reader.ReadUInt16();

        for (var i = 0; i < questionCount; i++)
        {
            var name = reader.ReadName();
            var type = (DnsRecordType)reader.ReadUInt16();
            var rawClass = reader.ReadUInt16();
            message.Questions.Add(new DnsQuestion(name, type)
            {
                UnicastResponse = (rawClass & 0x8000) != 0,
            });
        }

        ReadRecords(reader, answerCount, message.Answers);
        ReadRecords(reader, authorityCount, message.Authorities);
        ReadRecords(reader, additionalCount, message.Additionals);
        return message;
    }

    public override string ToString() =>
        $"{(IsResponse ? "response" : "query")} id={Id} q={Questions.Count} an={Answers.Count} " +
        $"ns={Authorities.Count} ar={Additionals.Count} [{string.Join(", ", Questions)}]";

    private static ushort WriteRecords(DnsBufferWriter writer, List<DnsRecord> records)
    {
        foreach (var record in records) record.Write(writer);
        return (ushort)records.Count;
    }

    private static void ReadRecords(DnsBufferReader reader, int count, List<DnsRecord> target)
    {
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadName();
            var type = (DnsRecordType)reader.ReadUInt16();
            var rawClass = reader.ReadUInt16();
            var ttl = reader.ReadUInt32();
            var dataLength = reader.ReadUInt16();

            target.Add(DnsRecord.Read(
                reader,
                name,
                type,
                (DnsClass)(rawClass & 0x7FFF),
                cacheFlush: (rawClass & 0x8000) != 0,
                ttl,
                dataLength));
        }
    }
}
