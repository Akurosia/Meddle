using System.Numerics;
using Dalamud.Bindings.ImGui;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Structs;

namespace Meddle.Plugin.UI.Debug;

public class EnvLightingTab : ITab
{
    public void Dispose()
    {
        // TODO release managed resources here
    }

    public string Name => "Env Lighting";
    public int Order => 2;
    public MenuType MenuType => MenuType.Debug;

    public unsafe void Draw()
    {
        var envMan = EnvManagerEx.Instance();
        if (envMan == null) throw new InvalidOperationException("EnvManagerEx is null");
        var envState = envMan->EnvState;
        var lighting = envState.Lighting;

        var sunCol = lighting.SunLightColor;
        ImGui.ColorButton("Sunlight Color", new Vector4(sunCol.Red, sunCol.Green, sunCol.Blue, 1.0f));

        var moonCol = lighting.MoonLightColor;
        ImGui.ColorButton("Moonlight Color", new Vector4(moonCol.Red, moonCol.Green, moonCol.Blue, 1.0f));

        var ambientCol = lighting.Ambient;
        ImGui.ColorButton("Ambient Color", new Vector4(ambientCol.Red, ambientCol.Green, ambientCol.Blue, 1.0f));
    }
}
