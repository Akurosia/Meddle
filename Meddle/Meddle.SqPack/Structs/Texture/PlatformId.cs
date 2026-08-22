namespace Meddle.SqPack.Structs.Texture;

public enum PlatformId : byte
{
    Win32,
    PS3, // obsolete now but uses big endian which I'm not going to support
    PS4
}
