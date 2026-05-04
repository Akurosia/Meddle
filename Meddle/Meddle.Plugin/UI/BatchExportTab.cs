using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Composer;
using Meddle.Plugin.Models.Layout;
using Meddle.Plugin.Services;
using Meddle.Plugin.UI.Layout;
using Meddle.Plugin.Utils;
using Meddle.Utils.Files.SqPack;
using Microsoft.Extensions.Logging;
using Lumina.Excel.Sheets;
using SharpGLTF.Scenes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Meddle.Plugin.UI;

public unsafe class BatchExportTab : ITab
{
    private static readonly Regex EquipmentIdRegex = new(@"e\d{4}", RegexOptions.Compiled);

    private readonly ILogger<BatchExportTab> logger;
    private readonly CommonUi commonUi;
    private readonly Configuration config;
    private readonly ResolverService resolverService;
    private readonly ComposerFactory composerFactory;
    private readonly SqPack pack;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;

    private ICharacter? selectedCharacter;
    private Task exportTask = Task.CompletedTask;
    private CancellationTokenSource cancelToken = new();
    private ProgressWrapper? progress;
    private int bodyItemTestCount = 5;
    private string? statusMessage;

    public BatchExportTab(
        ILogger<BatchExportTab> logger,
        CommonUi commonUi,
        Configuration config,
        ResolverService resolverService,
        ComposerFactory composerFactory,
        SqPack pack,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager)
    {
        this.logger = logger;
        this.commonUi = commonUi;
        this.config = config;
        this.resolverService = resolverService;
        this.composerFactory = composerFactory;
        this.pack = pack;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.dataManager = dataManager;
    }

    public string Name => "Batch";
    public int Order => (int)WindowOrder.Batch;
    public MenuType MenuType => MenuType.Default;

    public void Draw()
    {
        UiUtil.DrawProgress(exportTask, progress, cancelToken);
        commonUi.DrawCharacterSelect(ref selectedCharacter, CharacterValidationFlags.IsVisible);

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            ImGui.TextWrapped(statusMessage);
            ImGui.Separator();
        }

        DrawBodyItemSection();
        ImGui.Separator();
        DrawZoneEnemySection();
    }

    public void Dispose()
    {
        cancelToken.Cancel();
        cancelToken.Dispose();
    }

    private void DrawBodyItemSection()
    {
        ImGui.Text("Equipable Item GLTF Export");
        ImGui.TextWrapped("Uses the selected visible character as the template and exports one GLTF per equipable item: main hand, off hand, shield, head, body, hands, legs, shoes, earrings, necklace, wrists, and rings.");

        var bodyExportDirectory = config.BatchBodyItemExportDirectory;
        if (ImGui.InputText("Body Save Path", ref bodyExportDirectory, 512))
        {
            config.BatchBodyItemExportDirectory = bodyExportDirectory;
            config.Save();
        }

        ImGui.InputInt("Test Count", ref bodyItemTestCount);

        bodyItemTestCount = Math.Clamp(bodyItemTestCount, 1, 100);

        DrawEquipmentCategoryToggles();

        using var disabled = ImRaii.Disabled(!exportTask.IsCompleted);
        if (ImGui.Button("Export Test Items"))
        {
            StartBodyItemExport(testMode: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Export All Equipable Items"))
        {
            StartBodyItemExport(testMode: false);
        }
    }

    private void DrawZoneEnemySection()
    {
        var territoryId = clientState.TerritoryType;
        var zoneLookup = config.ZoneEnemyExportLookup.GetValueOrDefault(territoryId, []);

        ImGui.Text("Zone Enemy GLTF Generation");
        ImGui.TextWrapped("Scans visible battle NPCs in the current zone, exports only missing model sets, and stores a lookup so repeated runs skip duplicates.");
        var zoneExportDirectory = config.BatchZoneEnemyExportDirectory;
        if (ImGui.InputText("Zone Save Path", ref zoneExportDirectory, 512))
        {
            config.BatchZoneEnemyExportDirectory = zoneExportDirectory;
            config.Save();
        }
        ImGui.Text($"Current Territory Id: {territoryId}");
        ImGui.Text($"Stored Enemy Entries: {zoneLookup.Count}");

        using var disabled = ImRaii.Disabled(!exportTask.IsCompleted || territoryId == 0);
        if (ImGui.Button("Generate Missing Zone Enemies"))
        {
            StartZoneEnemyExport();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Current Zone Lookup"))
        {
            config.ZoneEnemyExportLookup.Remove(territoryId);
            config.Save();
            statusMessage = territoryId == 0
                ? "Current territory lookup was cleared."
                : $"Cleared stored enemy lookup for territory {territoryId}.";
        }
    }

    private void StartBodyItemExport(bool testMode)
    {
        if (!exportTask.IsCompleted)
        {
            statusMessage = "Another export is already running.";
            return;
        }

        if (selectedCharacter == null)
        {
            statusMessage = "Select a visible human character first.";
            return;
        }

        var character = (Character*)selectedCharacter.Address;
        var parsedCharacter = resolverService.ParseCharacter(character);
        if (parsedCharacter == null)
        {
            statusMessage = "Failed to parse the selected character.";
            return;
        }

        if (!parsedCharacter.Models.Any())
        {
            statusMessage = "The selected character does not expose any equipment model templates.";
            return;
        }

        CharacterBatchCandidate[] candidates;
        var selectedSlots = GetSelectedEquipmentSlots().ToHashSet();
        if (selectedSlots.Count == 0)
        {
            statusMessage = "Enable at least one equipment category first.";
            return;
        }

        try
        {
            candidates = FindBodyItemCandidates(parsedCharacter, selectedSlots, testMode).ToArray();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build body item export candidates");
            statusMessage = $"Failed to prepare body item export: {ex.Message}";
            return;
        }
        if (candidates.Length == 0)
        {
            statusMessage = "No matching equipable item models were found.";
            return;
        }

        cancelToken = new CancellationTokenSource();
        progress = new ProgressWrapper
        {
            Progress = new ExportProgress(candidates.Length, "Equipable Items")
        };

        var outputDir = GetBodyItemOutputDirectory();

        exportTask = Task.Run(() =>
        {
            Directory.CreateDirectory(outputDir);
            var exportConfig = CreateBatchExportConfig();
            ExportCharacterBatch(candidates, outputDir, exportConfig, progress.Progress!, true, (candidate, exportName) =>
            {
                logger.LogInformation("Exported item {ItemId} to {ExportName}", candidate.ItemId, exportName);
            });
            ExportUtil.OpenExportFolderInExplorer(outputDir, config, cancelToken.Token);
        }, cancelToken.Token);

        statusMessage = testMode
            ? $"Queued export for up to {bodyItemTestCount} equipable items across {selectedSlots.Count} category(s)."
            : "Queued export for all equipable items.";
    }

    private void StartZoneEnemyExport()
    {
        if (!exportTask.IsCompleted)
        {
            statusMessage = "Another export is already running.";
            return;
        }

        var territoryId = clientState.TerritoryType;
        if (territoryId == 0)
        {
            statusMessage = "No active territory was detected.";
            return;
        }

        var candidates = CollectZoneEnemyCandidates(territoryId).ToArray();
        if (candidates.Length == 0)
        {
            statusMessage = $"No new visible battle NPC model sets were found for territory {territoryId}.";
            return;
        }

        cancelToken = new CancellationTokenSource();
        progress = new ProgressWrapper
        {
            Progress = new ExportProgress(candidates.Length, "Zone Enemies")
        };

        var outputDir = GetZoneEnemyOutputDirectory(territoryId);
        exportTask = Task.Run(() =>
        {
            Directory.CreateDirectory(outputDir);
            var exportConfig = CreateBatchExportConfig();
            ExportCharacterBatch(candidates, outputDir, exportConfig, progress.Progress!, false, (candidate, exportName) =>
            {
                var zoneLookup = config.ZoneEnemyExportLookup.GetValueOrDefault(territoryId);
                if (zoneLookup == null)
                {
                    zoneLookup = new Dictionary<string, Configuration.ZoneEnemyExportRecord>();
                    config.ZoneEnemyExportLookup[territoryId] = zoneLookup;
                }

                zoneLookup[candidate.LookupKey] = new Configuration.ZoneEnemyExportRecord
                {
                    Name = candidate.DisplayName,
                    ModelPaths = GetAllModelPaths(candidate.CharacterInfo).Distinct().OrderBy(x => x).ToArray(),
                    ExportedAtUtc = DateTime.UtcNow
                };
            });
            config.Save();
            ExportUtil.OpenExportFolderInExplorer(outputDir, config, cancelToken.Token);
        }, cancelToken.Token);

        statusMessage = $"Queued export for {candidates.Length} new enemy model set(s) in territory {territoryId}.";
    }

    private IEnumerable<CharacterBatchCandidate> FindBodyItemCandidates(
        ParsedCharacterInfo templateCharacter,
        IReadOnlySet<EquipmentSlot> selectedSlots,
        bool testMode)
    {
        var templates = BuildEquipmentTemplates(templateCharacter)
            .Where(template => selectedSlots.Contains(template.Slot))
            .ToDictionary(template => template.Slot, template => template);
        var found = 0;

        foreach (var item in dataManager.GetExcelSheet<Item>())
        {
            if (cancelToken.IsCancellationRequested)
            {
                yield break;
            }

            var itemName = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(itemName))
            {
                continue;
            }

            foreach (var slot in selectedSlots)
            {
                if (!templates.TryGetValue(slot, out var template))
                {
                    continue;
                }

                if (!IsTemplateApplicableToItem(slot, item))
                {
                    continue;
                }

                if (!TryBuildItemExportModel(item, template, out var exportModel))
                {
                    continue;
                }

                var exportInfo = new ParsedCharacterInfo(
                    [exportModel],
                    templateCharacter.Skeleton,
                    templateCharacter.Attach,
                    templateCharacter.HumanInfo)
                {
                    Attaches = []
                };

                var exportName = $"{itemName.Replace(' ', '-').SanitizeFileName()}_{item.RowId}";
                yield return new CharacterBatchCandidate(
                    exportName,
                    itemName,
                    $"item:{item.RowId}:{slot}",
                    exportInfo,
                    (int)item.RowId);

                found++;
                if (testMode && found >= bodyItemTestCount)
                {
                    yield break;
                }
            }
        }
    }

    private IEnumerable<CharacterBatchCandidate> CollectZoneEnemyCandidates(uint territoryId)
    {
        var existingLookup = config.ZoneEnemyExportLookup.GetValueOrDefault(territoryId, []);
        var seenThisRun = new HashSet<string>(StringComparer.Ordinal);

        foreach (var character in objectTable.OfType<ICharacter>())
        {
            if (character.ObjectKind != ObjectKind.BattleNpc)
            {
                continue;
            }

            if (!character.IsValidCharacterBase(CharacterValidationFlags.IsVisible))
            {
                continue;
            }

            ParsedCharacterInfo? parsedCharacter;
            unsafe
            {
                parsedCharacter = resolverService.ParseCharacter((Character*)character.Address);
            }
            if (parsedCharacter == null)
            {
                continue;
            }

            var modelPaths = GetAllModelPaths(parsedCharacter).Distinct().OrderBy(x => x).ToArray();
            if (modelPaths.Length == 0)
            {
                continue;
            }

            var lookupKey = string.Join("|", modelPaths);
            if (!seenThisRun.Add(lookupKey))
            {
                continue;
            }

            if (existingLookup.ContainsKey(lookupKey))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(character.Name.TextValue)
                ? $"battle_npc_{character.BaseId}"
                : character.Name.TextValue;
            var exportName = $"{displayName.SanitizeFileName()}_{character.BaseId:X}_{seenThisRun.Count:D3}";

            yield return new CharacterBatchCandidate(
                exportName,
                displayName,
                lookupKey,
                parsedCharacter);
        }
    }

    private void ExportCharacterBatch(
        IEnumerable<CharacterBatchCandidate> candidates,
        string outputDir,
        Configuration.ExportConfiguration exportConfig,
        ExportProgress rootProgress,
        bool inlineBuffer,
        Action<CharacterBatchCandidate, string> onExported)
    {
        var composer = composerFactory.CreateCharacterComposer(outputDir, exportConfig, cancelToken.Token);

        foreach (var candidate in candidates)
        {
            cancelToken.Token.ThrowIfCancellationRequested();

            var scene = new SceneBuilder();
            var root = new NodeBuilder(candidate.ExportName);
            scene.AddNode(root);

            composer.Compose(candidate.CharacterInfo, scene, root, new ExportProgress(candidate.CharacterInfo.Models.Count, candidate.DisplayName));
            ExportUtil.SaveAsType(scene.ToGltf2(), exportConfig.ExportType, outputDir, candidate.ExportName);
            if (inlineBuffer)
            {
                InlineGltfBuffer(Path.Combine(outputDir, candidate.ExportName.SanitizeFileName() + ".gltf"));
            }
            onExported(candidate, candidate.ExportName);
            rootProgress.IncrementProgress();
        }

        rootProgress.IsComplete = true;
    }

    private Configuration.ExportConfiguration CreateBatchExportConfig()
    {
        var exportConfig = config.ExportConfig.Clone();
        exportConfig.SetDefaultCloneOptions();
        exportConfig.ExportType = ExportType.GLTF;
        exportConfig.PoseMode = SkeletonUtils.PoseMode.Local;
        exportConfig.UseDeformer = true;
        return exportConfig;
    }

    private string GetBodyItemOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(config.BatchBodyItemExportDirectory))
        {
            return config.BatchBodyItemExportDirectory;
        }

        return Path.Combine(
            config.ExportDirectory,
            "BatchExports",
            $"BodyItems-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}");
    }

    private string GetZoneEnemyOutputDirectory(uint territoryId)
    {
        if (!string.IsNullOrWhiteSpace(config.BatchZoneEnemyExportDirectory))
        {
            return config.BatchZoneEnemyExportDirectory;
        }

        return Path.Combine(config.ExportDirectory, "BatchExports", "ZoneEnemies", $"territory_{territoryId:D4}");
    }

    private static IEnumerable<string> GetAllModelPaths(ParsedCharacterInfo characterInfo)
    {
        foreach (var model in characterInfo.Models)
        {
            if (!string.IsNullOrWhiteSpace(model.Path.FullPath))
            {
                yield return model.Path.FullPath;
            }
        }

        foreach (var attach in characterInfo.Attaches)
        {
            foreach (var attachPath in GetAllModelPaths(attach))
            {
                yield return attachPath;
            }
        }
    }

    private static void InlineGltfBuffer(string gltfPath)
    {
        if (!File.Exists(gltfPath))
        {
            return;
        }

        var json = JsonNode.Parse(File.ReadAllText(gltfPath))?.AsObject();
        if (json == null || json["buffers"] is not JsonArray buffers || buffers.Count == 0 || buffers[0] is not JsonObject bufferObject)
        {
            return;
        }

        var uri = bufferObject["uri"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var binPath = Path.Combine(Path.GetDirectoryName(gltfPath)!, uri);
        if (!File.Exists(binPath))
        {
            return;
        }

        bufferObject["uri"] = $"data:application/octet-stream;base64,{Convert.ToBase64String(File.ReadAllBytes(binPath))}";
        File.WriteAllText(gltfPath, json.ToJsonString());
        File.Delete(binPath);
    }

    private IEnumerable<EquipmentTemplate> BuildEquipmentTemplates(ParsedCharacterInfo templateCharacter)
    {
        var yieldedSlots = new HashSet<EquipmentSlot>();
        foreach (var model in templateCharacter.Models)
        {
            var slot = InferEquipmentSlot(model.Path.FullPath);
            if (slot != null && yieldedSlots.Add(slot.Value))
            {
                yield return new EquipmentTemplate(slot.Value, model);
            }
        }

        var weaponAttaches = templateCharacter.Attaches
            .Where(attach => attach.Models.Any(model => model.Path.FullPath.Contains("chara/weapon/", StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();

        if (weaponAttaches.Length > 0)
        {
            var mainWeaponModel = weaponAttaches[0].Models.First(model => model.Path.FullPath.Contains("chara/weapon/", StringComparison.OrdinalIgnoreCase));
            if (yieldedSlots.Add(EquipmentSlot.MainHand))
            {
                yield return new EquipmentTemplate(EquipmentSlot.MainHand, mainWeaponModel);
            }
        }

        if (weaponAttaches.Length > 1)
        {
            var offhandWeaponModel = weaponAttaches[1].Models.First(model => model.Path.FullPath.Contains("chara/weapon/", StringComparison.OrdinalIgnoreCase));
            if (yieldedSlots.Add(EquipmentSlot.OffHand))
            {
                yield return new EquipmentTemplate(EquipmentSlot.OffHand, offhandWeaponModel);
            }
        }
    }

    private bool TryBuildItemExportModel(Item item, EquipmentTemplate template, out ParsedModelInfo exportModel)
    {
        exportModel = null!;
        if (!TryResolveItemModelInfo(item, template.Slot, out var itemModel))
        {
            return false;
        }

        var candidatePath = ApplyModelToTemplatePath(template.Model.Path.FullPath, template.Slot, itemModel);
        if (!pack.FileExists(candidatePath, out _))
        {
            return false;
        }

        var exportMaterials = ApplyModelToTemplateMaterials(template.Model.Materials, template.Slot, itemModel);
        exportModel = new ParsedModelInfo(
            candidatePath,
            ApplyModelToTemplatePath(template.Model.Path.GamePath, template.Slot, itemModel),
            true,
            template.Model.Deformer,
            template.Model.ShapeAttributeGroup,
            exportMaterials,
            null,
            null);

        return true;
    }

    private static ParsedMaterialInfo?[] ApplyModelToTemplateMaterials(
        ParsedMaterialInfo?[] templateMaterials,
        EquipmentSlot slot,
        ItemModelInfo itemModel)
    {
        return templateMaterials.Select(material =>
        {
            if (material == null)
            {
                return null;
            }

            return new ParsedMaterialInfo(
                ApplyModelToTemplatePath(material.Path.FullPath, slot, itemModel, true),
                ApplyModelToTemplatePath(material.Path.GamePath, slot, itemModel, true),
                material.Shpk,
                material.RenderMaterialOutput,
                material.ColorTable,
                material.Textures)
            {
                Stain0 = material.Stain0,
                Stain1 = material.Stain1
            };
        }).ToArray();
    }

    private static string ApplyModelToTemplatePath(string templatePath, EquipmentSlot slot, ItemModelInfo itemModel, bool materialPath = false)
    {
        var path = templatePath;

        if (slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand)
        {
            path = ReplaceFirst(path, @"w\d{4}", $"w{itemModel.PrimaryId:D4}");
            path = ReplaceFirst(path, @"b\d{4}", $"b{itemModel.SecondaryId:D4}");
        }
        else
        {
            path = ReplaceFirst(path, @"[ea]\d{4}", match => $"{match.Value[0]}{itemModel.PrimaryId:D4}");
        }

        if (materialPath)
        {
            path = ReplaceFirst(path, @"v\d{4}", $"v{Math.Max(1, (int)itemModel.Variant):D4}");
        }

        return path;
    }

    private static string ReplaceFirst(string input, string pattern, string replacement)
    {
        return new Regex(pattern, RegexOptions.IgnoreCase).Replace(input, replacement, 1);
    }

    private static string ReplaceFirst(string input, string pattern, MatchEvaluator evaluator)
    {
        return new Regex(pattern, RegexOptions.IgnoreCase).Replace(input, evaluator, 1);
    }

    private static EquipmentSlot? InferEquipmentSlot(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("_met.mdl")) return EquipmentSlot.Head;
        if (normalized.Contains("_top.mdl")) return EquipmentSlot.Body;
        if (normalized.Contains("_glv.mdl")) return EquipmentSlot.Hands;
        if (normalized.Contains("_dwn.mdl")) return EquipmentSlot.Legs;
        if (normalized.Contains("_sho.mdl")) return EquipmentSlot.Feet;
        if (normalized.Contains("_ear.mdl")) return EquipmentSlot.Earrings;
        if (normalized.Contains("_nek.mdl")) return EquipmentSlot.Necklace;
        if (normalized.Contains("_wrs.mdl")) return EquipmentSlot.Wrists;
        if (normalized.Contains("_rir.mdl") || normalized.Contains("_ril.mdl")) return EquipmentSlot.Ring;
        return null;
    }

    private static bool IsTemplateApplicableToItem(EquipmentSlot slot, Item item)
    {
        var equipSlotCategory = GetRefRowValue(item, "EquipSlotCategory");
        if (equipSlotCategory == null)
        {
            return false;
        }

        return slot switch
        {
            EquipmentSlot.MainHand => GetTruthyProperty(equipSlotCategory, "MainHand"),
            EquipmentSlot.OffHand => GetTruthyProperty(equipSlotCategory, "OffHand"),
            EquipmentSlot.Head => GetTruthyProperty(equipSlotCategory, "Head"),
            EquipmentSlot.Body => GetTruthyProperty(equipSlotCategory, "Body"),
            EquipmentSlot.Hands => GetTruthyProperty(equipSlotCategory, "Gloves", "Hands"),
            EquipmentSlot.Legs => GetTruthyProperty(equipSlotCategory, "Legs"),
            EquipmentSlot.Feet => GetTruthyProperty(equipSlotCategory, "Feet"),
            EquipmentSlot.Earrings => GetTruthyProperty(equipSlotCategory, "Ears"),
            EquipmentSlot.Necklace => GetTruthyProperty(equipSlotCategory, "Neck"),
            EquipmentSlot.Wrists => GetTruthyProperty(equipSlotCategory, "Wrists"),
            EquipmentSlot.Ring => GetTruthyProperty(equipSlotCategory, "FingerR", "FingerL", "Ring"),
            _ => false
        };
    }

    private static bool TryResolveItemModelInfo(Item item, EquipmentSlot slot, out ItemModelInfo itemModel)
    {
        itemModel = default;
        var rawModelValue = GetPropertyValue(item, slot == EquipmentSlot.OffHand ? "ModelSub" : "ModelMain");
        if (rawModelValue == null)
        {
            return false;
        }

        if (slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand)
        {
            if (!TryConvertToUInt64(rawModelValue, out var packedValue) || packedValue == 0)
            {
                return false;
            }

            var weaponModel = MemoryMarshal.Cast<ulong, WeaponModelId>(MemoryMarshal.CreateSpan(ref packedValue, 1))[0];
            if (weaponModel.Id == 0 || weaponModel.Type == 0)
            {
                return false;
            }

            itemModel = new ItemModelInfo(weaponModel.Id, weaponModel.Type, Math.Max((ushort)1, weaponModel.Variant));
            return true;
        }

        if (!TryConvertToUInt64(rawModelValue, out var packedEquipmentValue) || packedEquipmentValue == 0)
        {
            return false;
        }

        var equipmentModel = MemoryMarshal.Cast<ulong, EquipmentModelId>(MemoryMarshal.CreateSpan(ref packedEquipmentValue, 1))[0];
        if (equipmentModel.Id == 0)
        {
            return false;
        }

        itemModel = new ItemModelInfo(equipmentModel.Id, 0, Math.Max((ushort)1, equipmentModel.Variant));
        return true;
    }

    private static object? GetRefRowValue(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        if (value == null)
        {
            return null;
        }

        return value.GetType().GetProperty("Value")?.GetValue(value) ?? value;
    }

    private static object? GetPropertyValue(object source, string propertyName)
    {
        return source.GetType().GetProperty(propertyName)?.GetValue(source);
    }

    private static bool GetTruthyProperty(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
            switch (value)
            {
                case bool boolValue when boolValue:
                    return true;
                case byte byteValue when byteValue != 0:
                    return true;
                case sbyte sbyteValue when sbyteValue != 0:
                    return true;
                case short shortValue when shortValue != 0:
                    return true;
                case ushort ushortValue when ushortValue != 0:
                    return true;
                case int intValue when intValue != 0:
                    return true;
                case uint uintValue when uintValue != 0:
                    return true;
            }
        }

        return false;
    }

    private static bool TryConvertToUInt64(object value, out ulong result)
    {
        switch (value)
        {
            case ulong ulongValue:
                result = ulongValue;
                return true;
            case long longValue:
                result = unchecked((ulong)longValue);
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case int intValue:
                result = unchecked((ulong)intValue);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private void DrawEquipmentCategoryToggles()
    {
        ImGui.Text("Categories");

        DrawCategoryToggle("Main Hand", nameof(config.BatchExportMainHand));
        ImGui.SameLine();
        DrawCategoryToggle("Off Hand / Shield", nameof(config.BatchExportOffHand));

        DrawCategoryToggle("Head", nameof(config.BatchExportHead));
        ImGui.SameLine();
        DrawCategoryToggle("Body", nameof(config.BatchExportBody));
        ImGui.SameLine();
        DrawCategoryToggle("Hands", nameof(config.BatchExportHands));

        DrawCategoryToggle("Legs", nameof(config.BatchExportLegs));
        ImGui.SameLine();
        DrawCategoryToggle("Shoes", nameof(config.BatchExportFeet));

        DrawCategoryToggle("Earrings", nameof(config.BatchExportEarrings));
        ImGui.SameLine();
        DrawCategoryToggle("Necklace", nameof(config.BatchExportNecklace));
        ImGui.SameLine();
        DrawCategoryToggle("Wrists", nameof(config.BatchExportWrists));
        ImGui.SameLine();
        DrawCategoryToggle("Rings", nameof(config.BatchExportRings));
    }

    private void DrawCategoryToggle(string label, string propertyName)
    {
        var property = typeof(Configuration).GetProperty(propertyName);
        if (property?.GetValue(config) is not bool value)
        {
            return;
        }

        if (ImGui.Checkbox(label, ref value))
        {
            property.SetValue(config, value);
            config.Save();
        }
    }

    private IEnumerable<EquipmentSlot> GetSelectedEquipmentSlots()
    {
        if (config.BatchExportMainHand) yield return EquipmentSlot.MainHand;
        if (config.BatchExportOffHand) yield return EquipmentSlot.OffHand;
        if (config.BatchExportHead) yield return EquipmentSlot.Head;
        if (config.BatchExportBody) yield return EquipmentSlot.Body;
        if (config.BatchExportHands) yield return EquipmentSlot.Hands;
        if (config.BatchExportLegs) yield return EquipmentSlot.Legs;
        if (config.BatchExportFeet) yield return EquipmentSlot.Feet;
        if (config.BatchExportEarrings) yield return EquipmentSlot.Earrings;
        if (config.BatchExportNecklace) yield return EquipmentSlot.Necklace;
        if (config.BatchExportWrists) yield return EquipmentSlot.Wrists;
        if (config.BatchExportRings) yield return EquipmentSlot.Ring;
    }

    private sealed record CharacterBatchCandidate(
        string ExportName,
        string DisplayName,
        string LookupKey,
        ParsedCharacterInfo CharacterInfo,
        int? ItemId = null);

    private sealed record EquipmentTemplate(EquipmentSlot Slot, ParsedModelInfo Model);
    private readonly record struct ItemModelInfo(ushort PrimaryId, ushort SecondaryId, ushort Variant);

    private enum EquipmentSlot
    {
        MainHand,
        OffHand,
        Head,
        Body,
        Hands,
        Legs,
        Feet,
        Earrings,
        Necklace,
        Wrists,
        Ring
    }
}
