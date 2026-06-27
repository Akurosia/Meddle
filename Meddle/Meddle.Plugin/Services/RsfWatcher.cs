using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Meddle.Utils.Files.SqPack;

namespace Meddle.Plugin.Services;

public class RsfWatcher : IDisposable, IService
{
    private readonly IFramework framework;
    private readonly SqPack pack;

    public RsfWatcher(IFramework framework, SqPack pack)
    {
        this.framework = framework;
        this.pack = pack;
        framework.Update += FrameworkOnUpdate;
    }
    
    private unsafe void FrameworkOnUpdate(IFramework framework1)
    {
        var world = LayoutWorld.Instance();
        if (world == null)
        {
            return;
        }
        
        var rsf = world->RsfMap;
        foreach (var key in rsf->Keys)
        {
            if (!rsf->TryGetValuePointer(key, out var value))
            {
                continue;
            }
            
            if (value == null)
            {
                continue;
            }

            var valueData = value->Value;
            if (valueData == null)
            {
                continue;
            }
            
            // byte* to byte[64]
            var valueDataBuffer = new byte[64];
            Marshal.Copy((nint)valueData, valueDataBuffer, 0, 64);
            pack.RsfData[key] = valueDataBuffer;
        }
    }

    public void Dispose()
    {
        framework.Update -= FrameworkOnUpdate;
    }
}
