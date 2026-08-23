using Meddle.Plugin.Models.Skeletons;

namespace Meddle.Plugin.Models.AnimationDump;

public static class AnimationCaptureDumpMapper
{
    public static AnimationCaptureDump ToDump(this List<(DateTime Time, AttachSet[] Attaches)> frames) => new()
    {
        Frames = frames.Select(f => new AnimationCaptureFrame
        {
            Time = f.Time,
            Attaches = f.Attaches.Select(ToDump).ToList()
        }).ToList()
    };

    private static AttachSetDump ToDump(AttachSet attach) => new()
    {
        Id = attach.Id,
        Name = attach.Name,
        OwnerId = attach.OwnerId,
        AttachBoneName = attach.AttachBoneName,
        Transform = TransformDump.From(attach.Transform.Translation, attach.Transform.Rotation, attach.Transform.Scale),
        Attach = ToDump(attach.Attach),
        Skeleton = ToDump(attach.Skeleton)
    };

    private static ParsedAttachDump ToDump(ParsedAttach attach) => new()
    {
        AttachmentCount = attach.AttachmentCount,
        ExecuteType = attach.ExecuteType,
        PartialSkeletonIdx = attach.PartialSkeletonIdx,
        BoneIdx = attach.BoneIdx,
        OffsetTransform = attach.OffsetTransform is { } offset ? TransformDump.From(offset) : null,
        TargetSkeleton = attach.TargetSkeleton is { } target ? ToDump(target) : null,
        OwnerSkeleton = attach.OwnerSkeleton is { } owner ? ToDump(owner) : null
    };

    private static ParsedSkeletonDump ToDump(ParsedSkeleton skeleton) => new()
    {
        Transform = TransformDump.From(skeleton.Transform),
        PartialSkeletons = skeleton.PartialSkeletons.Select(ToDump).ToList()
    };

    private static ParsedPartialSkeletonDump ToDump(ParsedPartialSkeleton partial) => new()
    {
        HandlePath = partial.HandlePath,
        ConnectedBoneIndex = partial.ConnectedBoneIndex,
        BoneCount = partial.BoneCount,
        HkSkeleton = partial.HkSkeleton is { } hk ? ToDump(hk) : null,
        Poses = partial.Poses.Select(ToDump).ToList()
    };

    private static ParsedHkaSkeletonDump ToDump(ParsedHkaSkeleton hk) => new()
    {
        BoneNames = hk.BoneNames.ToList(),
        BoneParents = hk.BoneParents.ToList(),
        ReferencePose = hk.ReferencePose.Select(TransformDump.From).ToList()
    };

    private static ParsedHkaPoseDump ToDump(ParsedHkaPose pose) => new()
    {
        Pose = pose.Pose.Select(TransformDump.From).ToList()
    };
}
