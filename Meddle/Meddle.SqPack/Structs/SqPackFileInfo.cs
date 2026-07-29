using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SqPackFileInfo
{
    public uint Size;
    public FileType Type;
    public uint RawFileSize;
    public fixed uint AdditionalData[3]; 
    // Model:
    // 0: NumberOfBlocks
    // 1: UsedNumberOfBlocks
    // 2: Version
    // Other:
    // 0: unknown
    // 1: unknown
    // 2: NumberOfBlocks
    
    public uint NumberOfBlocks => Type != FileType.Model ? AdditionalData[2] : AdditionalData[0];
}
