using System.Numerics;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Meddle.Plugin.Models.Composer;

/// <summary>
/// Material-vertex for grass blade point exports. Each point is one blade instance from a
/// .ggd file; the custom attributes carry the raw instance fields.
/// </summary>
public struct GrassBladeVertex : IVertexCustom
{
    public const string RotationAttribute = "_ROTATION";
    public const string ScaleAttribute = "_SCALE";
    public const string InfoAttribute = "_INFO";
    public const string BendAttribute = "_BEND";
    public const string RandRangeAttribute = "_RANDRANGE";

    /// <summary>COLOR_0: (instance ColorR, ColorG, 0, 1).</summary>
    public Vector4 Color;
    /// <summary>Instance rotation (x, y, z, w).</summary>
    public Vector4 Rotation;
    /// <summary>ScaleA = x/z, ScaleB = y, ScaleMul, unused</summary>
    public Vector4 Scale;
    /// <summary>blade TypeIndex 0..7, blade LOD group 0..2, Flags, Param/255</summary>
    public Vector4 Info;
    /// <summary>bend amount/255, bend scale/255*0.5+1</summary>
    public Vector2 Bend;
    /// <summary>ScaleMin, ScaleMax, RotMin, RotMax</summary>
    public Vector4 RandRange;

    public int MaxColors => 1;
    public int MaxTextCoords => 0;

    public IEnumerable<KeyValuePair<string, AttributeFormat>> GetEncodingAttributes()
    {
        yield return new("COLOR_0", new AttributeFormat(DimensionType.VEC4));
        yield return new(RotationAttribute, new AttributeFormat(DimensionType.VEC4));
        yield return new(ScaleAttribute, new AttributeFormat(DimensionType.VEC4));
        yield return new(InfoAttribute, new AttributeFormat(DimensionType.VEC4));
        yield return new(BendAttribute, new AttributeFormat(DimensionType.VEC2));
        yield return new(RandRangeAttribute, new AttributeFormat(DimensionType.VEC4));
    }

    public IEnumerable<string> CustomAttributes =>
        [RotationAttribute, ScaleAttribute, InfoAttribute, BendAttribute, RandRangeAttribute];

    public Vector4 GetColor(int index)
    {
        if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
        return Color;
    }

    public Vector2 GetTexCoord(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetColor(int setIndex, Vector4 color)
    {
        if (setIndex == 0) Color = color;
    }

    public void SetTexCoord(int setIndex, Vector2 coord) { }

    public bool TryGetCustomAttribute(string attributeName, out object? value)
    {
        switch (attributeName)
        {
            case RotationAttribute: value = Rotation; return true;
            case ScaleAttribute: value = Scale; return true;
            case InfoAttribute: value = Info; return true;
            case BendAttribute: value = Bend; return true;
            case RandRangeAttribute: value = RandRange; return true;
            default: value = null; return false;
        }
    }

    public void SetCustomAttribute(string attributeName, object value)
    {
        switch (attributeName)
        {
            case RotationAttribute: Rotation = (Vector4)value; break;
            case ScaleAttribute: Scale = (Vector4)value; break;
            case InfoAttribute: Info = (Vector4)value; break;
            case BendAttribute: Bend = (Vector2)value; break;
            case RandRangeAttribute: RandRange = (Vector4)value; break;
        }
    }

    public void Validate() { }

    public void Add(in VertexMaterialDelta delta) { }

    public VertexMaterialDelta Subtract(IVertexMaterial baseValue) => VertexMaterialDelta.Zero;
}
