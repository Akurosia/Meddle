using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.AnimationDump;
using Meddle.Plugin.UI;
using Meddle.Plugin.Utils;
using Meddle.Utils;
using Microsoft.Extensions.Logging;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace Meddle.Plugin.Services;

public class AnimationExportService : IDisposable, IService
{
    private readonly ILogger<AnimationExportService> logger;

    public AnimationExportService(ILogger<AnimationExportService> logger)
    {
        this.logger = logger;
    }

    public void Dispose()
    {
        logger.LogDebug("Disposing ExportUtil");
    }

    // Exports three glTF files per attach set (actor, weapon, ornament ...):
    //   *_skeleton.glb - bone-local pose keyframes (skinned dummy mesh carries the armature)
    //   *_absolute.glb - object placement in world space, one animated node
    //   *_local.glb    - object placement relative to the capture's first frame
    public void ExportAnimation(
        List<(DateTime, AttachSet[])> frames,
        AnimationExportSettings settings,
        CancellationToken token = default)
    {
        try
        {
            var boneSets = SkeletonUtils.GetAnimatedBoneMap(frames.ToArray());
            var startTime = frames.Min(x => x.Item1);
            var folder = settings.Path;
            Directory.CreateDirectory(folder);
            // Frames indexed by time so an attach can find its owner's AttachSet in the same frame.
            var frameLookup = frames.ToDictionary(f => f.Item1, f => f.Item2);
            // Owner bone maps are structure-only (poses read per frame), so build once per owner.
            var ownerBoneCache = new Dictionary<string, (List<BoneNodeBuilder> Bones, BoneNodeBuilder? Root)>();
            foreach (var (id, (bones, root, timeline)) in boneSets)
            {
                if (token.IsCancellationRequested) return;
                if (root == null) throw new InvalidOperationException("Root bone not found");
                logger.LogInformation("Adding bone set {Id}", id);

                // Skeleton file: bone-local pose animation only, no root motion.
                var skeletonScene = new SceneBuilder();
                var skeletonRootNode = new NodeBuilder(id) { Extras = MakeExtras("Skeleton") };
                skeletonRootNode.AddNode(root);
                skeletonScene.AddNode(skeletonRootNode);
                skeletonScene.AddSkinnedMesh(GetDummyMesh(), Matrix4x4.Identity, bones.Cast<NodeBuilder>().ToArray());
                SaveScene(skeletonScene, folder, id, "skeleton");

                // Position files: one animated node each, placement independent of the bone poses.
                var absoluteRoot = new NodeBuilder($"{id}_absolute") { Extras = MakeExtras("AbsolutePosition") };
                var localRoot = new NodeBuilder($"{id}_local") { Extras = MakeExtras("LocalPosition") };

                Vector3? startPos = null;
                Quaternion? startRot = null;
                Vector3? startScale = null;
                (int ExecuteType, string? BoneName)? lastAttachPoint = null;
                (Vector3 Pos, Quaternion Rot, Vector3 Scale)? lastAbsoluteSample = null;
                (Vector3 Pos, Quaternion Rot, Vector3 Scale)? lastLocalSample = null;
                var lastKeyTime = 0f;
                foreach (var (frameTime, attach) in timeline)
                {
                    if (token.IsCancellationRequested) return;

                    var pos = attach.Transform.Translation;
                    var rot = attach.Transform.Rotation;
                    var scale = attach.Transform.Scale;
                    var time = SkeletonUtils.TotalSeconds(frameTime, startTime);

                    // Attached items: place at the owner attach-bone's world transform (matching the
                    // static exporter; the attach's OffsetTransform lives on its root bone instead).
                    if (attach is { OwnerId: not null, AttachBoneName: not null, Attach.OwnerSkeleton: not null } &&
                        frameLookup.TryGetValue(frameTime, out var siblingAttaches))
                    {
                        var ownerAttach = siblingAttaches.FirstOrDefault(a => a.Id == attach.OwnerId);
                        if (ownerAttach != null)
                        {
                            try
                            {
                                if (!ownerBoneCache.TryGetValue(attach.OwnerId, out var ownerBones))
                                {
                                    var list = SkeletonUtils.GetBoneMap(
                                        attach.Attach.OwnerSkeleton, SkeletonUtils.PoseMode.None, out var ownerRoot);
                                    ownerBones = (list, ownerRoot);
                                    ownerBoneCache[attach.OwnerId] = ownerBones;
                                }

                                var attachPointBone = ownerBones.Bones.FirstOrDefault(
                                    b => b.BoneName.Equals(attach.AttachBoneName, StringComparison.OrdinalIgnoreCase));
                                if (attachPointBone != null)
                                {
                                    var ownerWorldMatrix = SkeletonUtils.ToMatrix(ownerAttach.Transform);
                                    var ownerWorld = SkeletonUtils.ComputeBoneWorldMatrix(
                                        attachPointBone, attach.Attach.OwnerSkeleton, ownerWorldMatrix);
                                    var decomposed = new Transform(SnapAffine(ownerWorld));
                                    pos = decomposed.Translation;
                                    rot = decomposed.Rotation;
                                    scale = decomposed.Scale;
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to compute owner-bone placement for {Id}, falling back to raw transform", attach.Id);
                            }
                        }
                    }

                    startPos ??= pos;
                    startRot ??= rot;
                    startScale ??= scale;

                    Transform localTransform;
                    try
                    {
                        localTransform = new Transform(SnapAffine(ComputeLocalDelta(
                            startScale.Value, startRot.Value, startPos.Value, scale, rot, pos)));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to compute local-relative transform for {Id} at frame {FrameTime} " +
                            "(start: pos={StartPos} rot={StartRot} scale={StartScale}; current: pos={Pos} rot={Rot} scale={Scale}), " +
                            "falling back to the absolute transform for this frame",
                            attach.Id, frameTime, startPos, startRot, startScale, pos, rot, scale);
                        localTransform = new Transform(pos, rot, scale);
                    }

                    if (lastAbsoluteSample is { } lastAbs)
                    {
                        rot = SkeletonUtils.HemisphereAlign(rot, lastAbs.Rot);
                    }

                    var localRotation = localTransform.Rotation;
                    if (lastLocalSample is { } lastLoc)
                    {
                        localRotation = SkeletonUtils.HemisphereAlign(localRotation, lastLoc.Rot);
                    }

                    // Hold the previous placement until just before an attach-point switch, so the
                    // switch is a step instead of a lerp across the whole inter-sample gap.
                    var attachPoint = (attach.Attach.ExecuteType, attach.AttachBoneName);
                    if (lastAttachPoint != null && lastAttachPoint.Value != attachPoint && lastAbsoluteSample != null)
                    {
                        var epsilon = Math.Min(0.01f, (time - lastKeyTime) * 0.25f);
                        var holdTime = time - epsilon;
                        WriteSample(absoluteRoot, holdTime, lastAbsoluteSample.Value);
                        WriteSample(localRoot, holdTime, lastLocalSample!.Value);
                    }

                    WriteSample(absoluteRoot, time, (pos, rot, scale));
                    WriteSample(localRoot, time, (localTransform.Translation, localRotation, localTransform.Scale));

                    lastAttachPoint = attachPoint;
                    lastAbsoluteSample = (pos, rot, scale);
                    lastLocalSample = (localTransform.Translation, localRotation, localTransform.Scale);
                    lastKeyTime = time;
                }

                var absoluteScene = new SceneBuilder();
                absoluteScene.AddNode(absoluteRoot);
                SaveScene(absoluteScene, folder, id, "absolute");

                var localScene = new SceneBuilder();
                localScene.AddNode(localRoot);
                SaveScene(localScene, folder, id, "local");
            }

            logger.LogInformation("Export complete");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to export animation");
            throw;
        }
    }

    private static void WriteSample(NodeBuilder node, float time, (Vector3 Pos, Quaternion Rot, Vector3 Scale) sample)
    {
        node.UseTranslation().UseTrackBuilder("pose").WithPoint(time, sample.Pos);
        node.UseRotation().UseTrackBuilder("pose").WithPoint(time, sample.Rot);
        node.UseScale().UseTrackBuilder("pose").WithPoint(time, sample.Scale);
    }

    // Delta that satisfies delta * start == current (row-vector convention), so MeddleTools can
    // compose it onto the target's own starting placement (matrix_world = initial @ wrapper).
    private static Matrix4x4 ComputeLocalDelta(
        Vector3 startScale, Quaternion startRot, Vector3 startPos,
        Vector3 scale, Quaternion rot, Vector3 pos)
    {
        // Transform's sanitization guards against degenerate captured frames (zero scale, non-finite).
        var startMatrix = SkeletonUtils.ToMatrix(new Transform(startPos, startRot, startScale).AffineTransform);
        var currentMatrix = SkeletonUtils.ToMatrix(new Transform(pos, rot, scale).AffineTransform);
        if (Matrix4x4.Invert(startMatrix, out var startInverse))
        {
            // Order matters: Inverse(start) * current swings unboundedly with the current rotation.
            var delta = currentMatrix * startInverse;
            if (IsValidAffineMatrix(delta))
            {
                return delta;
            }
        }

        if (IsValidAffineMatrix(currentMatrix))
        {
            return currentMatrix;
        }

        // CreateTranslation is structurally affine no matter how bad the captured TRS was.
        return Matrix4x4.CreateTranslation(pos);
    }

    private static bool IsValidAffineMatrix(Matrix4x4 m)
    {
        const float epsilon = 1e-4f;
        return float.IsFinite(m.M14) && float.IsFinite(m.M24) && float.IsFinite(m.M34) && float.IsFinite(m.M44) &&
               MathF.Abs(m.M14) < epsilon && MathF.Abs(m.M24) < epsilon && MathF.Abs(m.M34) < epsilon &&
               MathF.Abs(m.M44 - 1f) < epsilon;
    }

    // Snap the last column to exactly (0,0,0,1) - SharpGLTF's AffineTransform ctor rejects even
    // one ULP of float error, routine after Invert/multiply on world-scale translations.
    private static Matrix4x4 SnapAffine(Matrix4x4 m)
    {
        m.M14 = 0f;
        m.M24 = 0f;
        m.M34 = 0f;
        m.M44 = 1f;
        return m;
    }

    private static JsonNode? MakeExtras(string nodeType) =>
        JsonNode.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { { "nodeType", nodeType } }));

    private static readonly JsonSerializerOptions RawDumpJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    
    public void ExportRawDump(
        List<(DateTime, AttachSet[])> frames,
        AnimationExportSettings settings,
        CancellationToken token = default)
    {
        try
        {
            var folder = settings.Path;
            Directory.CreateDirectory(folder);
            var dump = frames.ToDump();
            var outputPath = Path.Combine(folder, "raw_capture.json.gz");
            using (var fileStream = File.Create(outputPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                JsonSerializer.Serialize(gzipStream, dump, RawDumpJsonOptions);
            }

            logger.LogInformation("Raw animation dump exported to {Path}", outputPath);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to export raw animation dump");
            throw;
        }
    }

    private void SaveScene(SceneBuilder scene, string folder, string id, string kind)
    {
        var sceneGraph = scene.ToGltf2();
        var outputPath = Path.Combine(folder, $"motion_{id}_{kind}.glb");
        sceneGraph.SaveGLB(outputPath);
    }

    // https://github.com/0ceal0t/Dalamud-VFXEditor/blob/be00131b93b3c6dd4014a4f27c2661093daf3a85/VFXEditor/Utils/Gltf/GltfSkeleton.cs#L132
    public static MeshBuilder<VertexPosition, VertexEmpty, VertexJoints4> GetDummyMesh(string name = "DUMMY_MESH")
    {
        var dummyMesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexJoints4>(name);
        var material = new MaterialBuilder("material");

        var p1 = new VertexPosition
        {
            Position = new Vector3(0.000001f, 0, 0)
        };
        var p2 = new VertexPosition
        {
            Position = new Vector3(0, 0.000001f, 0)
        };
        var p3 = new VertexPosition
        {
            Position = new Vector3(0, 0, 0.000001f)
        };

        dummyMesh.UsePrimitive(material).AddTriangle(
            (p1, new VertexEmpty(), new VertexJoints4(0)),
            (p2, new VertexEmpty(), new VertexJoints4(0)),
            (p3, new VertexEmpty(), new VertexJoints4(0))
        );

        return dummyMesh;
    }
}
