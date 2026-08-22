using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Standard;

[StructLayout(LayoutKind.Sequential)]
public struct DatStdFileBlockInfos
{
    public uint Offset;
    public ushort CompressedSize;
    public ushort UncompressedSize;
};
