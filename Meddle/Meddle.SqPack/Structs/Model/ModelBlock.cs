using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Model;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ModelBlock
{
    public uint Size;
    public FileType Type;
    public uint RawFileSize;
    public uint NumberOfBlocks;
    public uint UsedNumberOfBlocks;
    public uint Version;
    public uint StackSize;
    public uint RuntimeSize;
    public fixed uint VertexBufferSize[(int)LodLevel.Max];
    public fixed uint EdgeGeometryVertexBufferSize[(int)LodLevel.Max];
    public fixed uint IndexBufferSize[(int)LodLevel.Max];
    public uint CompressedStackMemorySize;
    public uint CompressedRuntimeMemorySize;
    public fixed uint CompressedVertexBufferSize[(int)LodLevel.Max];
    public fixed uint CompressedEdgeGeometryVertexBufferSize[(int)LodLevel.Max];
    public fixed uint CompressedIndexBufferSize[(int)LodLevel.Max];
    public uint StackOffset;
    public uint RuntimeOffset;
    public fixed uint VertexBufferOffset[(int)LodLevel.Max];
    public fixed uint EdgeGeometryVertexBufferOffset[(int)LodLevel.Max];
    public fixed uint IndexBufferOffset[(int)LodLevel.Max];
    public ushort StackBlockIndex;
    public ushort RuntimeBlockIndex;
    public fixed ushort VertexBufferBlockIndex[(int)LodLevel.Max];
    public fixed ushort EdgeGeometryVertexBufferBlockIndex[(int)LodLevel.Max];
    public fixed ushort IndexBufferBlockIndex[(int)LodLevel.Max];
    public ushort StackBlockNum;
    public ushort RuntimeBlockNum;
    public fixed ushort VertexBufferBlockNum[(int)LodLevel.Max];
    public fixed ushort EdgeGeometryVertexBufferBlockNum[(int)LodLevel.Max];
    public fixed ushort IndexBufferBlockNum[(int)LodLevel.Max];
    public ushort VertexDeclarationNum;
    public ushort MaterialNum;
    public byte NumLods;
    public bool IndexBufferStreamingEnabled;
    public bool EdgeGeometryEnabled;
    public byte Padding;
};
