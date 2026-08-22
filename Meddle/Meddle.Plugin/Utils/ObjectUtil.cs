using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Interop;
using SharpGLTF.Transforms;

namespace Meddle.Plugin.Utils;

public static class ObjectUtil
{
    public static AffineTransform ToAffine(this FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform transform)
    {
        return new AffineTransform(transform.Scale, transform.Rotation, transform.Translation);
    }
    
    public static string ToFormatted(this Vector3 vector)
    {
        return $"<X: {vector.X:0.00} Y: {vector.Y:0.00} Z: {vector.Z:0.00}>";
    }

    public static string ToFormatted(this Quaternion vector)
    {
        return $"<X: {vector.X:0.00} Y: {vector.Y:0.00} Z: {vector.Z:0.00} W: {vector.W:0.00}>";
    }
    
    public static unsafe List<(string Label, Pointer<DrawObject> DrawObject)> GetAttachedDrawObjects(Character* character)
    {
        var results = new List<(string, Pointer<DrawObject>)>();

        Pointer<DrawObject> mainDrawObject = character->DrawObject;
        if (mainDrawObject != null)
            results.Add(("Character", mainDrawObject));

        var ornament = character->OrnamentData.OrnamentObject;
        if (ornament != null && ornament->DrawObject != null)
        {
            Pointer<DrawObject> ornamentDrawObject = ornament->DrawObject;
            results.Add(("Ornament", ornamentDrawObject));
        }

        var mount = character->Mount.MountObject;
        if (mount != null && mount->DrawObject != null)
        {
            Pointer<DrawObject> mountDrawObject = mount->DrawObject;
            results.Add(("Mount", mountDrawObject));
        }

        var companion = character->CompanionData.CompanionObject;
        if (companion != null && companion->DrawObject != null)
        {
            Pointer<DrawObject> companionDrawObject = companion->DrawObject;
            results.Add(("Companion", companionDrawObject));
        }

        var weapons = character->DrawData.WeaponData;
        for (var i = 0; i < weapons.Length; i++)
        {
            Pointer<DrawObject> weaponDrawObject = weapons[i].DrawData.DrawObject;
            if (weaponDrawObject != null)
                results.Add(($"Weapon {i}", weaponDrawObject));
        }

        return results;
    }
}
