using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Interop;
using Meddle.Plugin.Services;
using Meddle.Plugin.Utils;

namespace Meddle.Plugin.UI.Debug;

public class ColorTableTester : IService
{
    private readonly CommonUi commonUi;
    private ICharacter? selectedCharacter;

    public ColorTableTester(CommonUi commonUi)
    {
        this.commonUi = commonUi;
    }

    public unsafe void Draw()
    {
        commonUi.DrawCharacterSelect(ref selectedCharacter, CharacterValidationFlags.IsVisible);

        if (selectedCharacter == null)
        {
            ImGui.Text("No character selected");
            return;
        }
        
        var character = (Character*)selectedCharacter.Address;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null)
        {
            ImGui.Text("Draw object is null");
            return;
        }
            
        if (drawObject->GetObjectType() != ObjectType.CharacterBase)
        {
            ImGui.Text("Draw object is not a character base");
            return;
        }
            
        var cBase = (CharacterBase*)drawObject;
        var modelType = cBase->GetModelType();
        if (modelType != CharacterBase.ModelType.Human)
        {
            ImGui.Text("Model is not human");
            return;
        }
        
        
        // cPtr.Value->ColorTableTexturesSpan[(slotIdx * CSCharacterBase.MaterialsPerSlot) + materialIdx];
        var human = (Human*)cBase;
        // var colorTableContexts = new List<(Pointer<Model> Model, Pointer<Material> Material, Pointer<Texture> Texture, int ColorTableIdx)>();
        var colorTableDict = new Dictionary<int, (Pointer<Model> Model, Pointer<Material> Material)>();
        for (var modelIdx = 0; modelIdx < human->ModelsSpan.Length; modelIdx++)
        {
            var model = human->ModelsSpan[modelIdx];
            if (model == null || model.Value == null)
            {
                continue;
            }

            for (var materialIdx = 0; materialIdx < model.Value->MaterialsSpan.Length; materialIdx++)
            {
                var material = model.Value->MaterialsSpan[materialIdx];
                if (material == null || material.Value == null)
                {
                    continue;
                }

                var index = ParseMaterialUtil.ResolveColorTableSetIndex((int)model.Value->SlotIndex, materialIdx);
                colorTableDict[index] = (model, material);
            }
        }
        for (var colorTableIdx = 0; colorTableIdx < human->ColorTableTexturesSpan.Length; colorTableIdx++)
        {
            var colorTableTexture = human->ColorTableTexturesSpan[colorTableIdx];
            if (colorTableTexture == null || colorTableTexture.Value == null)
            {
                continue;
            }

            if (!colorTableDict.TryGetValue(colorTableIdx, out var modelMaterial))
            {
                continue;
            }

            var modelName = modelMaterial.Model.Value->ModelResourceHandle->FileName;
            var materialName = modelMaterial.Material.Value->MaterialResourceHandle->FileName;
            var shortMaterialName = Path.GetFileName(materialName.ToString());
            
            if (ImGui.CollapsingHeader($"Color Table Texture {colorTableIdx} - {shortMaterialName} - {colorTableTexture.Value->ActualWidth}x{colorTableTexture.Value->ActualHeight}"))
            {
                var colorTable = ParseMaterialUtil.ParseColorTableTexture(colorTableTexture);
                UiUtil.DrawColorTable(colorTable);
            }
        }
    }
}
