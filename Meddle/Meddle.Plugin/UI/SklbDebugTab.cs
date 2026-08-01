using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Meddle.Formats.Files;
using Meddle.Formats.Helpers;
using Meddle.Plugin.Havok;
using Meddle.Plugin.Models;
using Meddle.Plugin.Utils;

namespace Meddle.Plugin.UI;

public class SklbDebugTab : ITab
{
    private readonly Configuration config;
    private readonly SqPack.SqPack sqPack;
    private readonly HavokConverter havokConverter;
    private readonly CommonUi commonUi;
    private readonly INotificationManager notificationManager;
    private readonly FileDialogManager fileDialog = new()
    {
        AddedWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
    };

    private ICharacter? selectedCharacter;
    private string sklbPathInput = "";
    private string? lastError;

    public SklbDebugTab(Configuration config, SqPack.SqPack sqPack, HavokConverter havokConverter,
                        CommonUi commonUi, INotificationManager notificationManager)
    {
        this.config = config;
        this.sqPack = sqPack;
        this.havokConverter = havokConverter;
        this.commonUi = commonUi;
        this.notificationManager = notificationManager;
    }

    public string Name => "Sklb Debug";
    public int Order => 6;
    public MenuType MenuType => MenuType.Debug;

    public unsafe void Draw()
    {
        fileDialog.Draw();
        using var indent = ImRaii.PushIndent();

        commonUi.DrawCharacterSelect(ref selectedCharacter);

        if (selectedCharacter != null)
        {
            var paths = GetSklbPathsForCharacter(selectedCharacter);
            if (paths.Count == 0)
            {
                ImGui.TextDisabled("No skeletons found on this character");
            }
            else
            {
                using var table = ImRaii.Table("##SklbPaths", 2,
                                               ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                               ImGuiTableFlags.SizingStretchProp);
                if (table)
                {
                    ImGui.TableSetupColumn("Skeleton", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableHeadersRow();
                    foreach (var (label, path) in paths)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped($"{label}: {path}");
                        ImGui.TableNextColumn();
                        if (ImGui.Button($"Export##{label}{path}"))
                        {
                            ExportSklb(path);
                        }
                    }
                }
            }
        }

        ImGui.Separator();
        ImGui.Text("Manual Path");
        ImGui.SameLine();
        ImGui.InputText("##SklbPath", ref sklbPathInput, 260);
        ImGui.SameLine();
        if (ImGui.Button("Export##Manual"))
        {
            ExportSklb(sklbPathInput);
        }
        ImGui.TextWrapped("Manual path accepts either a game path " +
                           "(e.g. chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb) " +
                           "or an absolute path to a .sklb file on disk.");

        if (lastError != null)
        {
            ImGui.TextColored(new Vector4(1, 0, 0, 1), lastError);
        }
    }

    private unsafe List<(string Label, string Path)> GetSklbPathsForCharacter(ICharacter character)
    {
        var results = new List<(string, string)>();
        var charPtr = (Character*)character.Address;

        CollectCharacterBaseSklbPaths(charPtr->DrawObject, "Character", results);

        var ornament = charPtr->OrnamentData.OrnamentObject;
        if (ornament != null)
            CollectCharacterBaseSklbPaths(ornament->DrawObject, "Ornament", results);

        var mount = charPtr->Mount.MountObject;
        if (mount != null)
            CollectCharacterBaseSklbPaths(mount->DrawObject, "Mount", results);

        var companion = charPtr->CompanionData.CompanionObject;
        if (companion != null)
            CollectCharacterBaseSklbPaths(companion->DrawObject, "Companion", results);

        var weapons = charPtr->DrawData.WeaponData;
        for (var i = 0; i < weapons.Length; i++)
        {
            if (weapons[i].DrawData.DrawObject != null)
                CollectCharacterBaseSklbPaths(weapons[i].DrawData.DrawObject, $"Weapon {i}", results);
        }

        return results;
    }

    private static unsafe void CollectCharacterBaseSklbPaths(DrawObject* drawObject, string label,
                                                              List<(string, string)> results)
    {
        if (drawObject == null || drawObject->GetObjectType() != ObjectType.CharacterBase) return;

        var cBase = (CharacterBase*)drawObject;
        var skeleton = cBase->Skeleton;
        if (skeleton == null) return;

        for (var i = 0; i < skeleton->PartialSkeletonCount; i++)
        {
            var handle = skeleton->PartialSkeletons[i].SkeletonResourceHandle;
            if (handle == null) continue;

            var path = handle->FileName.ParseString();
            if (string.IsNullOrEmpty(path)) continue;

            results.Add((skeleton->PartialSkeletonCount > 1 ? $"{label} [{i}]" : label, path));
        }
    }

    private void ExportSklb(string path)
    {
        lastError = null;
        try
        {
            var data = sqPack.GetFileOrReadFromDisk(path);
            if (data == null)
            {
                notificationManager.AddNotification(new Notification
                {
                    Content = $"File not found: {path}",
                    Type = NotificationType.Error
                });
                return;
            }

            var sklb = new SklbFile(data);
            var xml = havokConverter.HkxToXml(sklb.Skeleton.ToArray());

            var fileName = Path.GetFileNameWithoutExtension(path);
            var defaultName = $"Export-{fileName}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}";
            fileDialog.SaveFolderDialog("Save Sklb + Havok XML", defaultName,
                                        (result, exportPath) =>
                                        {
                                            if (!result) return;
                                            Directory.CreateDirectory(exportPath);
                                            File.WriteAllBytes(Path.Combine(exportPath, fileName + ".sklb"), data);
                                            File.WriteAllText(Path.Combine(exportPath, fileName + ".xml"), xml);
                                            ExportUtil.OpenExportFolderInExplorer(exportPath, config,
                                                CancellationToken.None);
                                        }, config.ExportDirectory);
        }
        catch (Exception e)
        {
            lastError = e.ToString();
            notificationManager.AddNotification(new Notification
            {
                Content = $"Failed to convert sklb to Havok XML: {e.Message}",
                Type = NotificationType.Error
            });
        }
    }

    public void Dispose()
    {
    }
}
