using System.Numerics;
using Meddle.Formats.Files.MtrlFile;
using SkiaSharp;

namespace Meddle.Utils.Files.Structs.Material;

public static class ColorTableTextureExtensions
{
    public static SKColor ToSkColor(this ShortVec4 vec)
    {
        var c = Vector4.Clamp(vec.ToVector4(), Vector4.Zero, Vector4.One);
        return new SKColor((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255), (byte)(c.W * 255));
    }

    public static SkTexture ToTexture(this LegacyColorTable table)
    {
        var texture = new SkTexture(LegacyColorTable.TextureSize.Width, LegacyColorTable.TextureSize.Height);
        var buffer = table.Buffer;
        for (var x = 0; x < LegacyColorTable.TextureSize.Width; x++)
        {
            for (var y = 0; y < LegacyColorTable.TextureSize.Height; y++)
            {
                texture[x, y] = buffer[y][x].ToSkColor();
            }
        }
        return texture;
    }

    public static SkTexture ToTexture(this ColorTable table)
    {
        var texture = new SkTexture(ColorTable.TextureSize.Width, ColorTable.TextureSize.Height);
        var buffer = table.Buffer;
        for (var x = 0; x < ColorTable.TextureSize.Width; x++)
        {
            for (var y = 0; y < ColorTable.TextureSize.Height; y++)
            {
                texture[x, y] = buffer[y][x].ToSkColor();
            }
        }
        return texture;
    }
}
