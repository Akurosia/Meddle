using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Meddle.Plugin.Models;
using Meddle.Plugin.Services;

namespace Meddle.Plugin.UI.Debug;

public class PbdDebugTab : ITab
{
    private readonly PbdHooks pbdHooks;

    public PbdDebugTab(PbdHooks pbdHooks)
    {
        this.pbdHooks = pbdHooks;
    }

    public void Dispose()
    {
        // TODO release managed resources here
    }

    public string Name => "PBD Info";
    public int Order => 4;
    public MenuType MenuType => MenuType.Debug;

    public void Draw()
    {
        using var table = ImRaii.Table("##PbdInfo", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        ImGui.TableSetupColumn("Human", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("DeformerId", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("RaceSexId", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("PbdPath");
        ImGui.TableHeadersRow();
        foreach (var cachedDeformer in pbdHooks.GetDeformerCache())
        {
            foreach (var deformer in cachedDeformer.Value)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{cachedDeformer.Key:X8}");
                ImGui.TableNextColumn();
                ImGui.Text($"{deformer.Key}");
                ImGui.TableNextColumn();
                ImGui.Text($"{deformer.Value.DeformerId}");
                ImGui.TableNextColumn();
                ImGui.Text($"{deformer.Value.RaceSexId}");
                ImGui.TableNextColumn();
                ImGui.Text($"{deformer.Value.PbdPath}");
            }
        }
    }
}
