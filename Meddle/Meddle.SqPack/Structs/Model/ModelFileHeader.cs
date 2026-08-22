using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Model;

[StructLayout(LayoutKind.Sequential, Size = 68)]
public unsafe struct ModelFileHeader
{
    public uint Version;
    public uint StackSize;
    public uint RuntimeSize;
    public ushort VertexDeclarationCount;
    public ushort MaterialCount;
    public fixed uint VertexOffset[3];
    public fixed uint IndexOffset[3];
    public fixed uint VertexBufferSize[3];
    public fixed uint IndexBufferSize[3];
    public byte LodCount;
    public bool EnableIndexBufferStreaming;
    public bool EnableEdgeGeometry;
}
