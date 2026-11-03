using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CanEmAllCoop.Net;

/// <summary>
/// Little-endian binary writer. Pure C# (no Unity / BepInEx) so it is unit-tested.
/// Strings: int32 byte length (-1 = null) + UTF-8. Blobs: int32 length (-1 = null) + raw bytes.
/// Write(x) overloads and the named WriteX variants do the same thing; use whichever reads better.
/// </summary>
public sealed class NetWriter
{
    byte[] _buf;
    int _len;

    public NetWriter(int capacity = 256) => _buf = new byte[Math.Max(16, capacity)];

    public int Length => _len;
    /// <summary>Backing array; only the first Length bytes are valid. Do not keep it across writes.</summary>
    public byte[] Buffer => _buf;

    public void Reset() => _len = 0;
    public byte[] ToArray() { var r = new byte[_len]; Array.Copy(_buf, r, _len); return r; }
    public ReadOnlySpan<byte> AsSpan() => new ReadOnlySpan<byte>(_buf, 0, _len);

    Span<byte> Take(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        var s = new Span<byte>(_buf, _len, n);
        _len += n;
        return s;
    }

    public void Write(bool v) => Take(1)[0] = v ? (byte)1 : (byte)0;
    public void Write(byte v) => Take(1)[0] = v;
    public void Write(sbyte v) => Take(1)[0] = (byte)v;
    public void Write(short v) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), v);
    public void Write(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void Write(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
    public void Write(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void Write(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
    public void Write(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
    public void Write(float v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), BitConverter.SingleToInt32Bits(v));
    public void Write(double v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), BitConverter.DoubleToInt64Bits(v));

    public void Write(string v)
    {
        if (v == null) { Write(-1); return; }
        int n = Encoding.UTF8.GetByteCount(v);
        Write(n);
        Encoding.UTF8.GetBytes(v, Take(n));
    }

    public void WriteBool(bool v) => Write(v);
    public void WriteByte(byte v) => Write(v);
    public void WriteShort(short v) => Write(v);
    public void WriteUShort(ushort v) => Write(v);
    public void WriteInt(int v) => Write(v);
    public void WriteUInt(uint v) => Write(v);
    public void WriteLong(long v) => Write(v);
    public void WriteULong(ulong v) => Write(v);
    public void WriteFloat(float v) => Write(v);
    public void WriteDouble(double v) => Write(v);
    public void WriteString(string v) => Write(v);

    /// <summary>Raw bytes, no length prefix.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data) => data.CopyTo(Take(data.Length));
    public void WriteBytes(byte[] data, int offset, int count) => WriteBytes(new ReadOnlySpan<byte>(data, offset, count));

    /// <summary>Length-prefixed bytes (int32, -1 = null).</summary>
    public void WriteBlob(byte[] data)
    {
        if (data == null) { Write(-1); return; }
        Write(data.Length);
        WriteBytes(data, 0, data.Length);
    }

    /// <summary>Maps v in [min,max] to 16 bits (clamped). Resolution (max-min)/65535.</summary>
    public void WriteQuantized(float v, float min, float max) => Write(Quantize.ToUShort(v, min, max));
}

/// <summary>Little-endian reader over a byte range. Reads past the end throw EndOfStreamException.</summary>
public sealed class NetReader
{
    readonly byte[] _buf;
    readonly int _start;
    readonly int _end;
    int _pos;

    public NetReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }

    public NetReader(byte[] data, int offset, int count)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (offset < 0 || count < 0 || offset + count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _buf = data; _start = offset; _pos = offset; _end = offset + count;
    }

    /// <summary>Bytes consumed so far.</summary>
    public int Position => _pos - _start;
    public int Length => _end - _start;
    public int Remaining => _end - _pos;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _end) throw new EndOfStreamException($"NetReader underrun: need {n}, have {_end - _pos}");
        var s = new ReadOnlySpan<byte>(_buf, _pos, n);
        _pos += n;
        return s;
    }

    public bool ReadBool() => Take(1)[0] != 0;
    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];
    public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadULong() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadFloat() => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Take(4)));
    public double ReadDouble() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));

    /// <summary>Strings above this size are rejected (corrupt or hostile length prefix).</summary>
    public const int MaxStringBytes = 32 * 1024 * 1024;

    public string ReadString()
    {
        int n = ReadInt();
        if (n < 0) return null;
        if (n > MaxStringBytes) throw new InvalidDataException($"string length {n} too large");
        return Encoding.UTF8.GetString(Take(n));
    }

    public byte[] ReadBytes(int count) => Take(count).ToArray();
    public byte[] ReadBytesToEnd() => Take(Remaining).ToArray();

    public byte[] ReadBlob()
    {
        int n = ReadInt();
        if (n < 0) return null;
        if (n > MaxStringBytes) throw new InvalidDataException($"blob length {n} too large");
        return ReadBytes(n);
    }

    public void Skip(int count) => Take(count);

    public float ReadQuantized(float min, float max) => Quantize.FromUShort(ReadUShort(), min, max);

    // BinaryReader-style aliases
    public bool ReadBoolean() => ReadBool();
    public short ReadInt16() => ReadShort();
    public ushort ReadUInt16() => ReadUShort();
    public int ReadInt32() => ReadInt();
    public uint ReadUInt32() => ReadUInt();
    public long ReadInt64() => ReadLong();
    public ulong ReadUInt64() => ReadULong();
    public float ReadSingle() => ReadFloat();
}

/// <summary>16-bit range quantization used by the Unity helpers in NetUnity.cs.</summary>
public static class Quantize
{
    public static ushort ToUShort(float v, float min, float max)
    {
        if (!(max > min)) return 0;
        if (float.IsNaN(v)) v = min;
        float t = (v - min) / (max - min);
        if (t <= 0f) return 0;
        if (t >= 1f) return ushort.MaxValue;
        return (ushort)(t * 65535f + 0.5f);
    }

    public static float FromUShort(ushort q, float min, float max) => min + q / 65535f * (max - min);
}
