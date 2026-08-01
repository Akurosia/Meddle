using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using Meddle.Plugin.Services;

namespace Meddle.Plugin.Havok;

/// <summary>
/// Converts Havok packfiles to Havok XML.
/// </summary>
public unsafe class HavokConverter : IService
{
    private const string RootLevelContainerClassName = "hkRootLevelContainer";

    public string HkxToXml(byte[] hkx)
    {
        hkResource* resource;
        var errorDetails = stackalloc hkSerializeUtil.ErrorDetails[1];
        var loadOptions = BuildLoadOptions();
        fixed (byte* buf = hkx)
        {
            resource = hkSerializeUtil.LoadFromBuffer(buf, hkx.Length, errorDetails, &loadOptions);
        }

        if (resource == null)
            throw new InvalidOperationException(
                $"Havok failed to read hkx resource: {errorDetails->DefaultMessage.String}");

        var saveOptions = new hkSerializeUtil.SaveOptions
        {
            Flags = new hkFlags<hkSerializeUtil.SaveOptionBits, int>
            {
                Storage = (int)(hkSerializeUtil.SaveOptionBits.SerializeIgnoredMembers |
                                 hkSerializeUtil.SaveOptionBits.TextFormat |
                                 hkSerializeUtil.SaveOptionBits.WriteAttributes)
            }
        };

        var tempFile = Write(resource, saveOptions);
        try
        {
            return File.ReadAllText(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static hkSerializeUtil.LoadOptions BuildLoadOptions()
    {
        var registry = hkBuiltinTypeRegistry.Instance();
        if (registry == null)
            throw new InvalidOperationException("hkBuiltinTypeRegistry instance is null");

        return new hkSerializeUtil.LoadOptions
        {
            Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int>
            {
                Storage = (int)hkSerializeUtil.LoadOptionBits.Default
            },
            ClassNameRegistry = registry->GetClassNameRegistry(),
            TypeInfoRegistry = registry->GetTypeInfoRegistry()
        };
    }

    /// <summary>Serializes an hkResource to a temporary file and returns its path.</summary>
    private static string Write(hkResource* resource, hkSerializeUtil.SaveOptions options)
    {
        var registry = hkBuiltinTypeRegistry.Instance();
        if (registry == null)
            throw new InvalidOperationException("hkBuiltinTypeRegistry instance is null");

        var typeInfoRegistry = registry->GetTypeInfoRegistry();
        var contents = resource->GetContentsPointer(RootLevelContainerClassName, typeInfoRegistry);
        if (contents == null)
            throw new InvalidOperationException("Failed to get hkRootLevelContainer contents from resource");

        var classNameRegistry = registry->GetClassNameRegistry();
        var klass = classNameRegistry->GetClassByName(RootLevelContainerClassName);
        if (klass == null)
            throw new InvalidOperationException("Failed to resolve hkRootLevelContainer class");

        var tempFile = Path.GetTempFileName();
        var oStream = stackalloc hkOstream[1];
        oStream->Ctor(tempFile);
        try
        {
            var result = stackalloc hkResult[1];
            hkSerializeUtil.Save(result, contents, klass, oStream->StreamWriter.ptr, options);
            if (result->Result == hkResult.hkResultEnum.Failure)
                throw new InvalidOperationException("Havok failed to save resource");
        }
        finally
        {
            oStream->Dtor();
        }

        return tempFile;
    }
}
