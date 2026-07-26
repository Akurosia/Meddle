using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Interop;
using Meddle.Plugin.Models.Structs;
using Meddle.Plugin.Utils;

namespace Meddle.Plugin.Models.Skeletons;

public class ParsedPartialSkeleton
{
    public ParsedPartialSkeleton() { }

    public unsafe ParsedPartialSkeleton(Pointer<PartialSkeleton> partialSkeleton) :
        this(partialSkeleton.Value) { }

    public unsafe ParsedPartialSkeleton(PartialSkeleton* partialSkeleton)
    {
        var ex = (PartialSkeletonEx*)partialSkeleton;
        if (partialSkeleton->SkeletonResourceHandle != null)
        {
            HkSkeleton = new ParsedHkaSkeleton(partialSkeleton->SkeletonResourceHandle->HavokSkeleton);
            HandlePath = partialSkeleton->SkeletonResourceHandle->FileName.ParseString();
        }

        BoneCount = ex->BoneCount;
        ConnectedBoneIndex = partialSkeleton->ConnectedBoneIndex;

        var poses = new List<ParsedHkaPose>();
        for (var i = 0; i < partialSkeleton->HavokPoses.Length; ++i)
        {
            var pose = partialSkeleton->GetHavokPose(i);
            if (pose != null)
            {
                if (pose->Skeleton != partialSkeleton->SkeletonResourceHandle->HavokSkeleton)
                {
                    throw new ArgumentException(
                        $"Pose is not the same as the skeleton {(nint)pose->Skeleton:X16} != {(nint)partialSkeleton->SkeletonResourceHandle->HavokSkeleton:X16}");
                }

                poses.Add(new ParsedHkaPose(pose));
            }
        }

        Poses = poses;
    }

    public string? HandlePath { get; init; }
    public ParsedHkaSkeleton? HkSkeleton { get; init; }
    public IReadOnlyList<ParsedHkaPose> Poses { get; init; } = [];
    public int ConnectedBoneIndex { get; init; }
    public uint BoneCount { get; init; }
}
