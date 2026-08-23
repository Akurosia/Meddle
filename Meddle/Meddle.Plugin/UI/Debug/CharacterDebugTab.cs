using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Structs;
using Meddle.Plugin.Services;
using Meddle.Plugin.Utils;

namespace Meddle.Plugin.UI.Debug;

public class CharacterDebugTab : IService
{
    private readonly CommonUi commonUi;
    private ICharacter? selectedCharacter;
    private readonly IGameGui gui;
    private string boneSearch = "";

    private enum BoneMode
    {
        Local,
        ModelPropagate,
        ModelNoPropagate,
        ModelRaw
    }

    private BoneMode boneModeInput = BoneMode.ModelPropagate;
    
    
    public CharacterDebugTab(IGameGui gui, CommonUi commonUi)
    {
        this.gui = gui;
        this.commonUi = commonUi;
    }

    public unsafe void Draw()
    {
        using var indent = ImRaii.PushIndent();
        commonUi.DrawCharacterSelect(ref selectedCharacter);
        if (selectedCharacter == null)
        {
            ImGui.Text("No characters found");
            return;
        }

        // player address
        ImGui.Text($"Address: {selectedCharacter.Address:X8}");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Click to copy");
        }
        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText($"{selectedCharacter.Address:X8}");
        }


        var character = (Character*)selectedCharacter.Address;
        if (character == null)
        {
            ImGui.Text("Character is null");
            return;
        }

        foreach (var (label, drawObjectPtr) in ObjectUtil.GetAttachedDrawObjects(character))
        {
            var drawObject = drawObjectPtr.Value;
            ImGui.Text($"{label} DrawObject Address: {(nint)drawObject:X8}");
            var objectType = drawObject->GetObjectType();
            ImGui.Text($"{label} Object Type: {objectType}");
            if (objectType == ObjectType.CharacterBase)
            {
                var cBase = (CharacterBase*)drawObject;
                DrawCharacterBase(cBase, label);
            }
        }
    }
    
    public unsafe void DrawCharacterBase(CharacterBase* cBase, string name)
    {
        if (cBase == null)
        {
            ImGui.Text($"{name} CharacterBase is null");
            return;
        }
        using var id = ImRaii.PushId($"{(nint)cBase:X8}");
        if (ImGui.CollapsingHeader(name))
        {
            ImGui.Text($"Visible: {cBase->IsVisible}");
            ImGui.Text($"ModelType: {cBase->GetModelType()}");

            if (cBase->GetModelType() == CharacterBase.ModelType.Human)
            {
                var human = (Human*)cBase;
                ImGui.Text($"RaceSexId: {human->RaceSexId}");
                ImGui.Text($"HairId: {human->HairId}");
                ImGui.Text($"FaceId: {human->FaceId}");
                ImGui.Text($"TailEarId: {human->TailEarId}");
                ImGui.Text($"FurId: {human->FurId}");

                ImGui.Text($"Highlights: {human->Customize.Highlights}");
                ImGui.Text($"Lipstick: {human->Customize.Lipstick}");
            }

            var skeleton = cBase->Skeleton;
            if (skeleton == null)
            {
                ImGui.Text($"{name} Skeleton is null");
            }
            else
            {
                ImGui.Text($"Skeleton: {(nint)cBase->Skeleton:X8}");
                ImGui.Text($"Partial Skeleton Count: {cBase->Skeleton->PartialSkeletonCount}");
                using var skeletonIndent = ImRaii.PushIndent();
                if (ImGui.CollapsingHeader("Draw Bones"))
                {
                    // boneMode
                    ImGui.Text("Bone Mode");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200);
                    using (var combo = ImRaii.Combo("##BoneMode", boneModeInput.ToString()))
                    {
                        if (combo.Success)
                        {
                            foreach (BoneMode mode in Enum.GetValues(typeof(BoneMode)))
                            {
                                if (ImGui.Selectable(mode.ToString(), mode == boneModeInput))
                                {
                                    boneModeInput = mode;
                                }
                            }
                        }
                    }

                    ImGui.Text("Bone Search");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200);
                    ImGui.InputText("##BoneSearch", ref boneSearch, 100);

                    // imgui select partial skeleton by index
                    for (int i = 0; i < skeleton->PartialSkeletonCount; i++)
                    {
                        using var pskeletonIndent = ImRaii.PushIndent();
                        var partialSkeleton = skeleton->PartialSkeletons[i];
                        var handle = partialSkeleton.SkeletonResourceHandle;
                        if (handle == null) continue;
                        var path = handle->FileName.ParseString();
                        if (ImGui.CollapsingHeader($"Partial Skeleton {i}: {path}"))
                        {
                            var ex = (PartialSkeletonEx*)(&partialSkeleton);
                            var boneCount = ex->BoneCount;
                            ImGui.Text($"Partial Skeleton Bone Count: {boneCount}");
                            using var boneTable = ImRaii.Table($"##BoneTable{i}", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
                            ImGui.TableSetupColumn("Bone", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Parent", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Translation", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Rotation", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Scale", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableHeadersRow();
                            DrawBoneTransformsOnScreen(partialSkeleton, boneModeInput);
                        }
                    }
                }
            }


            if (ImGui.CollapsingHeader("Draw Models"))
            {
                using var skeletonIndent = ImRaii.PushIndent();
                DrawModels(cBase);
            }
        }
    }

    public unsafe void DrawModels(CharacterBase* cBase)
    {
        var models = cBase->ModelsSpan;
        for (var i = 0; i < models.Length; i++)
        {
            var modelPtr = models[i];
            if (ImGui.CollapsingHeader($"Model {i} - {((nint)modelPtr.Value):X8}") && modelPtr.Value != null)
            {
                try
                {
                    using var modelIndent = ImRaii.PushIndent();
                    using var modelId = ImRaii.PushId(i);
                    var model = modelPtr.Value;
                    UiUtil.Text($"Model: {(nint)model:X8}", $"{(nint)model:X8}");
                    ImGui.Text($"Name: {model->ModelResourceHandle->FileName.ToString()}");
                    ImGui.Text($"Material Count: {model->MaterialsSpan.Length}");
                    ImGui.Text($"Bone Count: {model->BoneCount}");

                    if (ImGui.CollapsingHeader("Materials"))
                    {
                        try
                        {
                            using var materialIndent = ImRaii.PushIndent();
                            for (var materialIdx = 0; materialIdx < model->MaterialsSpan.Length; materialIdx++)
                            {
                                var material = model->MaterialsSpan[materialIdx];
                                if (ImGui.CollapsingHeader($"Material {materialIdx} - {(nint)material.Value:X8}") && material.Value != null)
                                {
                                    try
                                    {
                                        using var materialId = ImRaii.PushId(materialIdx);
                                        UiUtil.Text($"Material: {(nint)material.Value:X8}", $"{(nint)material.Value:X8}");
                                        UiUtil.Text($"Material Resource: {(nint)material.Value->MaterialResourceHandle:X8}", $"{(nint)material.Value->MaterialResourceHandle:X8}");
                                        ImGui.Text($"Texture Count: {material.Value->TextureCount}");

                                        for (var j = 0; j < material.Value->TextureCount; j++)
                                        {
                                            try
                                            {
                                                using var textureIndent = ImRaii.PushIndent();
                                                var texture = material.Value->Textures[j];
                                                if (ImGui.CollapsingHeader($"Texture {j} - {(nint)texture.Texture:X8}") && texture.Texture != null)
                                                {
                                                    try
                                                    {
                                                        using var textureId = ImRaii.PushId(j);
                                                        UiUtil.Text($"Texture: {(nint)texture.Texture:X8}", $"{(nint)texture.Texture:X8}");
                                                        ImGui.Text($"Id: {texture.Id}");
                                                        ImGui.Text($"SamplerFlags: {texture.SamplerFlags}");
                                                        ImGui.Text($"Texture: {texture.Texture->FileName.ToString()}");
                                                    }
                                                    catch (Exception e)
                                                    {
                                                        ImGui.Text($"Error: {e.Message}");
                                                    }
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                ImGui.Text($"Error: {e.Message}");
                                            }
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        ImGui.Text($"Error: {e.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            ImGui.Text($"Error: {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    ImGui.Text($"Error: {e.Message}");
                }
            }
        }
    }

    private unsafe void DrawBoneTransformsOnScreen(PartialSkeleton partialSkeleton, BoneMode boneMode)
    {
        var rootPos = partialSkeleton.Skeleton->Transform;
        var rootTransform = new Transform(rootPos);
        var ex = (PartialSkeletonEx*)(&partialSkeleton);
        var pose = partialSkeleton.GetHavokPose(0);
        for (var i = 0; i < ex->BoneCount; i++)
        {
            var bone = pose->Skeleton->Bones[i];
            if (!string.IsNullOrEmpty(boneSearch) && bone.Name.String != null && !bone.Name.String.Contains(boneSearch))
            {
                continue;
            }

            var t = boneMode switch
            {
                BoneMode.Local => new Transform(pose->LocalPose[i]),
                BoneMode.ModelPropagate => new Transform(*pose->AccessBoneModelSpace(i, hkaPose.PropagateOrNot.Propagate)),
                BoneMode.ModelNoPropagate => new Transform(*pose->AccessBoneModelSpace(i, hkaPose.PropagateOrNot.DontPropagate)),
                BoneMode.ModelRaw => new Transform(pose->ModelPose[i]),
                _ => new Transform(pose->ModelPose[i])
            };

            var modelTransform = boneMode == BoneMode.Local ? new Transform(pose->ModelPose[i]) : t;
            var worldMatrix = modelTransform.AffineTransform.Matrix * rootTransform.AffineTransform.Matrix;
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text($"[{i}] {bone.Name.String}");
            var dotColorRgb = new Vector4(1, 1, 1, 0.5f);
            if (ImGui.IsItemHovered())
            {
                dotColorRgb = new Vector4(1, 0, 0, 0.5f);
            }

            ImGui.TableSetColumnIndex(1);
            var parentIndex = pose->Skeleton->ParentIndices[i];
            ImGui.Text(parentIndex.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{t.Translation:F3}");
            ImGui.TableSetColumnIndex(3);
            ImGui.Text($"X:{t.Rotation.X:F2} Y:{t.Rotation.Y:F2} " +
                       $"Z:{t.Rotation.Z:F2} W:{t.Rotation.W:F2}");
            ImGui.TableSetColumnIndex(4);
            ImGui.Text($"{t.Scale:F3}");

            if (gui.WorldToScreen(worldMatrix.Translation, out var screenPos))
            {
                var dotColor = ImGui.GetColorU32(dotColorRgb);
                ImGui.GetBackgroundDrawList().AddCircleFilled(screenPos, 5, dotColor);
            }
        }
    }
}
