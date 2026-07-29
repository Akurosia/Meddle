using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Index;

[StructLayout(LayoutKind.Sequential)]
public struct IndexHashTableEntry
{
    public ulong Hash;
    public uint Data;
    private uint _padding;

    public bool IsSynonym => (Data & 0b1) == 0b1;

    public byte DataFileId => (byte)((Data & 0b1110) >> 1);

    public long Offset => ((uint)Data & ~0xF) * 0x08;
}
