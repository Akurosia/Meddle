using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs;

[StructLayout(LayoutKind.Sequential)]
struct DatBlockHeader
{
    public uint Size;

    // always 0?
    public uint unknown1;
    public DatBlockType DatBlockType;
    public uint BlockDataSize;
};
