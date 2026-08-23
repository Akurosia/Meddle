using System.ComponentModel;
using System.Numerics;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Skeletons;
using Meddle.Utils;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace Meddle.Plugin.Utils;

public static class SkeletonUtils
{
    public enum PoseMode
    {
        [Description("Reference Pose")]
        None,
        [Description("Reference Pose with Scale")]
        LocalScaleOnly,
        [Description("Pose")]
        Local
    }
    
    public static (List<BoneNodeBuilder> List, BoneNodeBuilder Root)[] GetBoneMaps(
        ParsedSkeleton skeleton, PoseMode poseMode)
    {
        List<BoneNodeBuilder> boneMap = new();
        List<BoneNodeBuilder> rootList = new();

        for (var partialIdx = 0; partialIdx < skeleton.PartialSkeletons.Count; partialIdx++)
        {
            var partial = skeleton.PartialSkeletons[partialIdx];
            var hkSkeleton = partial.HkSkeleton;
            if (hkSkeleton == null)
                continue;

            var skeleBones = new BoneNodeBuilder[hkSkeleton.BoneNames.Count];
            for (var i = 0; i < hkSkeleton.BoneNames.Count; i++)
            {
                var name = hkSkeleton.BoneNames[i];
                if (string.IsNullOrEmpty(name))
                    continue;

                if (boneMap.FirstOrDefault(b => b.BoneName.Equals(name, StringComparison.OrdinalIgnoreCase)) is
                    { } dupeBone)
                {
                    skeleBones[i] = dupeBone;
                    continue;
                }

                var bone = new BoneNodeBuilder(name)
                {
                    BoneIndex = i,
                    PartialSkeletonHandle = partial.HandlePath ??
                                            throw new InvalidOperationException(
                                                $"No handle path for {name} [{partialIdx},{i}]"),
                    PartialSkeletonIndex = partialIdx,
                };

                var boneTransform = hkSkeleton.ReferencePose[i].AffineTransform;
                bone.SetLocalTransform(boneTransform, false);

                var parentIdx = hkSkeleton.BoneParents[i];
                if (parentIdx != -1)
                {
                    skeleBones[parentIdx].AddNode(bone);
                }
                else
                {
                    rootList.Add(bone);
                }

                skeleBones[i] = bone;
                boneMap.Add(bone);
            }
        }

        var boneMapList = new List<(List<BoneNodeBuilder> List, BoneNodeBuilder Root)>();
        foreach (var root in rootList)
        {
            var bones = NodeBuilder.Flatten(root).Cast<BoneNodeBuilder>().ToList();
            if (!NodeBuilder.IsValidArmature(bones))
            {
                throw new InvalidOperationException($"Armature is invalid, {string.Join(", ", bones.Select(x => x.BoneName))}");
            }
            
            boneMapList.Add((bones, root));
        }

        var boneMaps = boneMapList.ToArray();

        if (poseMode != PoseMode.None)
        {
            foreach (var map in boneMaps)
            {
                foreach (var bone in map.List)
                {
                    var boneTransform = GetBoneTransform(skeleton, bone);
                    if (boneTransform == null)
                    {
                        continue;
                    }

                    AddBoneKeyframe(bone, poseMode, 0, boneTransform.Value);
                }
            }
        }

        return boneMaps;
    }
    
    private static AffineTransform? GetAppliedBoneTransform(AttachSet attachSet, BoneNodeBuilder bone)
    {
        if (bone.Parent is not BoneNodeBuilder && 
            attachSet is { OwnerId: not null, AttachBoneName: not null, Attach.OffsetTransform: { } offset })
        {
            return offset.AffineTransform;
        }

        return GetBoneTransform(attachSet.Skeleton, bone);
    }
    
    public static List<BoneNodeBuilder> GetBoneMap(ParsedSkeleton skeleton, PoseMode poseMode, out BoneNodeBuilder? root)
    {
        var maps = GetBoneMaps(skeleton, poseMode);
        if (maps.Length == 0)
        {
            root = null;
            return [];
        }

        // Prefer n_root when a skeleton has multiple roots (e.g. Air-Wheeler A9 mount's n_pluslayer).
        var rootMap = maps.FirstOrDefault(x => x.Root.BoneName.Equals("n_root", StringComparison.OrdinalIgnoreCase));
        if (rootMap != default)
        {
            root = rootMap.Root;
            return rootMap.List;
        }
        
        var map0 = maps[0];
        root = map0.Root;
        return map0.List;
    }

    public record AttachGrouping(List<BoneNodeBuilder> Bones, BoneNodeBuilder? Root, List<(DateTime Time, AttachSet Attach)> Timeline);

    // Groups captured frames into per-attach timelines and builds each attach's keyframed bone map.
    public static Dictionary<string, AttachGrouping> GetAnimatedBoneMap((DateTime Time, AttachSet[] Attaches)[] frames)
    {
        var attachDict = new Dictionary<string, AttachGrouping>();
        var attachTimelines = new Dictionary<string, List<(DateTime Time, AttachSet Attach)>>();
        foreach (var frame in frames)
        {
            foreach (var attach in frame.Attaches)
            {
                var timelineName = $"{attach.Id}_{attach.Name}";
                if (!attachTimelines.TryGetValue(timelineName, out var timeline))
                {
                    timeline = [];
                    attachTimelines.Add(timelineName, timeline);
                }

                timeline.Add((frame.Time, attach));
            }
        }

        var startTime = frames.Min(x => x.Time);
        foreach (var (attachId, timeline) in attachTimelines)
        {
            ProcessTimeline(attachId, timeline, attachDict, startTime);
        }

        return attachDict;
    }

    private static void ProcessTimeline(
        string attachId,
        List<(DateTime Time, AttachSet Attach)> timeline,
        Dictionary<string, AttachGrouping> attachDict,
        DateTime startTime)
    {
        if (!attachDict.TryGetValue(attachId, out var attachBoneMap))
        {
            attachBoneMap = new AttachGrouping([], null, timeline);
            attachDict.Add(attachId, attachBoneMap);
        }

        if (timeline.Count == 0)
        {
            return;
        }

        var lastTransforms = new Dictionary<string, (AffineTransform Transform, float Time, bool KeyframeSet)>();
        (int ExecuteType, string? BoneName)? lastAttachPoint = null;
        var prevFrameTime = 0f;
        foreach (var time in timeline.Select(x => x.Time).Distinct())
        {
            var frame = timeline.FirstOrDefault(x => x.Time == time);
            var frameTime = TotalSeconds(frame.Time, startTime);

            var boneMap = GetBoneMap(frame.Attach.Skeleton, PoseMode.None, out var attachRoot);
            if (attachRoot == null)
                continue;

            if (attachBoneMap.Root == null)
            {
                attachBoneMap = attachBoneMap with { Root = attachRoot };
            }

            // If attach point changes, hold original bone until the switch happens to avoid lerping
            var attachPoint = (frame.Attach.Attach.ExecuteType, frame.Attach.AttachBoneName);
            float? switchHoldTime = null;
            if (lastAttachPoint != null && lastAttachPoint.Value != attachPoint)
            {
                var epsilon = Math.Min(0.01f, (frameTime - prevFrameTime) * 0.25f);
                switchHoldTime = frameTime - epsilon;
            }

            foreach (var attachBone in boneMap)
            {
                var currentTransform = GetAppliedBoneTransform(frame.Attach, attachBone);
                if (currentTransform == null)
                    continue;

                var bone = attachBoneMap.Bones.FirstOrDefault(x => x.BoneName.Equals(attachBone.BoneName, StringComparison.OrdinalIgnoreCase));
                if (bone == null)
                {
                    attachBoneMap.Bones.Add(attachBone);
                    bone = attachBone;
                }

                var boneName = attachBone.BoneName;
                if (!lastTransforms.TryGetValue(boneName, out var value))
                {
                    AddBoneKeyframe(bone, PoseMode.Local, frameTime, currentTransform.Value);
                    lastTransforms[boneName] = (currentTransform.Value, frameTime, true);
                    continue;
                }

                var (lastTransform, lastTime, keyframeSet) = value;

                var current = currentTransform.Value;
                var alignedRotation = HemisphereAlign(current.Rotation, lastTransform.Rotation);
                if (alignedRotation != current.Rotation)
                {
                    current = new AffineTransform(current.Scale, alignedRotation, current.Translation);
                }

                var isSame = IsSameTransform(current, lastTransform);

                // Skip if same, but mark in-case we need to write a hold value for a later change
                if (isSame && !IsLastFrameInTimeline(timeline, frame.Time))
                {
                    lastTransforms[boneName] = (lastTransform, frameTime, false);
                    continue;
                }

                if (!isSame)
                {
                    if (switchHoldTime is { } holdTime && holdTime > lastTime)
                    {
                        AddBoneKeyframe(bone, PoseMode.Local, holdTime, lastTransform);
                    }
                    else if (!keyframeSet)
                    {
                        // Close the held span with the held value, not the current frame's.
                        AddBoneKeyframe(bone, PoseMode.Local, lastTime, lastTransform);
                    }
                }

                AddBoneKeyframe(bone, PoseMode.Local, frameTime, current);
                lastTransforms[boneName] = (current, frameTime, true);
            }

            prevFrameTime = frameTime;
            lastAttachPoint = attachPoint;
            attachDict[attachId] = attachBoneMap;
        }
    }
    
    private static bool IsLastFrameInTimeline(
        List<(DateTime Time, AttachSet Attach)> timeline, DateTime time)
    {
        return timeline.LastOrDefault().Time == time;
    }

    // Flips current to match previous's hemisphere (q and -q are the same rotation) so adjacent
    // keys never interpolate the long way around.
    public static Quaternion HemisphereAlign(Quaternion current, Quaternion previous) =>
        Quaternion.Dot(current, previous) < 0 ? Quaternion.Negate(current) : current;
    
    private static void AddBoneKeyframe(BoneNodeBuilder bone, PoseMode poseMode, float time, AffineTransform transform)
    {
        bone.UseScale().UseTrackBuilder("pose").WithPoint(time, transform.Scale);
        
        if (poseMode != PoseMode.LocalScaleOnly)
        {
            bone.UseRotation().UseTrackBuilder("pose").WithPoint(time, transform.Rotation);
            bone.UseTranslation().UseTrackBuilder("pose").WithPoint(time, transform.Translation);
        }
    }

    private static bool IsSameTransform(AffineTransform current, AffineTransform previous, float tolerance = 0.0001f)
    {
        return IsVectorSame(current.Scale, previous.Scale, tolerance) &&
               IsQuaternionSame(current.Rotation, previous.Rotation, tolerance) &&
               IsVectorSame(current.Translation, previous.Translation, tolerance);
    }

    private static bool IsVectorSame(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float tolerance)
    {
        return Math.Abs(a.X - b.X) < tolerance &&
               Math.Abs(a.Y - b.Y) < tolerance &&
               Math.Abs(a.Z - b.Z) < tolerance;
    }

    private static bool IsQuaternionSame(System.Numerics.Quaternion a, System.Numerics.Quaternion b, float tolerance)
    {
        return Math.Abs(a.X - b.X) < tolerance &&
               Math.Abs(a.Y - b.Y) < tolerance &&
               Math.Abs(a.Z - b.Z) < tolerance &&
               Math.Abs(a.W - b.W) < tolerance;
    }
    
    public static Matrix4x4 ToMatrix(AffineTransform t) =>
        Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Translation);

    public static Matrix4x4 ComputeBoneWorldMatrix(
        NodeBuilder? bone, ParsedSkeleton skeleton, Matrix4x4 instanceWorldTransform, NodeBuilder? stopAt = null)
    {
        var world = Matrix4x4.Identity;
        var c = bone;
        while (c != null)
        {
            if (c is BoneNodeBuilder { IsGenerated: false } boneNode &&
                GetBoneTransform(skeleton, boneNode) is { } poseTransform)
            {
                world *= ToMatrix(poseTransform);
            }
            else
            {
                world *= c.LocalMatrix;
            }

            if (c == stopAt || c.Parent == null)
            {
                world *= instanceWorldTransform;
            }

            c = c.Parent;
        }

        return world;
    }

    public static AffineTransform? GetBoneTransform(ParsedSkeleton skeleton, BoneNodeBuilder bone)
    {
        var partial = skeleton.PartialSkeletons[bone.PartialSkeletonIndex];
        if (partial.Poses.Count == 0) return null;
            
        var pose = partial.Poses[0];
        var boneTransform = pose.Pose[bone.BoneIndex].AffineTransform;

        if (bone.Parent is not BoneNodeBuilder)
        {
            var scale = boneTransform.Scale * skeleton.Transform.Scale;
            return new AffineTransform(scale, boneTransform.Rotation, boneTransform.Translation);
        }
        
        return boneTransform;
    }
    
    public static float TotalSeconds(DateTime time, DateTime startTime)
    {
        var seconds = (float)(time - startTime).TotalSeconds;
        return seconds < 0.0001f ? 0f : seconds;
    }
}
