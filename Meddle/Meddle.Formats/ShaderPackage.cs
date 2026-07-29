using Meddle.Formats.Files;

namespace Meddle.Formats;

public class ShaderPackage
{
    public string Name { get; }
    public Dictionary<uint, float[]> MaterialConstants { get; }
    public Dictionary<uint, string> ResourceKeys { get; }
    public Dictionary<uint, string> Textures { get;  }
    public Dictionary<uint, uint> DefaultKeyValues { get; }

    public ShaderPackage(ShpkFile file, string name)
    {
        Name = name;
        
        var resourceKeys = new Dictionary<uint, string>();
        var defaultKeyValues = new Dictionary<uint, uint>();
        var textures = new Dictionary<uint, string>();
        var stringReader = new SpanBinaryReader(file.RawData[(int)file.FileHeader.StringsOffset..]);
        foreach (var sampler in file.Samplers)
        {
            if (sampler.Slot != 2)
                continue;
            
            var resName = stringReader.ReadString((int)sampler.StringOffset);
            // compute crc
            resourceKeys[sampler.Id] = resName;
        }
        
        foreach (var constant in file.Constants)
        {
            if (constant.Slot != 2)
                continue;
            var resName = stringReader.ReadString((int)constant.StringOffset);  
            resourceKeys[constant.Id] = resName;
        }
        
        foreach (var texture in file.Textures)
        {
            var resName = stringReader.ReadString((int)texture.StringOffset);
            textures[texture.Id] = resName;
            if (texture.Slot != 2)
                continue;
            resourceKeys[texture.Id] = resName;
        }
        
        foreach (var uav in file.Uavs)
        {
            if (uav.Slot != 2)
                continue;
            var resName = stringReader.ReadString((int)uav.StringOffset);
            resourceKeys[uav.Id] = resName;
        }
        
        var materialConstantDict = new Dictionary<uint, float[]>();
        var orderedMaterialParams = file.MaterialParams.Select((x, idx) => (x, idx)).OrderBy(x => x.x.ByteOffset).ToArray();
        foreach (var (materialParam, i) in orderedMaterialParams)
        {
            // get defaults from byteoffset -> byteoffset + bytesize
            var defaults = file.MaterialParamDefaults
                               .Skip(materialParam.ByteOffset / 4)
                               .Take(materialParam.ByteSize / 4).ToArray();
            var defaultCopy = new float[defaults.Length];
            Array.Copy(defaults, defaultCopy, defaults.Length);
            materialConstantDict[materialParam.Id] = defaultCopy;
        }

        foreach (var materialKey in file.MaterialKeys)
        {
            defaultKeyValues[materialKey.Id] = materialKey.DefaultValue;
        }
        
        foreach (var systemKey in file.SystemKeys)
        {
            defaultKeyValues[systemKey.Id] = systemKey.DefaultValue;
        }
        
        foreach (var sceneKey in file.SceneKeys)
        {
            defaultKeyValues[sceneKey.Id] = sceneKey.DefaultValue;
        }

        foreach (var subViewKey in file.SubViewKeys)
        {
            defaultKeyValues[subViewKey.Id] = subViewKey.DefaultValue;
        }
        
        DefaultKeyValues = defaultKeyValues;
        MaterialConstants = materialConstantDict;
        ResourceKeys = resourceKeys;
        Textures = textures;
    }
}
