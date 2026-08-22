using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Meddle.Formats.Constants;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Composer;

namespace Meddle.Plugin.UI.Debug;

public class ConstantsTab : ITab
{
    private string constantSearch = "";

    public void Dispose()
    {
        // TODO release managed resources here
    }

    public string Name => "Constants";
    public int Order => 8;
    public MenuType MenuType => MenuType.Debug;

    public void Draw()
    {
        if (ImGui.CollapsingHeader("Constant Cache"))
        {
            if (ImGui.InputText("##ConstantSearch", ref constantSearch, 100))
            {
                constantSearch = constantSearch.ToLower();
            }

            var constants = Names.GetConstants().ToArray();
            if (!string.IsNullOrEmpty(constantSearch))
            {
                constants = constants.Where(x =>
                     {
                         if (x.Value.Value.Contains(constantSearch, StringComparison.CurrentCultureIgnoreCase))
                         {
                             return true;
                         }

                         if (x.Key.ToString().Contains(constantSearch, StringComparison.CurrentCultureIgnoreCase))
                         {
                             return true;
                         }

                         var hexKey = $"0x{x.Key:X8}";
                         if (hexKey.Contains(constantSearch, StringComparison.CurrentCultureIgnoreCase))
                         {
                             return true;
                         }

                         return false;
                     })
                    .ToArray();
            }

            using var table = ImRaii.Table("##ConstantCache", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);

            ImGui.TableSetupColumn("Key");
            ImGui.TableSetupColumn("Hex Key");
            ImGui.TableSetupColumn("Value");
            ImGui.TableHeadersRow();

            foreach (var (key, value) in constants)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(key.ToString());
                ImGui.TableNextColumn();
                var hexKey = $"0x{key:X8}";
                ImGui.Text(hexKey);
                ImGui.TableNextColumn();
                if (value is Names.StubName stubName)
                {
                    ImGui.Text($"{stubName.Value} (stubName)");
                }
                else if (value is Names.Name name)
                {
                    ImGui.Text(name.Value);
                }
                else if (value is Names.SuffixedName compositeName)
                {
                    ImGui.Text(compositeName.Value);
                }
            }
        }

        var buf = string.Join("\n", MaterialComposer.FailedConstants);
        ImGui.InputTextMultiline("Failed Constants", ref buf, 100000, new Vector2(0, 0), ImGuiInputTextFlags.ReadOnly);
    }
}
