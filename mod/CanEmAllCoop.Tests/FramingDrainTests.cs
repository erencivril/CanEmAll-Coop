using System;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class FramingDrainTests
{
    [Fact]
    public void HasCompleteFrameTracksPartialAndWholeFrames()
    {
        var d = new StreamFrameDecoder();
        Assert.False(d.HasCompleteFrame);

        var frame = Framing.EncodeFrame(new byte[] { 1, 2, 3, 4, 5 }, 5);
        d.Append(frame, 0, 3);                       // part of the length prefix
        Assert.False(d.HasCompleteFrame);
        d.Append(frame, 3, 4);                       // prefix + 3 payload bytes
        Assert.False(d.HasCompleteFrame);
        d.Append(frame, 7, frame.Length - 7);
        Assert.True(d.HasCompleteFrame);

        Assert.True(d.TryRead(out var got));
        Assert.Equal(5, got.Length);
        Assert.False(d.HasCompleteFrame);
    }

    [Fact]
    public void FramesStayReadableAfterTheyWereBufferedAcrossSeveralCalls()
    {
        // A bounded per-frame drain leaves frames in the decoder; every one of them must still come out, in order.
        var d = new StreamFrameDecoder();
        for (byte i = 0; i < 100; i++)
        {
            var f = Framing.EncodeFrame(new byte[] { i, i }, 2);
            d.Append(f, 0, f.Length);
        }
        int read = 0;
        while (d.HasCompleteFrame)
        {
            // read in batches of 32 like the transport does per frame
            for (int k = 0; k < 32 && d.TryRead(out var fr); k++)
            {
                Assert.Equal((byte)read, fr[0]);
                read++;
            }
        }
        Assert.Equal(100, read);
        Assert.Equal(0, d.Buffered);
    }

    [Fact]
    public void OversizedLengthPrefixIsNeverACompleteFrame()
    {
        var d = new StreamFrameDecoder(maxFrameSize: 16);
        var bad = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0 };
        d.Append(bad, 0, bad.Length);
        Assert.False(d.HasCompleteFrame);
        Assert.False(d.TryRead(out _));
        Assert.True(d.Corrupt);
    }
}
