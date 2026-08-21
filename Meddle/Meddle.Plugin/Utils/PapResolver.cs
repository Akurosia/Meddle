using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Utils;

public static unsafe class PapResolver
{
    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    private struct PapCacheKey
    {
        [FieldOffset(0x00)] public uint IdHash;
        [FieldOffset(0x04)] public uint NameHash;
        [FieldOffset(0x08)] public uint ModelSubId;
        [FieldOffset(0x0C)] public uint SubraceByte;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    private struct PapCacheValue
    {
        [FieldOffset(0x10)] public ResourceHandle* ResourceHandle;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x8F8)]
    private struct CharacterBasePapMaps
    {
        [FieldOffset(0x8D8)] public StdMap<PapCacheKey, PapCacheValue> Map1;
        [FieldOffset(0x8E8)] public StdMap<PapCacheKey, PapCacheValue> Map2;
    }

    private static uint HashAnimationName(ReadOnlySpan<byte> nameUtf8)
    {
        // FNV-1a 32-bit
        var hash = 0x811C9DC5u;
        foreach (var b in nameUtf8)
            hash = b ^ (hash * 0x01000193u);
        return hash;
    }

    // Resolves an ActionTimelineKey (e.g. "emote/j_pose02_loop") to a pap path by name
    public static bool TryGetResolvedPapPath(CharacterBase* characterBase, string animationName, out string? path)
    {
        path = null;
        if (characterBase == null || string.IsNullOrEmpty(animationName))
            return false;

        var targetHash = HashAnimationName(Encoding.UTF8.GetBytes(animationName));

        var maps = (CharacterBasePapMaps*)characterBase;
        foreach (var map in new[] { maps->Map1, maps->Map2 })
        {
            foreach (var pair in map)
            {
                if (pair.Item1.NameHash != targetHash || pair.Item2.ResourceHandle == null)
                    continue;

                path = pair.Item2.ResourceHandle->FileName.ToString();
                return true;
            }
        }

        var leafName = animationName[(animationName.LastIndexOf('/') + 1)..];

        var allResidentPaps = new List<string>();
        foreach (var slot in DumpSkeletonContainerPaps(characterBase))
        {
            allResidentPaps.AddRange(slot.PapVector1);
            allResidentPaps.AddRange(slot.PapVector2);
            allResidentPaps.AddRange(slot.PapVector3);
        }

        if (!string.IsNullOrEmpty(leafName))
        {
            foreach (var candidate in allResidentPaps)
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(candidate), leafName,
                                    StringComparison.OrdinalIgnoreCase))
                    continue;

                path = candidate;
                return true;
            }
        }

        if (allResidentPaps.Count == 1)
        {
            path = allResidentPaps[0];
            return true;
        }

        // Facial keys (e.g. "facial/pose/bow") don't seem to exist in character-resident list. So scanning the global table.
        if (animationName.StartsWith("facial/", StringComparison.OrdinalIgnoreCase))
        {
            var cNumber = ExtractModelId(allResidentPaps);
            if (cNumber != null)
            {
                var facePap = TryFindGlobalResidentFacePap(cNumber);
                if (facePap != null)
                {
                    path = facePap;
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ExtractModelId(List<string> residentPaps)
    {
        const string prefix = "chara/human/c";
        foreach (var candidate in residentPaps)
        {
            var idx = candidate.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            var start = idx + prefix.Length - 1; // include the leading 'c'
            var end = candidate.IndexOf('/', start);
            if (end > start)
                return candidate[start..end];
        }

        return null;
    }

    private static string? TryFindGlobalResidentFacePap(string cNumber)
    {
        var prefix = $"chara/human/{cNumber}/animation/f";
        const string suffix = "/resident/face.pap";

        foreach (var handle in EnumerateGlobalPapHandles())
        {
            if (handle.Value == null)
                continue;

            string name;
            try
            {
                name = handle.Value->FileName.ToString();
            }
            catch
            {
                continue;
            }

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    public readonly record struct SkeletonContainerPaps(
        int SlotIndex, List<string> PapVector1, List<string> PapVector2, List<string> PapVector3);

    public static List<SkeletonContainerPaps> DumpSkeletonContainerPaps(CharacterBase* characterBase)
    {
        var result = new List<SkeletonContainerPaps>();
        if (characterBase == null)
            return result;

        try
        {
            var containers = characterBase->SkeletonAnimationContainers;
            for (var i = 0; i < containers.Length; i++)
            {
                var v1 = ReadPapVectorPaths(containers[i].PapVector1);
                var v2 = ReadPapVectorPaths(containers[i].PapVector2);
                var v3 = ReadPapVectorPaths(containers[i].PapVector3);
                if (v1.Count > 0 || v2.Count > 0 || v3.Count > 0)
                    result.Add(new SkeletonContainerPaps(i, v1, v2, v3));
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(ex, "DumpSkeletonContainerPaps failed");
        }

        return result;
    }

    private static List<string> ReadPapVectorPaths(StdVector<Pointer<ResourceHandle>> vector)
    {
        var list = new List<string>();
        foreach (var ptr in vector.AsSpan())
        {
            var handle = ptr.Value;
            if (handle == null)
                continue;

            try
            {
                var name = handle->FileName.ParseString();
                if (!string.IsNullOrEmpty(name))
                    list.Add(name);
            }
            catch
            {
                // skip unreadable entries
            }
        }

        return list;
    }

    // AnimationIndex is this control's index into the resolved pap's Bindings/Animations arrays; matches
    // PapAnimation.HavokIndex in the pap's own name table. SkeletonPath is this partial skeleton's own
    // live-resolved .sklb path, i.e. the skeleton the pap's clip needs to be sampled against.
    public readonly record struct ActiveAnimationControl(
        int PartialSkeletonIndex, int LayerIndex, int ControlIndex,
        float Weight, float LocalTime, Pointer<hkaAnimation> AnimationHandle,
        Pointer<hkaAnimationBinding> BindingHandle, string? PapPath, int? AnimationIndex, string? SkeletonPath);

    public readonly record struct PapMatch(string FileName, int AnimationIndex);

    private const int HavokLayerCount = 2;

    [StructLayout(LayoutKind.Explicit, Size = 0xC8)]
    private struct PartialAnimationPackResourceHandleExt
    {
        [FieldOffset(0xC0)] public hkaAnimationContainer* AnimationContainer;
    }

    // Ground-truth blend state read directly off each PartialSkeleton's hkaAnimatedSkeleton -- covers
    // idle/move, which never populate a SchedulerResource.
    public static List<ActiveAnimationControl> DumpActiveAnimationControls(CharacterBase* characterBase)
    {
        var result = new List<ActiveAnimationControl>();
        if (characterBase == null || characterBase->Skeleton == null)
            return result;

        try
        {
            var skeleton = characterBase->Skeleton;
            for (var i = 0; i < skeleton->PartialSkeletonCount; i++)
            {
                var partial = &skeleton->PartialSkeletons[i];
                string? skeletonPath = null;
                try
                {
                    if (partial->SkeletonResourceHandle != null)
                        skeletonPath = partial->SkeletonResourceHandle->FileName.ToString();
                }
                catch
                {
                    // leave null if unreadable
                }

                for (var layer = 0; layer < HavokLayerCount; layer++)
                {
                    var animatedSkeleton = partial->GetHavokAnimatedSkeleton(layer);
                    if (animatedSkeleton == null)
                        continue;

                    var controls = animatedSkeleton->AnimationControls;
                    for (var c = 0; c < controls.Length; c++)
                    {
                        var control = (hkaAnimationControl*)controls[c].Value;
                        if (control == null)
                            continue;

                        var binding = control->Binding.ptr;
                        var animation = binding != null ? binding->Animation.ptr : null;
                        var match = ResolvePapForControl(characterBase, partial, animation, binding);
                        result.Add(new ActiveAnimationControl(
                            i, layer, c, control->Weight, control->LocalTime,
                            animation, binding, match?.FileName, match?.AnimationIndex, skeletonPath));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(ex, "DumpActiveAnimationControls failed");
        }

        return result;
    }

    // Tries, in order: this partial's own resident paps, the per-actor resolve cache, then the
    // engine-wide registry (needed for paps not attached to any per-actor structure, e.g. shared
    // resident facial-expression files).
    private static PapMatch? ResolvePapForControl(CharacterBase* characterBase, PartialSkeleton* partial,
                                                   Pointer<hkaAnimation> animationHandle,
                                                   Pointer<hkaAnimationBinding> bindingHandle)
    {
        if (animationHandle.IsNull && bindingHandle.IsNull)
            return null;
        if (partial == null || partial->SkeletonResourceHandle == null)
            return null;

        var containers = characterBase->SkeletonAnimationContainers;
        for (var ci = 0; ci < containers.Length; ci++)
        {
            if (containers[ci].PartialSkeleton != partial->SkeletonResourceHandle)
                continue;

            var slot = containers[ci];
            foreach (var vector in new[] { slot.PapVector1, slot.PapVector2, slot.PapVector3 })
            {
                foreach (var ptr in vector.AsSpan())
                {
                    var match = TryMatchResidentPap(ptr.Value, animationHandle, bindingHandle);
                    if (match != null)
                        return match;
                }
            }

            break;
        }

        try
        {
            var maps = (CharacterBasePapMaps*)characterBase;
            foreach (var map in new[] { maps->Map1, maps->Map2 })
            {
                foreach (var pair in map)
                {
                    var match = TryMatchResidentPap(pair.Item2.ResourceHandle, animationHandle, bindingHandle);
                    if (match != null)
                        return match;
                }
            }
        }
        catch
        {
            // cache tree can mutate mid-walk; skip on failure
        }

        try
        {
            foreach (var handle in EnumerateGlobalPapHandles())
            {
                var match = TryMatchResidentPap(handle.Value, animationHandle, bindingHandle);
                if (match != null)
                    return match;
            }
        }
        catch
        {
            // global registry can mutate mid-walk; skip on failure
        }

        return null;
    }

    // Every loaded ResourceHandle is interned into ResourceManager's global ResourceGraph for engine-wide
    // dedup -- reaches paps not attached to this actor's own containers or cache.
    private static List<Pointer<ResourceHandle>> EnumerateGlobalPapHandles()
    {
        // Not a C# iterator (yield) -- pointer/unsafe locals can't survive across yield boundaries in
        // iterator state machines, so this materializes the list eagerly instead.
        const uint PapFileType = 7364976; // ResourceHandleType.FileType 4CC "pap\0", confirmed via IDA

        var result = new List<Pointer<ResourceHandle>>();

        var resourceManager = ResourceManager.Instance();
        if (resourceManager == null || resourceManager->ResourceGraph == null)
            return result;

        ref var container = ref resourceManager->ResourceGraph->GetContainer(ResourceCategory.Chara);
        var papTypeMap = container.MainMap;
        if (papTypeMap == null)
            return result;

        if (!papTypeMap->TryGetValuePointer(PapFileType, out var innerMapPtr) || innerMapPtr == null)
            return result;

        var innerMap = innerMapPtr->Value;
        if (innerMap == null)
            return result;

        foreach (var pair in *innerMap)
            result.Add(pair.Item2);

        return result;
    }

    private static PapMatch? TryMatchResidentPap(ResourceHandle* handle, Pointer<hkaAnimation> animationHandle,
                                                  Pointer<hkaAnimationBinding> bindingHandle)
    {
        if (handle == null)
            return null;

        try
        {
            var container = ((PartialAnimationPackResourceHandleExt*)handle)->AnimationContainer;
            if (container == null)
                return null;

            if (!bindingHandle.IsNull)
            {
                for (var i = 0; i < container->Bindings.Length; i++)
                {
                    if (container->Bindings[i].ptr == bindingHandle)
                        return new PapMatch(handle->FileName.ToString(), i);
                }
            }

            if (!animationHandle.IsNull)
            {
                for (var i = 0; i < container->Animations.Length; i++)
                {
                    if (container->Animations[i].ptr == animationHandle)
                        return new PapMatch(handle->FileName.ToString(), i);
                }
            }
        }
        catch
        {
            // resident pap list can contain handles mid-load/unload; skip unreadable entries
        }

        return null;
    }
}
