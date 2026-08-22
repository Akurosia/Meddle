using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;

namespace Meddle.Utils;

/// <summary>Near-zero-area triangle skinned to every bone, so AddSkinnedMesh has something to attach the skeleton to.</summary>
public static class DummyMesh
{
    public static MeshBuilder<VertexPosition, VertexEmpty, VertexJoints4> Create(string name = "DUMMY_MESH")
    {
        var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexJoints4>(name);
        var material = new MaterialBuilder("material");

        var p1 = new VertexPosition { Position = new Vector3(0.000001f, 0, 0) };
        var p2 = new VertexPosition { Position = new Vector3(0, 0.000001f, 0) };
        var p3 = new VertexPosition { Position = new Vector3(0, 0, 0.000001f) };

        mesh.UsePrimitive(material).AddTriangle(
            (p1, new VertexEmpty(), new VertexJoints4(0)),
            (p2, new VertexEmpty(), new VertexJoints4(0)),
            (p3, new VertexEmpty(), new VertexJoints4(0)));

        return mesh;
    }
}
