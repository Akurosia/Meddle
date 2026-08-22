using System.Runtime.InteropServices;

namespace Meddle.SqPack.Structs.Texture;

[StructLayout(LayoutKind.Sequential)]
public struct ReferenceBlockRange
{
    public uint Begin;
    public uint End;
}
