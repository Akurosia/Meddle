using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Interop;

namespace Meddle.Plugin.Models.Structs;

[StructLayout(LayoutKind.Explicit, Size = 0x78)]
public unsafe partial struct MeddleAttach
{
    [FieldOffset(0x00)] public void* VTable;
    [FieldOffset(0x38)] public MeddleAttach* LinkedListPrevious;
    [FieldOffset(0x40)] public MeddleAttach* LinkedListNext;

    // type 0:
    // - root object
    // - mount
    // - unmounted player
    // type 1: - ?
    // type 2: - ?
    // type 3:
    // - ornament
    // - mounted player
    // type 4:
    // - weapon
    // type 5: - ?
    [FieldOffset(0x50)] public int ExecuteType;

    [FieldOffset(0x54)] private int UnkValue;

    [FieldOffset(0x58)] public Skeleton* TargetSkeleton;      // ExecuteType 3/4
    [FieldOffset(0x60)] public Skeleton* OwnerSkeleton;       // ExecuteType 4
    [FieldOffset(0x60)] public CharacterBase* OwnerCharacter; // ExecuteType 3
    [FieldOffset(0x68)] public int AttachmentCount;
    [FieldOffset(0x70)] public Attachment* SkeletonBoneAttachments;

    public Span<Pointer<Attachment>> SkeletonBoneAttachmentsSpan => new(SkeletonBoneAttachments, AttachmentCount);

    [StructLayout(LayoutKind.Explicit, Size = 0x58)]
    public struct Attachment
    {
        [FieldOffset(0x02)] public Skeleton.BoneIndexMask BoneIndexMask;
        [FieldOffset(0x10)] public hkQsTransformf ChildTransform;
    }
}
