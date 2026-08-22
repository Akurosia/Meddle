using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Meddle.Plugin.Models;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.UI.Debug;

public class CharacterToolsTab : ITab
{
    private readonly ILogger<CharacterToolsTab> log;
    private readonly CharacterDebugTab characterDebugTab;
    private readonly SklbDebugTab sklbDebugTab;
    private readonly MaterialParameterTab materialParameterTab;
    private readonly ColorTableTester colorTableTester;
    private readonly OnRenderMaterialTester onRenderMaterialTester;
    private readonly Dictionary<string, string> lastErrors = new();

    public CharacterToolsTab(
        ILogger<CharacterToolsTab> log,
        CharacterDebugTab characterDebugTab,
        SklbDebugTab sklbDebugTab,
        MaterialParameterTab materialParameterTab,
        ColorTableTester colorTableTester,
        OnRenderMaterialTester onRenderMaterialTester)
    {
        this.log = log;
        this.characterDebugTab = characterDebugTab;
        this.sklbDebugTab = sklbDebugTab;
        this.materialParameterTab = materialParameterTab;
        this.colorTableTester = colorTableTester;
        this.onRenderMaterialTester = onRenderMaterialTester;
    }

    public void Dispose()
    {
        materialParameterTab.Dispose();
    }

    public string Name => "Character Tools";
    public int Order => 0;
    public MenuType MenuType => MenuType.Debug;

    public void Draw()
    {
        using var tabBar = ImRaii.TabBar("##CharacterToolsTabs", ImGuiTabBarFlags.Reorderable);
        DrawSubTab("Character Debug", characterDebugTab.Draw);
        DrawSubTab("Sklb Debug", sklbDebugTab.Draw);
        DrawSubTab("Material Parameters", materialParameterTab.Draw);
        DrawSubTab("ColorTable Tester", colorTableTester.Draw);
        DrawSubTab("OnRenderMaterial Tester", onRenderMaterialTester.Draw);
    }

    private void DrawSubTab(string name, Action draw)
    {
        using var tabItem = ImRaii.TabItem(name);
        if (!tabItem) return;

        try
        {
            draw();
        }
        catch (Exception e)
        {
            var errStr = e.ToString();
            if (!lastErrors.TryGetValue(name, out var lastError) || lastError != errStr)
            {
                lastErrors[name] = errStr;
                log.LogError(e, "Failed to draw {TabName} sub-tab", name);
            }

            ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Failed to draw {name} tab");
            ImGui.TextWrapped(e.ToString());
        }
    }
}
