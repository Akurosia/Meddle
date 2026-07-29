using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Meddle.Formats.Files;

/// <summary>
/// Parses a GrassZoneData (.gzd) file, i.e. <c>bg/.../grass/grass_zone_data.gzd</c>
/// zone-level index of grass cells. Each cell entry names a <see cref="GgdFile"/>
/// </summary>
public class GzdFile
{
    public const uint GzdMagic = 0x00677A64; // "dzg\0"

    public GzdHeader Header;
    /// <summary>Texture name suffixes (e.g. "_grass1").</summary>
    public string[] TextureSuffixes = new string[3];
    /// <summary>Only present when Header.Version >= 0x2000600; defaults to (1,1,1).</summary>
    public Vector3 Scale = Vector3.One;
    public string[] ModelPaths;
    /// <summary>Cell lists per LOD tier: [0]=h, [1]=m, [2]=l.</summary>
    public GzdCell[][] Cells = new GzdCell[3][];

    public GzdCell[] CellsH => Cells[0];
    public GzdCell[] CellsM => Cells[1];
    public GzdCell[] CellsL => Cells[2];

    [JsonIgnore]
    public byte[] RawData;
    [JsonIgnore]
    public ReadOnlySpan<byte> Data => RawData.AsSpan();

    public GzdFile(byte[] data) : this((ReadOnlySpan<byte>)data) { }

    public GzdFile(ReadOnlySpan<byte> data)
    {
        RawData = data.ToArray();
        var reader = new SpanBinaryReader(data);
        Header = reader.Read<GzdHeader>();
        if (Header.Magic != GzdMagic)
            throw new InvalidDataException($"Invalid gzd magic 0x{Header.Magic:X8}");
        if (Header.Version < 0x2000500)
            throw new NotSupportedException($"Old gzd version 0x{Header.Version:X}");
        for (var i = 0; i < 3; i++)
            TextureSuffixes[i] = Header.TextureSuffix(i);
        if (Header.Version >= 0x2000600)
            Scale = reader.Read<Vector3>();

        ModelPaths = new string[Header.ModelPathCount];
        for (var i = 0; i < Header.ModelPathCount; i++)
        {
            var pos = reader.Position;
            ModelPaths[i] = reader.ReadString(pos, 256).TrimEnd('\0');
            reader.Seek(256, SeekOrigin.Current);
        }

        for (var tier = 0; tier < 3; tier++)
            Cells[tier] = reader.Read<GzdCell>(Header.CellCount(tier)).ToArray();

        if (reader.Remaining != 0)
            throw new InvalidDataException($"{reader.Remaining} trailing bytes after cells");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct GzdHeader
    {
        public uint Magic;      // "dzg\0"
        public uint Version;    // client requires >= 0x2000500
        public fixed ushort CellCounts[3]; // per LOD tier h/m/l
        public byte Unk14;      // 32 in sample (grid dimension?)
        public byte ModelPathCount;
        public fixed byte TextureSuffixes[3 * 32];
        // followed by: float3 scale        (Version >= 0x2000600 only)
        //              char[256] x ModelPathCount model paths
        //              GzdCell[CellCounts[0]] + [1] + [2]

        public int CellCount(int tier) => CellCounts[tier];

        public string TextureSuffix(int i)
        {
            fixed (byte* p = TextureSuffixes)
            {
                var span = new ReadOnlySpan<byte>(p + i * 32, 32);
                var end = span.IndexOf((byte)0);
                return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
            }
        }
    }
}

/// <summary>
/// Index for Ggd files <c>{NumA:000}_{NumB:000}_{NumC:000}_{h|m|l}.ggd</c>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GzdCell
{
    public Vector3 Center;  // world space
    public float Radius;
    public byte LodTier;    // 0=h, 1=m, 2=l
    public byte NumC;
    public byte NumB;
    public byte NumA;

    public string GgdName => $"{NumA:D3}_{NumB:D3}_{NumC:D3}_{"hml"[LodTier]}.ggd";
}
