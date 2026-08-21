using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using InteropGenerator.Runtime;
using Meddle.Plugin.Utils;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

public unsafe class PapHooks : IDisposable, IService
{
    public const string GetResourceSyncSig = "E8 ?? ?? ?? ?? 48 8B C8 8B C3 F0 0F C0 81";
    public const string GetResourceAsyncSig = "E8 ?? ?? ?? 00 48 8B D8 EB ?? F0 FF 83 ?? ?? 00 00";

    public const string GetAnimationPapSig =
        "40 55 53 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC E0 02 00 00";
    private const int MaxTracked = 200;

    private readonly ILogger<PapHooks> logger;
    private readonly Hook<GetResourceSyncDelegate>? getResourceSyncHook;
    private readonly Hook<GetResourceAsyncDelegate>? getResourceAsyncHook;
    private readonly LinkedList<(string Path, DateTime Time)> recentPaps = new();

    public PapHooks(ILogger<PapHooks> logger, HookManager hookManager)
    {
        this.logger = logger;
        getResourceSyncHook = hookManager.CreateHook<GetResourceSyncDelegate>(GetResourceSyncSig, GetResourceSyncDetour);
        getResourceSyncHook?.Enable();
        getResourceAsyncHook = hookManager.CreateHook<GetResourceAsyncDelegate>(GetResourceAsyncSig, GetResourceAsyncDetour);
        getResourceAsyncHook?.Enable();
    }

    public IReadOnlyList<(string Path, DateTime Time)> GetRecentPaps()
    {
        lock (recentPaps)
        {
            return recentPaps.ToArray();
        }
    }

    private ResourceHandle* GetResourceSyncDetour(ResourceManager* resourceManager, ResourceCategory* category,
                                                    uint* type, uint* hash, CStringPointer path, void* unknown,
                                                    void* unkDebugPtr, uint unkDebugInt)
    {
        var result = getResourceSyncHook!.Original(resourceManager, category, type, hash, path, unknown,
                                                     unkDebugPtr, unkDebugInt);
        TrackIfPap(result);
        return result;
    }

    private ResourceHandle* GetResourceAsyncDetour(ResourceManager* resourceManager, ResourceCategory* category,
                                                     uint* type, uint* hash, CStringPointer path, void* unknown,
                                                     bool isUnknown, void* unkDebugPtr, uint unkDebugInt)
    {
        var result = getResourceAsyncHook!.Original(resourceManager, category, type, hash, path, unknown,
                                                      isUnknown, unkDebugPtr, unkDebugInt);
        TrackIfPap(result);
        return result;
    }

    private void TrackIfPap(ResourceHandle* result)
    {
        if (result == null)
            return;

        try
        {
            var filePath = result->FileName.ParseString();
            if (filePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
            {
                lock (recentPaps)
                {
                    // remove any prior entry for this path so it gets re-added at the front with a fresh time
                    for (var node = recentPaps.First; node != null; node = node.Next)
                    {
                        if (node.Value.Path == filePath)
                        {
                            recentPaps.Remove(node);
                            break;
                        }
                    }

                    recentPaps.AddFirst((filePath, DateTime.Now));
                    while (recentPaps.Count > MaxTracked)
                        recentPaps.RemoveLast();
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to read resource path in GetResource detour");
        }
    }

    public void Dispose()
    {
        logger.LogDebug("Disposing PapHooks");
        getResourceSyncHook?.Dispose();
        getResourceAsyncHook?.Dispose();
        recentPaps.Clear();
    }

    private unsafe delegate ResourceHandle* GetResourceSyncDelegate(
        ResourceManager* resourceManager, ResourceCategory* category, uint* type, uint* hash, CStringPointer path,
        void* unknown, void* unkDebugPtr, uint unkDebugInt);

    private unsafe delegate ResourceHandle* GetResourceAsyncDelegate(
        ResourceManager* resourceManager, ResourceCategory* category, uint* type, uint* hash, CStringPointer path,
        void* unknown, bool isUnknown, void* unkDebugPtr, uint unkDebugInt);
}
