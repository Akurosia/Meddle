using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SqPackIndexHeader
{
    public uint Size;
    public uint Version;
    public uint IndexDataOffset;
    public uint IndexDataSize;
    public fixed byte IndexDataHash[64];
    public uint DataFileCount;
    public uint SynonymDataOffset;
    public uint SynonymDataSize;
    public fixed byte SynonymDataHash[64];
    public uint EmptyBlockDataOffset;
    public uint EmptyBlockDataSize;
    public fixed byte EmptyBlockDataHash[64];
    public uint DirIndexDataOffset;
    public uint DirIndexDataSize;
    public fixed byte DirIndexDataHash[64];
    public uint IndexType;
    public fixed byte _reserved[656];
    public fixed byte self_hash[64];
}
