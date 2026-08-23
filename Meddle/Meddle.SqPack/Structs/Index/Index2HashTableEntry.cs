using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Index;

[StructLayout(LayoutKind.Sequential)]
public struct Index2HashTableEntry
{
    public uint Hash;
    public uint Data;

    public bool IsSynonym => (Data & 0b1) == 0b1;

    public byte DataFileId => (byte)((Data & 0b1110) >> 1);

    public long Offset => ((uint)Data & ~0xF) * 0x08;
}
