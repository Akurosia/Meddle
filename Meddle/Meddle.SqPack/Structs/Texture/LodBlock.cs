using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Texture;

[StructLayout(LayoutKind.Sequential)]
struct LodBlock
{
    public uint CompressedOffset;
    public uint CompressedSize;
    public uint DecompressedSize;
    public uint BlockOffset;
    public uint BlockCount;
}
