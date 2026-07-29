namespace Meddle.Formats.Files.MtrlFile;

public class MtrlFile
{
    public const uint MtrlMagic = 0x1030000;

    private readonly byte[] data;

    public MaterialFileHeader FileHeader;
    public TextureOffset[] TextureOffsets;
    public UvColorSet[] UvColorSets;
    public ColorSet[] ColorSets;
    public byte[] Strings;
    public byte[] AdditionalData;
    public byte[] DataSet;
    
    //public ColorTable ColorTable;
    //public ColorDyeTable ColorDyeTable;
    
    public MaterialShaderHeader ShaderHeader;
    public ShaderKey[] ShaderKeys;
    public Constant[] Constants;
    public Sampler[] Samplers;
    public uint[] ShaderValues;

    public MtrlFile(byte[] data) : this((ReadOnlySpan<byte>)data) { }

    public MtrlFile(ReadOnlySpan<byte> data)
    {
        this.data = data.ToArray();
        var reader = new SpanBinaryReader(data);
        FileHeader = reader.Read<MaterialFileHeader>();
        if (FileHeader.Version != MtrlMagic)
            throw new InvalidDataException($"Invalid MTRL magic: {FileHeader.Version:X}");
        TextureOffsets = new TextureOffset[FileHeader.TextureCount];

        var offsets = reader.Read<uint>(FileHeader.TextureCount);
        for (var i = 0; i < offsets.Length; i++)
        {
            TextureOffsets[i].Offset = (ushort)offsets[i];
            TextureOffsets[i].Flags = (ushort)(offsets[i] >> 16);
        }

        UvColorSets = reader.Read<UvColorSet>(FileHeader.UvSetCount).ToArray();
        ColorSets = reader.Read<ColorSet>(FileHeader.ColorSetCount).ToArray();

        Strings = reader.Read<byte>(FileHeader.StringTableSize).ToArray();
        AdditionalData = reader.Read<byte>(FileHeader.AdditionalDataSize).ToArray();

        if (FileHeader.DataSetSize > 0)
        {
            DataSet = reader.Read<byte>(FileHeader.DataSetSize).ToArray();
            // var dataSetReader = new SpanBinaryReader(DataSet);
            // if (LargeColorTable)
            // {
            //     ColorTable = HasTable ? ColorTable.Load(ref dataSetReader) : ColorTable.Default();
            //     if (HasDyeTable)
            //         ColorDyeTable = dataSetReader.Read<ColorDyeTable>();
            // }
            // else
            // {
            //     ColorTable = HasTable ? ColorTable.LoadLegacy(ref dataSetReader) : ColorTable.DefaultLegacy();
            //     if (HasDyeTable)
            //         ColorDyeTable = new ColorDyeTable(dataSetReader.Read<LegacyColorDyeTable>());
            // }
        }
        else
        {
            DataSet = Array.Empty<byte>();
            //ColorTable = ColorTable.Default();
        }

        ShaderHeader = reader.Read<MaterialShaderHeader>();

        ShaderKeys = reader.Read<ShaderKey>(ShaderHeader.ShaderKeyCount).ToArray();
        Constants = reader.Read<Constant>(ShaderHeader.ConstantCount).ToArray();
        Samplers = reader.Read<Sampler>(ShaderHeader.SamplerCount).ToArray();

        ShaderValues = reader.Read<uint>(ShaderHeader.ShaderValueListSize / 4).ToArray();
    }

    public bool LargeColorTable =>
        AdditionalData.Length > 1 && AdditionalData[1] == 0x05 && (AdditionalData[0] & 0x30) == 0x30;

    public bool HasTable => AdditionalData.Length > 0 && (AdditionalData[0] & 0x4) != 0;
    public bool HasDyeTable => AdditionalData.Length > 0 && (AdditionalData[0] & 0x8) != 0;
    public ReadOnlySpan<byte> RawData => data;
}
