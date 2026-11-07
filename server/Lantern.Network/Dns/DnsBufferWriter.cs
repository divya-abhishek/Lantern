using System.IO;

namespace Lantern.Network.Dns;

/// <summary>
/// Big-endian writer for DNS messages. Records the offset of every name it writes so
/// later names can be replaced by a compression pointer (RFC 1035 section 4.1.4).
/// </summary>
internal sealed class DnsBufferWriter
{
    private readonly MemoryStream _stream;
    private readonly Dictionary<string, int> _nameOffsets = new(StringComparer.OrdinalIgnoreCase);

    public DnsBufferWriter(int capacity = 512)
    {
        _stream = new MemoryStream(capacity);
    }

    /// <summary>Set to false for queries: some resolvers reject pointers inside the question section.</summary>
    public bool CompressNames { get; set; } = true;

    public int Length => (int)_stream.Length;

    public long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public byte[] ToArray() => _stream.ToArray();

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)(value & 0xFF));
    }

    public void WriteUInt32(uint value)
    {
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)((value >> 16) & 0xFF));
        _stream.WriteByte((byte)((value >> 8) & 0xFF));
        _stream.WriteByte((byte)(value & 0xFF));
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    /// <summary>Overwrites a previously written 16 bit value (used to patch header counts).</summary>
    public void PatchUInt16(int offset, ushort value)
    {
        var saved = _stream.Position;
        _stream.Position = offset;
        WriteUInt16(value);
        _stream.Position = saved;
    }

    /// <summary>Writes a dotted name using pointers to the longest already written suffix.</summary>
    public void WriteName(string name)
    {
        var labels = DnsName.Split(name);

        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels[i..]);

            if (CompressNames && _nameOffsets.TryGetValue(suffix, out var pointer) && pointer < 0x4000)
            {
                WriteUInt16((ushort)(0xC000 | pointer));
                return;
            }

            if (Position < 0x4000) _nameOffsets[suffix] = (int)Position;

            var label = System.Text.Encoding.UTF8.GetBytes(labels[i]);
            if (label.Length is 0 or > DnsName.MaxLabelLength)
            {
                throw new DnsFormatException($"Invalid DNS label '{labels[i]}' (length {label.Length}).");
            }

            WriteByte((byte)label.Length);
            WriteBytes(label);
        }

        WriteByte(0);
    }

    /// <summary>Writes a name without the root label (used inside SRV/PTR RDATA).</summary>
    public void WriteNameData(string name) => WriteName(name);
}
