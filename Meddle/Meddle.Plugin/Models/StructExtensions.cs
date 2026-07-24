using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Interop;
using Meddle.Plugin.Models.Skeletons;
using Meddle.Plugin.Models.Structs;
using Meddle.Plugin.Utils;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using Skeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Meddle.Plugin.Models;

public static class StructExtensions
{
    public const int CharacterBaseAttachOffset = 0xD0; // CharacterBase + 0xD0 -> Attach
    //
    // public const int ModelEnabledAttributeIndexMaskOffset = 0xAC; // Model + 0xAC -> EnabledAttributeIndexMask
    // public const int ModelEnabledShapeKeyIndexMaskOffset = 0xC8;  // Model + 0xC8 -> EnabledShapeKeyIndexMask

    public const int PartialSkeletonFlagsOffset = 0x8; // PartialSkeleton + 0x8 -> Flags

    public static unsafe Span<Pointer<MaterialResourceHandle>> GetMaterials(this Pointer<ModelResourceHandle> model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (model.Value == null) throw new ArgumentNullException(nameof(model));
        // var ext = (ModelResourceHandleExt*)model.Value;
        var headerData = new ModelResourceHandleData(model.Value->ModelData);
        var materialCount = headerData.ModelHeader.MaterialCount;
        return new Span<Pointer<MaterialResourceHandle>>(model.Value->MaterialResourceHandles, materialCount);
    }
    
    public static unsafe ParsedAttach GetParsedAttach(this Pointer<CharacterBase> character)
    {
        var attach = character.Value->Attach;
        return new ParsedAttach(attach);
    }
    
    public static unsafe List<Pointer<CharacterBase>> FindAttachedCharacterBases(CharacterBase* owner, nint attachVTable, IReadOnlySet<nint> exclude)
    {
        var results = new List<Pointer<CharacterBase>>();
        if (owner == null)
            return results;

        foreach (var nodePtr in EnumerateAttaches((MeddleAttach*)&owner->Attach, attachVTable))
        {
            var node = nodePtr.Value;
            if (node == null)
                continue;

            var attachedToOwner = node->ExecuteType switch
            {
                3 => node->OwnerCharacter == owner,
                4 => node->OwnerSkeleton == owner->Skeleton,
                _ => false
            };
            if (!attachedToOwner)
                continue;

            var childCBase = node->TargetSkeleton != null ? node->TargetSkeleton->Owner : null;
            if ((nint)childCBase == (nint)owner || exclude.Contains((nint)childCBase))
                continue;

            results.Add(childCBase);
        }

        return results;
    }

    public static unsafe List<Pointer<CharacterBase>> GetLinkedAttaches(this Pointer<CSCharacter> characterPtr, SigUtil sigUtil)
    {
        var character = characterPtr.Value;
        if (character == null)
            return [];

        var drawObject = character->DrawObject;
        if (drawObject == null || drawObject->GetObjectType() != ObjectType.CharacterBase)
            return [];

        var attachVTable = sigUtil.GetAttachVTable();

        var known = new HashSet<nint>
        {
            (nint)character,
            (nint)drawObject
        };

        foreach (var weapon in character->DrawData.WeaponData)
        {
            if (weapon.DrawObject != null)
                known.Add((nint)weapon.DrawObject);
        }

        if (character->Mount.MountObject != null)
            known.Add((nint)character->Mount.MountObject->GameObject.DrawObject);

        if (character->OrnamentData.OrnamentObject != null)
            known.Add((nint)character->OrnamentData.OrnamentObject->Character.GameObject.DrawObject);

        var cBase = (CharacterBase*)drawObject;
        return FindAttachedCharacterBases(cBase, attachVTable, known);
    }
    
    public static unsafe Span<Pointer<MeddleAttach>> EnumerateAttaches(this Pointer<MeddleAttach> attach, nint attachVTable)
    {
        if (attach.Value == null)
            return Span<Pointer<MeddleAttach>>.Empty;

        // walk back to first item in list
        var node = attach.Value;
        while (node->LinkedListPrevious != null)
            node = node->LinkedListPrevious;

        // walk from first item to end of list
        var results = new List<Pointer<MeddleAttach>>();
        while (node != null)
        {
            // the list can contain deformers too, filter out using the vtable
            if (attachVTable == 0 || node->VTable == (void*)attachVTable)
                results.Add(node);
            node = node->LinkedListNext;
        }

        return results.ToArray();
    }

    public static unsafe ParsedSkeleton GetParsedSkeleton(this Pointer<CharacterBase> character)
    {
        if (character == null) throw new ArgumentNullException(nameof(character));
        if (character.Value == null) throw new ArgumentNullException(nameof(character));
        return GetParsedSkeleton(character.Value->Skeleton);
    }

    private static unsafe ParsedSkeleton GetParsedSkeleton(this Pointer<Skeleton> skeleton)
    {
        if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
        return new ParsedSkeleton(skeleton.Value);
    }
    
    public static unsafe Meddle.Utils.Export.Model.ShapeAttributeGroup ParseModelShapeAttributes(
        Pointer<Model> modelPointer)
    {
        if (modelPointer == null) throw new ArgumentNullException(nameof(modelPointer));
        if (modelPointer.Value == null) throw new ArgumentNullException(nameof(modelPointer));
        var model = modelPointer.Value;
        // var (enabledAttributeIndexMask, enabledShapeKeyIndexMask) = modelPointer.GetModelMasks();
        var shapes = new List<(string, short)>();
        foreach (var shape in model->ModelResourceHandle->Shapes)
        {
            shapes.Add((MemoryHelper.ReadStringNullTerminated((nint)shape.Item1.Value), shape.Item2));
        }

        var attributes = new List<(string, short)>();
        foreach (var attribute in model->ModelResourceHandle->Attributes)
        {
            attributes.Add((MemoryHelper.ReadStringNullTerminated((nint)attribute.Item1.Value), attribute.Item2));
        }

        var shapeAttributeGroup = new Meddle.Utils.Export.Model.ShapeAttributeGroup(
            model->EnabledShapeKeyIndexMask, model->EnabledAttributeIndexMask, 
            shapes.ToArray(), attributes.ToArray());

        return shapeAttributeGroup;
    }
}
