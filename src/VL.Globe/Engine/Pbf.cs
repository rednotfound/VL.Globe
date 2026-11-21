using System;
using System.IO;
using System.Text;

namespace VL.Globe.Engine;

// A protobuf reader over [Position, End) of Data: vector tiles (VectorTile) and glyph ranges
// (GlyphPbf). MapLibre reads both with its own small pbf reader too.
internal sealed class Pbf
{
    public readonly byte[] Data;
    public int Position;
    readonly int _end;
    public Pbf(byte[] data, int start, int end) { Data = data; Position = start; _end = end; }

    public bool Next(out int field, out int wire)
    {
        if (Position >= _end) { field = wire = 0; return false; }
        ulong key = Varint();
        field = (int)(key >> 3); wire = (int)(key & 7);
        return true;
    }

    public ulong Varint()
    {
        ulong v = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (Position >= _end) throw new InvalidDataException("truncated varint");
            byte b = Data[Position++];
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
            if (shift > 63) throw new InvalidDataException("varint too long");
        }
    }

    public (int start, int end) Sub()
    {
        int len = (int)Varint();
        int start = Position;
        Position += len;
        if (Position > _end) throw new InvalidDataException("truncated message");
        return (start, Position);
    }

    public string String() { var (s, e) = Sub(); return Encoding.UTF8.GetString(Data, s, e - s); }
    public uint Fixed32() { uint v = BitConverter.ToUInt32(Data, Position); Position += 4; return v; }
    public ulong Fixed64() { ulong v = BitConverter.ToUInt64(Data, Position); Position += 8; return v; }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case 0: Varint(); break;
            case 1: Position += 8; break;
            case 2: Sub(); break;
            case 5: Position += 4; break;
            default: throw new InvalidDataException($"wire type {wire}");
        }
    }
}
