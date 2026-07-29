using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SqPackHeader
{
    public fixed byte magic[8];
    public byte platformId;
    public fixed byte __unknown[3];
    public uint size;
    public uint version;
    public uint type;
}
