using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace CanEmAllCoop.Net;

/// <summary>
/// Splits packets larger than MaxPacketSize into Fragment packets and builds length-prefixed stream frames.
/// Fragment packet: ushort header (CoreMsg.Fragment) | ushort transferId | ushort index | ushort count | chunk.
/// Fragments of one transfer must arrive in order (reliable ordered transports guarantee that).
/// </summary>
public static class Framing
{
    /// <summary>Largest packet sent unfragmented, and the chunk size of fragments. A fragment packet is 8 bytes larger.</summary>
    public const int MaxPacketSize = 60000;
    public const int FragmentHeaderSize = PacketHeader.Size + 6;
    /// <summary>Largest reassembled packet accepted (snapshot JSON is far below this).</summary>
    public const int MaxAssembledSize = 32 * 1024 * 1024;

    public static bool NeedsSplit(int packetLength) => packetLength > MaxPacketSize;

    public static List<byte[]> Split(byte[] packet, int length, ushort transferId, int chunkSize = MaxPacketSize)
    {
        if (packet == null) throw new ArgumentNullException(nameof(packet));
        if (length < 0 || length > packet.Length) throw new ArgumentOutOfRangeException(nameof(length));
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        int count = Math.Max(1, (length + chunkSize - 1) / chunkSize);
        if (count > ushort.MaxValue) throw new ArgumentException("packet too large to fragment", nameof(length));

        var result = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int off = i * chunkSize;
            int n = Math.Min(chunkSize, length - off);
            var frag = new byte[FragmentHeaderSize + n];
            BinaryPrimitives.WriteUInt16LittleEndian(frag.AsSpan(0), CoreMsg.Fragment);
            BinaryPrimitives.WriteUInt16LittleEndian(frag.AsSpan(2), transferId);
            BinaryPrimitives.WriteUInt16LittleEndian(frag.AsSpan(4), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(frag.AsSpan(6), (ushort)count);
            Buffer.BlockCopy(packet, off, frag, FragmentHeaderSize, n);
            result.Add(frag);
        }
        return result;
    }

    /// <summary>Wraps a packet into a stream frame: uint32 little-endian length + packet.</summary>
    public static byte[] EncodeFrame(byte[] packet, int length)
    {
        var frame = new byte[4 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length);
        Buffer.BlockCopy(packet, 0, frame, 4, length);
        return frame;
    }
}

/// <summary>Per-peer reassembly of Fragment packets.</summary>
public sealed class FragmentAssembler
{
    const int MaxConcurrentTransfers = 4;

    sealed class Transfer
    {
        public ushort Id;
        public int Count;
        public int Next;
        public int Total;
        public readonly List<byte[]> Chunks = new();
    }

    readonly List<Transfer> _active = new();

    /// <summary>Fragments that were discarded (out of order, oversized, malformed).</summary>
    public int Dropped { get; private set; }
    public int PendingTransfers => _active.Count;

    public void Clear() => _active.Clear();

    /// <summary>Feed one complete Fragment packet (including its 2-byte header). True when a packet was completed.</summary>
    public bool Accept(byte[] fragmentPacket, out byte[] complete)
    {
        complete = null;
        if (fragmentPacket == null || fragmentPacket.Length < Framing.FragmentHeaderSize) { Dropped++; return false; }

        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(fragmentPacket.AsSpan(2));
        ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(fragmentPacket.AsSpan(4));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(fragmentPacket.AsSpan(6));
        int chunkLen = fragmentPacket.Length - Framing.FragmentHeaderSize;
        if (count == 0 || idx >= count) { Dropped++; return false; }

        Transfer t = Find(tid);
        if (idx == 0)
        {
            if (t != null) _active.Remove(t);
            if (_active.Count >= MaxConcurrentTransfers) { _active.RemoveAt(0); Dropped++; }
            t = new Transfer { Id = tid, Count = count };
            _active.Add(t);
        }
        else if (t == null || t.Next != idx || t.Count != count)
        {
            if (t != null) _active.Remove(t);
            Dropped++;
            return false;
        }

        t.Total += chunkLen;
        if (t.Total > Framing.MaxAssembledSize)
        {
            _active.Remove(t);
            Dropped++;
            return false;
        }

        var chunk = new byte[chunkLen];
        Buffer.BlockCopy(fragmentPacket, Framing.FragmentHeaderSize, chunk, 0, chunkLen);
        t.Chunks.Add(chunk);
        t.Next++;
        if (t.Next < t.Count) return false;

        _active.Remove(t);
        complete = new byte[t.Total];
        int pos = 0;
        foreach (var c in t.Chunks)
        {
            Buffer.BlockCopy(c, 0, complete, pos, c.Length);
            pos += c.Length;
        }
        return true;
    }

    Transfer Find(ushort id)
    {
        foreach (var t in _active) if (t.Id == id) return t;
        return null;
    }
}

/// <summary>Incremental decoder for uint32-length-prefixed frames on a TCP byte stream.</summary>
public sealed class StreamFrameDecoder
{
    readonly int _maxFrame;
    byte[] _buf = new byte[4096];
    int _head;
    int _tail;

    public StreamFrameDecoder(int maxFrameSize = 4 * 1024 * 1024) => _maxFrame = maxFrameSize;

    /// <summary>A length prefix exceeded the limit: the stream is garbage and the connection must be dropped.</summary>
    public bool Corrupt { get; private set; }
    public int Buffered => _tail - _head;

    /// <summary>True when at least one whole frame is waiting to be read.</summary>
    public bool HasCompleteFrame
    {
        get
        {
            if (Corrupt || _tail - _head < 4) return false;
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_head));
            return len <= (uint)_maxFrame && _tail - _head >= 4 + (int)len;
        }
    }

    public void Append(byte[] data, int offset, int count)
    {
        if (count <= 0 || Corrupt) return;
        if (_tail + count > _buf.Length)
        {
            int live = _tail - _head;
            if (live + count <= _buf.Length)
            {
                Buffer.BlockCopy(_buf, _head, _buf, 0, live);
            }
            else
            {
                var bigger = new byte[Math.Max(_buf.Length * 2, live + count)];
                Buffer.BlockCopy(_buf, _head, bigger, 0, live);
                _buf = bigger;
            }
            _head = 0;
            _tail = live;
        }
        Buffer.BlockCopy(data, offset, _buf, _tail, count);
        _tail += count;
    }

    public bool TryRead(out byte[] frame)
    {
        frame = null;
        if (Corrupt || _tail - _head < 4) return false;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_head));
        if (len > (uint)_maxFrame) { Corrupt = true; return false; }
        if (_tail - _head < 4 + (int)len) return false;
        frame = new byte[len];
        Buffer.BlockCopy(_buf, _head + 4, frame, 0, (int)len);
        _head += 4 + (int)len;
        if (_head == _tail) _head = _tail = 0;
        return true;
    }
}
