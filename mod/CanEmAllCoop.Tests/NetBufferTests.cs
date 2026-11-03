using System;
using System.IO;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class NetBufferTests
{
    [Fact]
    public void RoundTripsEveryPrimitive()
    {
        var w = new NetWriter();
        w.Write(true);
        w.Write(false);
        w.Write((byte)200);
        w.Write((sbyte)-5);
        w.Write((short)-12345);
        w.Write((ushort)54321);
        w.Write(int.MinValue);
        w.Write(uint.MaxValue);
        w.Write(long.MinValue);
        w.Write(ulong.MaxValue);
        w.Write(3.25f);
        w.Write(-1234.5678901234);
        w.Write("hello");

        var r = new NetReader(w.ToArray());
        Assert.True(r.ReadBool());
        Assert.False(r.ReadBool());
        Assert.Equal((byte)200, r.ReadByte());
        Assert.Equal((sbyte)-5, r.ReadSByte());
        Assert.Equal((short)-12345, r.ReadShort());
        Assert.Equal((ushort)54321, r.ReadUShort());
        Assert.Equal(int.MinValue, r.ReadInt());
        Assert.Equal(uint.MaxValue, r.ReadUInt());
        Assert.Equal(long.MinValue, r.ReadLong());
        Assert.Equal(ulong.MaxValue, r.ReadULong());
        Assert.Equal(3.25f, r.ReadFloat());
        Assert.Equal(-1234.5678901234, r.ReadDouble());
        Assert.Equal("hello", r.ReadString());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void NamedWritersMatchOverloads()
    {
        var a = new NetWriter();
        a.Write(true); a.Write((byte)1); a.Write((short)2); a.Write((ushort)3); a.Write(4); a.Write(5u); a.Write(6L); a.Write(7UL); a.Write(8f); a.Write("x");
        var b = new NetWriter();
        b.WriteBool(true); b.WriteByte(1); b.WriteShort(2); b.WriteUShort(3); b.WriteInt(4); b.WriteUInt(5u); b.WriteLong(6L); b.WriteULong(7UL); b.WriteFloat(8f); b.WriteString("x");
        Assert.Equal(a.ToArray(), b.ToArray());

        var r = new NetReader(a.ToArray());
        Assert.True(r.ReadBoolean());
        Assert.Equal((byte)1, r.ReadByte());
        Assert.Equal((short)2, r.ReadInt16());
        Assert.Equal((ushort)3, r.ReadUInt16());
        Assert.Equal(4, r.ReadInt32());
        Assert.Equal(5u, r.ReadUInt32());
        Assert.Equal(6L, r.ReadInt64());
        Assert.Equal(7UL, r.ReadUInt64());
        Assert.Equal(8f, r.ReadSingle());
    }

    [Fact]
    public void IsLittleEndian()
    {
        var w = new NetWriter();
        w.Write(0x01020304);
        w.Write((ushort)0xA1B2);
        Assert.Equal(new byte[] { 4, 3, 2, 1, 0xB2, 0xA1 }, w.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("zażółć gęślą jaźń")]
    [InlineData("ünïcödé 日本語 \U0001F600")]
    public void StringsRoundTrip(string s)
    {
        var w = new NetWriter();
        w.Write(s);
        w.Write(42);
        var r = new NetReader(w.ToArray());
        Assert.Equal(s, r.ReadString());
        Assert.Equal(42, r.ReadInt());
    }

    [Fact]
    public void NullStringAndBlobSurvive()
    {
        var w = new NetWriter();
        w.Write((string)null);
        w.WriteBlob(null);
        w.WriteBlob(new byte[] { 1, 2, 3 });
        w.WriteBlob(new byte[0]);
        var r = new NetReader(w.ToArray());
        Assert.Null(r.ReadString());
        Assert.Null(r.ReadBlob());
        Assert.Equal(new byte[] { 1, 2, 3 }, r.ReadBlob());
        Assert.Empty(r.ReadBlob());
    }

    [Fact]
    public void LargeStringGrowsBuffer()
    {
        var big = new string('x', 300_000);
        var w = new NetWriter(16);
        w.Write(big);
        w.Write(7);
        var r = new NetReader(w.ToArray());
        Assert.Equal(big, r.ReadString());
        Assert.Equal(7, r.ReadInt());
    }

    [Fact]
    public void ReaderUnderrunThrows()
    {
        var r = new NetReader(new byte[] { 1, 2, 3 });
        Assert.Throws<EndOfStreamException>(() => r.ReadInt());
        Assert.Equal(3, r.Remaining); // a failed read does not consume
        Assert.Equal((byte)1, r.ReadByte());
    }

    [Fact]
    public void StringLengthBeyondBufferThrows()
    {
        var w = new NetWriter();
        w.Write(1000);   // claims 1000 bytes
        w.Write((byte)1);
        var r = new NetReader(w.ToArray());
        Assert.Throws<EndOfStreamException>(() => r.ReadString());
    }

    [Fact]
    public void AbsurdStringLengthIsRejected()
    {
        var w = new NetWriter();
        w.Write(int.MaxValue);
        var r = new NetReader(w.ToArray());
        Assert.Throws<InvalidDataException>(() => r.ReadString());
    }

    [Fact]
    public void ReaderRespectsOffsetAndCount()
    {
        var data = new byte[] { 9, 9, 1, 0, 0, 0, 9, 9 };
        var r = new NetReader(data, 2, 4);
        Assert.Equal(4, r.Length);
        Assert.Equal(1, r.ReadInt());
        Assert.Equal(4, r.Position);
        Assert.Equal(0, r.Remaining);
        Assert.Throws<EndOfStreamException>(() => r.ReadByte());
    }

    [Fact]
    public void ReaderRejectsBadRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetReader(new byte[4], 2, 4));
        Assert.Throws<ArgumentNullException>(() => new NetReader(null));
    }

    [Fact]
    public void ResetReusesWriter()
    {
        var w = new NetWriter();
        w.Write(123456789L);
        w.Reset();
        Assert.Equal(0, w.Length);
        w.Write((byte)7);
        Assert.Equal(new byte[] { 7 }, w.ToArray());
    }

    [Fact]
    public void RawBytesHaveNoPrefix()
    {
        var w = new NetWriter();
        w.WriteBytes(new byte[] { 1, 2, 3, 4, 5 }, 1, 3);
        Assert.Equal(new byte[] { 2, 3, 4 }, w.ToArray());
        var r = new NetReader(w.ToArray());
        r.Skip(1);
        Assert.Equal(new byte[] { 3, 4 }, r.ReadBytesToEnd());
    }

    [Fact]
    public void QuantizationRoundTripsWithinResolution()
    {
        var w = new NetWriter();
        float[] values = { -512f, -100.123f, 0f, 0.5f, 77.77f, 512f };
        foreach (var v in values) w.WriteQuantized(v, -512f, 512f);
        var r = new NetReader(w.ToArray());
        float step = 1024f / 65535f;
        foreach (var v in values)
            Assert.InRange(Math.Abs(r.ReadQuantized(-512f, 512f) - v), 0f, step);
    }

    [Fact]
    public void QuantizationClampsAndHandlesNaN()
    {
        Assert.Equal(0, Quantize.ToUShort(-9999f, -1f, 1f));
        Assert.Equal(ushort.MaxValue, Quantize.ToUShort(9999f, -1f, 1f));
        Assert.Equal(Quantize.ToUShort(-1f, -1f, 1f), Quantize.ToUShort(float.NaN, -1f, 1f));
        Assert.Equal(0, Quantize.ToUShort(5f, 1f, 1f)); // degenerate range
    }
}
