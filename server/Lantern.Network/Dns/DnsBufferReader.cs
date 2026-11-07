using System.Text;

namespace Lantern.Network.Dns;

/// <summary>Big-endian reader for DNS messages with decompression pointer support.</summary>
internal sealed class DnsBufferReader
{
    private const int MaxPointerJumps = 64;

    private readonly byte[] _buffer;

    public DnsBufferReader(byte[] buffer, int start = 0)
    {
        _buffer = buffer;
        Position = start;
    }

    public int Position { get; set; }

    public int Length => _buffer.Length;

    public bool HasMore(int bytes) => Position + bytes <= _buffer.Length;

    public byte ReadByte()
    {
        Require(1);
        return _buffer[Position++];
    }

    public ushort ReadUInt16()
    {
        Require(2);
        var value = (ushort)((_buffer[Position] << 8) | _buffer[Position + 1]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Require(4);
        uint value = (uint)((_buffer[Position] << 24) |
                            (_buffer[Position + 1] << 16) |
                            (_buffer[Position + 2] << 8) |
                            _buffer[Position + 3]);
        Position += 4;
        return value;
    }

    public byte[] ReadBytes(int count)
    {
        Require(count);
        var result = _buffer.AsSpan(Position, count).ToArray();
        Position += count;
        return result;
    }

    /// <summary>Reads one character-string (length octet + bytes).</summary>
    public string ReadCharacterString()
    {
        var length = ReadByte();
        return Encoding.UTF8.GetString(ReadBytes(length));
    }

    /// <summary>Reads an <c>ip4</c> address.</summary>
    public System.Net.IPAddress ReadAddress()
    {
        var raw = ReadBytes(4);
        return new System.Net.IPAddress(raw);
    }

    /// <summary>Reads a dotted name, following compression pointers.</summary>
    public string ReadName()
    {
        var builder = new StringBuilder(64);
        var jumps = 0;
        var position = Position;
        var end = -1;

        while (true)
        {
            if (position >= _buffer.Length) throw new DnsFormatException("Truncated DNS name.");

            var length = _buffer[position];

            if (length == 0)
            {
                position++;
                break;
            }

            // Top two bits set = pointer to an earlier offset.
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= _buffer.Length) throw new DnsFormatException("Truncated DNS pointer.");
                var pointer = ((length & 0x3F) << 8) | _buffer[position + 1];
                if (end < 0) end = position + 2;
                if (++jumps > MaxPointerJumps) throw new DnsFormatException("DNS name pointer loop.");
                if (pointer >= _buffer.Length) throw new DnsFormatException("DNS pointer past end of message.");
                position = pointer;
                continue;
            }

            if ((length & 0xC0) != 0) throw new DnsFormatException($"Unsupported DNS label length octet 0x{length:X2}.");
            if (position + 1 + length > _buffer.Length) throw new DnsFormatException("Truncated DNS label.");

            if (builder.Length > 0) builder.Append('.');
            builder.Append(Encoding.UTF8.GetString(_buffer, position + 1, length));
            position += 1 + length;
        }

        Position = end < 0 ? position : end;
        return builder.ToString();
    }

    private void Require(int bytes)
    {
        if (!HasMore(bytes)) throw new DnsFormatException("Truncated DNS message.");
    }
}
