using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Meddle.Plugin.Models;
using Meddle.Plugin.Services;

namespace Meddle.Plugin.UI.Debug;

public class StainInfoTab : ITab
{
    public void Dispose()
    {
        // TODO release managed resources here
    }

    public string Name => "Stain Info";
    public int Order => 9;
    public MenuType MenuType => MenuType.Debug;

    public void Draw()
    {
        foreach (var (key, stain) in StainProvider.StainDict)
        {
            using var id = ImRaii.PushId(key.ToString());
            ImGui.Text($"Stain: {key}, {stain.Name}");
            var color = StainProvider.GetStainColor(stain);
            ImGui.SameLine();
            ImGui.ColorButton("Color", color);
        }
    }
}
