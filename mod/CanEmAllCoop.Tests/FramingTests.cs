using System;
using System.Collections.Generic;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class FramingTests
{
    static byte[] Packet(int length, int seed = 1)
    {
        var rnd = new Random(seed);
        var p = new byte[length];
        rnd.NextBytes(p);
        return p;
    }

    static byte[] Reassemble(FragmentAssembler asm, List<byte[]> frags)
    {
        byte[] result = null;
        for (int i = 0; i < frags.Count; i++)
        {
            bool done = asm.Accept(frags[i], out var complete);
            if (i < frags.Count - 1) Assert.False(done);
            else { Assert.True(done); result = complete; }
        }
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59_999)]
    [InlineData(60_000)]
    [InlineData(60_001)]
    [InlineData(120_000)]
    [InlineData(120_001)]
    [InlineData(250_000)]
    [InlineData(1_000_000)]
    public void SplitThenAssembleRestoresPacket(int length)
    {
        var packet = Packet(length);
        var frags = Framing.Split(packet, packet.Length, 7);

        Assert.Equal(Math.Max(1, (length + Framing.MaxPacketSize - 1) / Framing.MaxPacketSize), frags.Count);
        foreach (var f in frags) Assert.True(f.Length <= 64 * 1024);

        var asm = new FragmentAssembler();
        Assert.Equal(packet, Reassemble(asm, frags));
        Assert.Equal(0, asm.PendingTransfers);
        Assert.Equal(0, asm.Dropped);
    }

    [Fact]
    public void NeedsSplitBoundary()
    {
        Assert.False(Framing.NeedsSplit(Framing.MaxPacketSize));
        Assert.True(Framing.NeedsSplit(Framing.MaxPacketSize + 1));
    }

    [Fact]
    public void SplitUsesOnlyLengthBytesOfBuffer()
    {
        var buffer = Packet(1000);
        var frags = Framing.Split(buffer, 300, 1, chunkSize: 100);
        Assert.Equal(3, frags.Count);
        var expected = new byte[300];
        Array.Copy(buffer, expected, 300);
        Assert.Equal(expected, Reassemble(new FragmentAssembler(), frags));
    }

    [Fact]
    public void FragmentHeaderIsFragmentMessage()
    {
        var frags = Framing.Split(Packet(10), 10, 0x1234, chunkSize: 4);
        Assert.Equal(3, frags.Count);
        Assert.Equal(CoreMsg.Fragment, BitConverter.ToUInt16(frags[0], 0));
        Assert.Equal(0x1234, BitConverter.ToUInt16(frags[0], 2));
        Assert.Equal(2, BitConverter.ToUInt16(frags[2], 4)); // index of last fragment
        Assert.Equal(3, BitConverter.ToUInt16(frags[2], 6)); // count
    }

    [Fact]
    public void OutOfOrderFragmentDropsTransfer()
    {
        var frags = Framing.Split(Packet(1000), 1000, 1, chunkSize: 100);
        var asm = new FragmentAssembler();
        Assert.False(asm.Accept(frags[0], out _));
        Assert.False(asm.Accept(frags[2], out _)); // skipped #1
        Assert.True(asm.Dropped > 0);
        Assert.Equal(0, asm.PendingTransfers);
        for (int i = 3; i < frags.Count; i++) Assert.False(asm.Accept(frags[i], out _)); // rest never completes
    }

    [Fact]
    public void LostFirstFragmentNeverCompletes()
    {
        var frags = Framing.Split(Packet(500), 500, 9, chunkSize: 100);
        var asm = new FragmentAssembler();
        for (int i = 1; i < frags.Count; i++) Assert.False(asm.Accept(frags[i], out _));
        Assert.Equal(0, asm.PendingTransfers);
    }

    [Fact]
    public void NewTransferWithSameIdRestarts()
    {
        var a = Framing.Split(Packet(500, 1), 500, 5, chunkSize: 100);
        var b = Packet(300, 2);
        var bf = Framing.Split(b, b.Length, 5, chunkSize: 100);
        var asm = new FragmentAssembler();
        asm.Accept(a[0], out _);
        asm.Accept(a[1], out _);
        Assert.Equal(b, Reassemble(asm, bf));
    }

    [Fact]
    public void InterleavedTransfersAssembleIndependently()
    {
        var pa = Packet(450, 1);
        var pb = Packet(350, 2);
        var fa = Framing.Split(pa, pa.Length, 1, chunkSize: 100);
        var fb = Framing.Split(pb, pb.Length, 2, chunkSize: 100);
        var asm = new FragmentAssembler();
        byte[] ra = null, rb = null;
        for (int i = 0; i < Math.Max(fa.Count, fb.Count); i++)
        {
            if (i < fa.Count && asm.Accept(fa[i], out var x)) ra = x;
            if (i < fb.Count && asm.Accept(fb[i], out var y)) rb = y;
        }
        Assert.Equal(pa, ra);
        Assert.Equal(pb, rb);
    }

    [Fact]
    public void AssemblersArePerPeer()
    {
        var p1 = Packet(300, 1);
        var p2 = Packet(300, 2);
        var f1 = Framing.Split(p1, 300, 1, chunkSize: 100);
        var f2 = Framing.Split(p2, 300, 1, chunkSize: 100); // same transfer id from another peer
        var peer1 = new FragmentAssembler();
        var peer2 = new FragmentAssembler();
        byte[] r1 = null, r2 = null;
        for (int i = 0; i < 3; i++)
        {
            if (peer1.Accept(f1[i], out var a)) r1 = a;
            if (peer2.Accept(f2[i], out var b)) r2 = b;
        }
        Assert.Equal(p1, r1);
        Assert.Equal(p2, r2);
    }

    [Fact]
    public void TooManyConcurrentTransfersEvictsOldest()
    {
        var asm = new FragmentAssembler();
        for (ushort id = 1; id <= 6; id++)
        {
            var f = Framing.Split(Packet(300, id), 300, id, chunkSize: 100);
            asm.Accept(f[0], out _);
        }
        Assert.True(asm.PendingTransfers <= 4);
        Assert.True(asm.Dropped >= 2);
    }

    [Fact]
    public void MalformedFragmentsAreIgnored()
    {
        var asm = new FragmentAssembler();
        Assert.False(asm.Accept(null, out _));
        Assert.False(asm.Accept(new byte[3], out _));
        var bad = Framing.Split(Packet(10), 10, 1, chunkSize: 4)[0];
        bad[6] = 0; bad[7] = 0; // count = 0
        Assert.False(asm.Accept(bad, out _));
        var bad2 = Framing.Split(Packet(10), 10, 1, chunkSize: 4)[0];
        bad2[4] = 9; // index >= count
        Assert.False(asm.Accept(bad2, out _));
        Assert.Equal(4, asm.Dropped);
    }

    [Fact]
    public void ClearForgetsPartialTransfers()
    {
        var asm = new FragmentAssembler();
        asm.Accept(Framing.Split(Packet(300), 300, 1, chunkSize: 100)[0], out _);
        Assert.Equal(1, asm.PendingTransfers);
        asm.Clear();
        Assert.Equal(0, asm.PendingTransfers);
    }

    // ---- stream framing (TCP)

    [Fact]
    public void StreamDecoderHandlesByteByByteFeed()
    {
        var p1 = Packet(70, 1);
        var p2 = Packet(5, 2);
        var stream = Concat(Framing.EncodeFrame(p1, p1.Length), Framing.EncodeFrame(p2, p2.Length));
        var dec = new StreamFrameDecoder();
        var got = new List<byte[]>();
        foreach (var b in stream)
        {
            dec.Append(new[] { b }, 0, 1);
            while (dec.TryRead(out var f)) got.Add(f);
        }
        Assert.Equal(2, got.Count);
        Assert.Equal(p1, got[0]);
        Assert.Equal(p2, got[1]);
        Assert.Equal(0, dec.Buffered);
    }

    [Fact]
    public void StreamDecoderHandlesManyFramesInOneChunkAndEmptyFrames()
    {
        var parts = new List<byte[]>();
        var expected = new List<byte[]>();
        for (int i = 0; i < 50; i++)
        {
            var p = Packet(i * 7, i);
            expected.Add(p);
            parts.Add(Framing.EncodeFrame(p, p.Length));
        }
        var stream = Concat(parts.ToArray());
        var dec = new StreamFrameDecoder();
        dec.Append(stream, 0, stream.Length);
        for (int i = 0; i < 50; i++)
        {
            Assert.True(dec.TryRead(out var f));
            Assert.Equal(expected[i], f);
        }
        Assert.False(dec.TryRead(out _));
    }

    [Fact]
    public void StreamDecoderWaitsForRestOfFrameAndReusesBuffer()
    {
        var big = Packet(10_000);
        var enc = Framing.EncodeFrame(big, big.Length);
        var dec = new StreamFrameDecoder();
        for (int round = 0; round < 20; round++)
        {
            dec.Append(enc, 0, 5000);
            Assert.False(dec.TryRead(out _));
            dec.Append(enc, 5000, enc.Length - 5000);
            Assert.True(dec.TryRead(out var f));
            Assert.Equal(big, f);
        }
    }

    [Fact]
    public void StreamDecoderFlagsOversizedFrame()
    {
        var dec = new StreamFrameDecoder(maxFrameSize: 100);
        var enc = Framing.EncodeFrame(Packet(101), 101);
        dec.Append(enc, 0, enc.Length);
        Assert.False(dec.TryRead(out _));
        Assert.True(dec.Corrupt);
    }

    [Fact]
    public void FragmentsSurviveStreamFraming()
    {
        var packet = Packet(200_000);
        var frags = Framing.Split(packet, packet.Length, 3);
        var dec = new StreamFrameDecoder();
        var asm = new FragmentAssembler();
        byte[] result = null;
        foreach (var f in frags)
        {
            var frame = Framing.EncodeFrame(f, f.Length);
            for (int off = 0; off < frame.Length; off += 1000) // deliver in awkward 1000-byte slices
            {
                dec.Append(frame, off, Math.Min(1000, frame.Length - off));
                while (dec.TryRead(out var got))
                    if (asm.Accept(got, out var done)) result = done;
            }
        }
        Assert.Equal(packet, result);
    }

    // ---- packet header

    [Fact]
    public void HeaderFlagsRoundTrip()
    {
        ushort h = PacketHeader.Make(699, reliable: false, relay: true);
        Assert.Equal(699, PacketHeader.Id(h));
        Assert.True(PacketHeader.IsUnreliable(h));
        Assert.True(PacketHeader.IsRelay(h));
        Assert.False(PacketHeader.IsDirected(h));

        h = PacketHeader.Make(100, reliable: true, directed: true);
        Assert.Equal(100, PacketHeader.Id(h));
        Assert.False(PacketHeader.IsUnreliable(h));
        Assert.True(PacketHeader.IsDirected(h));
    }

    [Fact]
    public void UserMessageIdsFitTheHeader()
    {
        for (ushort id = CoreMsg.FirstUserId; id < 700; id++) // module ranges end at 699
            Assert.Equal(id, PacketHeader.Id(PacketHeader.Make(id, false, true, true)));
    }

    static byte[] Concat(params byte[][] parts)
    {
        int n = 0;
        foreach (var p in parts) n += p.Length;
        var r = new byte[n];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}
