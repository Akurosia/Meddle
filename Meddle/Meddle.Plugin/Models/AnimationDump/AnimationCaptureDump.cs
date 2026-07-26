using System.Numerics;
using SharpGLTF.Transforms;

namespace Meddle.Plugin.Models.AnimationDump;

public class AnimationCaptureDump
{
    public int Version { get; set; } = 1;
    public List<AnimationCaptureFrame> Frames { get; set; } = [];
}

public class AnimationCaptureFrame
{
    public DateTime Time { get; set; }
    public List<AttachSetDump> Attaches { get; set; } = [];
}

public class AttachSetDump
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OwnerId { get; set; }
    public string? AttachBoneName { get; set; }
    public TransformDump Transform { get; set; } = TransformDump.Identity;
    public ParsedAttachDump Attach { get; set; } = new();

    public ParsedSkeletonDump Skeleton { get; set; } = new();
}

public class ParsedAttachDump
{
    public int AttachmentCount { get; set; }
    public int ExecuteType { get; set; }
    public byte PartialSkeletonIdx { get; set; }
    public uint BoneIdx { get; set; }
    public TransformDump? OffsetTransform { get; set; }
    public ParsedSkeletonDump? TargetSkeleton { get; set; }
    public ParsedSkeletonDump? OwnerSkeleton { get; set; }
}

public class ParsedSkeletonDump
{
    public TransformDump Transform { get; set; } = TransformDump.Identity;
    public List<ParsedPartialSkeletonDump> PartialSkeletons { get; set; } = [];
}

public class ParsedPartialSkeletonDump
{
    public string? HandlePath { get; set; }
    public int ConnectedBoneIndex { get; set; }
    public uint BoneCount { get; set; }
    public ParsedHkaSkeletonDump? HkSkeleton { get; set; }
    public List<ParsedHkaPoseDump> Poses { get; set; } = [];
}

public class ParsedHkaSkeletonDump
{
    public List<string?> BoneNames { get; set; } = [];
    public List<short> BoneParents { get; set; } = [];
    public List<TransformDump> ReferencePose { get; set; } = [];
}

public class ParsedHkaPoseDump
{
    public List<TransformDump> Pose { get; set; } = [];
}

public record struct TransformDump(float[] Translation, float[] Rotation, float[] Scale)
{
    public static TransformDump Identity => new([0, 0, 0], [0, 0, 0, 1], [1, 1, 1]);

    public static TransformDump From(Vector3 translation, Quaternion rotation, Vector3 scale) => new(
        [translation.X, translation.Y, translation.Z],
        [rotation.X, rotation.Y, rotation.Z, rotation.W],
        [scale.X, scale.Y, scale.Z]);

    public static TransformDump From(Transform t) => From(t.Translation, t.Rotation, t.Scale);

    public readonly Transform ToTransform() => new(
        new Vector3(Translation[0], Translation[1], Translation[2]),
        new Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3]),
        new Vector3(Scale[0], Scale[1], Scale[2]));

    public readonly AffineTransform ToAffine() => new(
        new Vector3(Scale[0], Scale[1], Scale[2]),
        new Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3]),
        new Vector3(Translation[0], Translation[1], Translation[2]));
}
