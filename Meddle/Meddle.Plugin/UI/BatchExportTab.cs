using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
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
    private readonly IFramework framework;
    private readonly List<CancellationTokenSource> ownedCancellationSources = [];

    private ICharacter? selectedCharacter;
    private Task exportTask = Task.CompletedTask;
    private CancellationTokenSource cancelToken;
    private ProgressWrapper? progress;
    private int bodyItemTestCount = 5;
    private string? statusMessage;
    private DateTime lastAutoEnemyScanUtc = DateTime.MinValue;
    private uint lastAutoEnemyTerritoryId;
    private int lastUnsavedEnemyCount;
    private string? lastUnsavedEnemyPreview;
    private DateTime? lastUnsavedEnemyDetectedUtc;
    private bool isDisposed;
    private volatile bool automaticZoneEnemyExportPending;
    private uint pendingAutomaticZoneEnemyTerritoryId;

    public BatchExportTab(
        ILogger<BatchExportTab> logger,
        CommonUi commonUi,
        Configuration config,
        ResolverService resolverService,
        ComposerFactory composerFactory,
        SqPack pack,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager,
        IFramework framework)
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
        this.framework = framework;
        cancelToken = CreateOwnedCancellationTokenSource();
        this.framework.Update += OnFrameworkUpdate;
    }

    public string Name => "Batch";
    public int Order => (int)WindowOrder.Batch;
    public MenuType MenuType => MenuType.Default;

    public void Draw()
    {
        UiUtil.DrawProgress(exportTask, progress, cancelToken);
        ProcessPendingAutomaticZoneEnemyExport();
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
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        framework.Update -= OnFrameworkUpdate;
        foreach (var cts in ownedCancellationSources)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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
        var automaticEnemyExport = config.EnableAutomaticZoneEnemyExport;
        if (ImGui.Checkbox("Enable Automatic Background Export", ref automaticEnemyExport))
        {
            config.EnableAutomaticZoneEnemyExport = automaticEnemyExport;
            config.Save();
            lastAutoEnemyScanUtc = DateTime.MinValue;
            lastAutoEnemyTerritoryId = 0;
        }
        var automaticIntervalSeconds = config.AutomaticZoneEnemyExportIntervalSeconds;
        if (ImGui.InputInt("Auto Interval (seconds)", ref automaticIntervalSeconds))
        {
            config.AutomaticZoneEnemyExportIntervalSeconds = Math.Clamp(automaticIntervalSeconds, 1, 3600);
            config.Save();
        }
        config.AutomaticZoneEnemyExportIntervalSeconds = Math.Clamp(config.AutomaticZoneEnemyExportIntervalSeconds, 1, 3600);
        ImGui.Text($"Current Territory Id: {territoryId}");
        ImGui.Text($"Stored Enemy Entries: {zoneLookup.Count}");
        ImGui.Text($"Unsaved Visible Entries: {lastUnsavedEnemyCount}");
        if (!string.IsNullOrWhiteSpace(lastUnsavedEnemyPreview))
        {
            ImGui.TextWrapped($"Last Found: {lastUnsavedEnemyPreview}");
        }
        if (lastUnsavedEnemyDetectedUtc.HasValue)
        {
            ImGui.Text($"Last Detection (UTC): {lastUnsavedEnemyDetectedUtc.Value:yyyy-MM-dd HH:mm:ss}");
        }

        using var disabled = ImRaii.Disabled(!exportTask.IsCompleted || territoryId == 0);
        if (ImGui.Button("Generate Missing Zone Enemies Now"))
        {
            StartZoneEnemyExport(isAutomatic: false);
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

        var selectedSlots = GetSelectedEquipmentSlots().ToHashSet();
        if (selectedSlots.Count == 0)
        {
            statusMessage = "Enable at least one equipment category first.";
            return;
        }

        cancelToken = CreateOwnedCancellationTokenSource();
        progress = new ProgressWrapper
        {
            Progress = new ExportProgress(testMode ? bodyItemTestCount : EstimateBodyItemExportTotal(selectedSlots.Count), "Equipable Items")
        };

        var outputDir = GetBodyItemOutputDirectory();

        exportTask = Task.Run(() =>
        {
            Directory.CreateDirectory(outputDir);
            var exportConfig = CreateBatchExportConfig();
            exportConfig.PoseMode = SkeletonUtils.PoseMode.None;
            IEnumerable<CharacterBatchCandidate> candidates;
            try
            {
                candidates = FindBodyItemCandidates((nint)character, parsedCharacter, selectedSlots, testMode);
                ExportCharacterBatch(candidates, outputDir, exportConfig, progress.Progress!, true, (candidate, exportName) =>
                {
                    logger.LogInformation("Exported item {ItemId} to {ExportName}", candidate.ItemId, exportName);
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed during body item batch export");
                throw;
            }
        }, cancelToken.Token);

        statusMessage = testMode
            ? $"Queued export for up to {bodyItemTestCount} equipable items across {selectedSlots.Count} category(s)."
            : "Queued export for all equipable items.";
    }

    private void StartZoneEnemyExport(bool isAutomatic)
    {
        if (!exportTask.IsCompleted)
        {
            if (!isAutomatic)
            {
                statusMessage = "Another export is already running.";
            }
            return;
        }

        var territoryId = clientState.TerritoryType;
        if (territoryId == 0)
        {
            if (!isAutomatic)
            {
                statusMessage = "No active territory was detected.";
            }
            return;
        }

        var candidates = CollectZoneEnemyCandidates(territoryId).ToArray();
        UpdateUnsavedEnemyStatus(candidates);
        if (candidates.Length == 0)
        {
            if (!isAutomatic)
            {
                statusMessage = $"No new visible battle NPC model sets were found for territory {territoryId}.";
            }
            return;
        }

        cancelToken = CreateOwnedCancellationTokenSource();
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
            ClearUnsavedEnemyStatus();
        }, cancelToken.Token);

        if (!isAutomatic)
        {
            statusMessage = $"Queued export for {candidates.Length} new enemy model set(s) in territory {territoryId}.";
        }
    }

    private IEnumerable<CharacterBatchCandidate> FindBodyItemCandidates(
        nint templateSourceCharacterAddress,
        ParsedCharacterInfo templateCharacter,
        IReadOnlySet<EquipmentSlot> selectedSlots,
        bool testMode)
    {
        var raceCode = GetRaceCode(templateCharacter.GenderRace);
        var templates = BuildEquipmentTemplates(templateSourceCharacterAddress, templateCharacter, selectedSlots)
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
                itemName = $"item_{item.RowId}";
            }

            foreach (var slot in selectedSlots)
            {
                if (!IsTemplateApplicableToItem(slot, item))
                {
                    continue;
                }

                templates.TryGetValue(slot, out var template);
                if (!TryBuildItemExportModel(item, slot, template, raceCode, out var exportModel))
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
                    (int)item.RowId,
                    GetEquipmentSlotFolderName(slot));

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

            var identity = ResolveBattleNpcIdentity(character, modelPaths);
            var lookupKey = identity.LookupKey;
            if (!seenThisRun.Add(lookupKey))
            {
                continue;
            }

            if (existingLookup.ContainsKey(lookupKey))
            {
                continue;
            }

            yield return new CharacterBatchCandidate(
                identity.ExportName,
                identity.DisplayName,
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
        var composersByOutputDir = new Dictionary<string, CharacterComposer>(StringComparer.OrdinalIgnoreCase);
        var exportedCount = 0;

        foreach (var candidate in candidates)
        {
            cancelToken.Token.ThrowIfCancellationRequested();
            var candidateOutputDir = string.IsNullOrWhiteSpace(candidate.ExportSubfolder)
                ? outputDir
                : Path.Combine(outputDir, candidate.ExportSubfolder);
            Directory.CreateDirectory(candidateOutputDir);
            if (!composersByOutputDir.TryGetValue(candidateOutputDir, out var composer))
            {
                composer = composerFactory.CreateCharacterComposer(candidateOutputDir, exportConfig, cancelToken.Token);
                composersByOutputDir[candidateOutputDir] = composer;
            }

            var scene = new SceneBuilder();
            var root = new NodeBuilder(candidate.ExportName);
            scene.AddNode(root);

            composer.Compose(candidate.CharacterInfo, scene, root, new ExportProgress(candidate.CharacterInfo.Models.Count, candidate.DisplayName));
            ExportUtil.SaveAsType(scene.ToGltf2(), exportConfig.ExportType, candidateOutputDir, candidate.ExportName);
            if (inlineBuffer)
            {
                InlineGltfBuffer(Path.Combine(candidateOutputDir, candidate.ExportName.SanitizeFileName() + ".gltf"));
                DeleteUnexpectedItemExportFiles(candidateOutputDir, candidate.ExportName);
            }
            onExported(candidate, candidate.ExportName);
            rootProgress.IncrementProgress();
            exportedCount++;

            if (exportedCount % 10 == 0)
            {
                Thread.Sleep(15);
            }

            if (exportedCount % 100 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
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

    private int EstimateBodyItemExportTotal(int selectedSlotCount)
    {
        try
        {
            var itemCount = dataManager.GetExcelSheet<Item>().Count();
            return Math.Max(1, itemCount * Math.Max(1, selectedSlotCount));
        }
        catch
        {
            return 1000;
        }
    }

    private string GetZoneEnemyOutputDirectory(uint territoryId)
    {
        var locationFolder = GetCurrentLocationFolderName(territoryId);
        if (!string.IsNullOrWhiteSpace(config.BatchZoneEnemyExportDirectory))
        {
            return Path.Combine(config.BatchZoneEnemyExportDirectory, locationFolder);
        }

        return Path.Combine(config.ExportDirectory, "BatchExports", "ZoneEnemies", locationFolder);
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
        if (json == null || json["buffers"] is not JsonArray buffers || buffers.Count == 0)
        {
            return;
        }

        var gltfDirectory = Path.GetDirectoryName(gltfPath)!;
        var binFilesToDelete = new List<string>();
        var changed = false;

        foreach (var node in buffers)
        {
            if (node is not JsonObject bufferObject)
            {
                continue;
            }

            var uri = bufferObject["uri"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var binPath = Path.GetFullPath(Path.Combine(gltfDirectory, uri));
            if (!File.Exists(binPath))
            {
                continue;
            }

            bufferObject["uri"] = $"data:application/octet-stream;base64,{Convert.ToBase64String(File.ReadAllBytes(binPath))}";
            binFilesToDelete.Add(binPath);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        File.WriteAllText(gltfPath, json.ToJsonString());
        foreach (var binPath in binFilesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(binPath))
            {
                File.Delete(binPath);
            }
        }
    }

    private static void DeleteUnexpectedItemExportFiles(string outputDir, string exportName)
    {
        var sanitizedName = exportName.SanitizeFileName();
        foreach (var extension in new[] { ".obj", ".mtl", ".glb" })
        {
            var filePath = Path.Combine(outputDir, sanitizedName + extension);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (isDisposed)
        {
            return;
        }

        try
        {
            TryStartAutomaticZoneEnemyExport();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic zone enemy export check failed");
            statusMessage = $"Automatic zone enemy export check failed: {ex.Message}";
        }
    }

    private void TryStartAutomaticZoneEnemyExport()
    {
        if (!config.EnableAutomaticZoneEnemyExport)
        {
            return;
        }

        if (automaticZoneEnemyExportPending)
        {
            return;
        }

        if (!exportTask.IsCompleted)
        {
            return;
        }

        var territoryId = clientState.TerritoryType;
        if (territoryId == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (territoryId != lastAutoEnemyTerritoryId)
        {
            lastAutoEnemyTerritoryId = territoryId;
            lastAutoEnemyScanUtc = DateTime.MinValue;
        }

        if (now - lastAutoEnemyScanUtc < TimeSpan.FromSeconds(config.AutomaticZoneEnemyExportIntervalSeconds))
        {
            return;
        }

        lastAutoEnemyScanUtc = now;
        pendingAutomaticZoneEnemyTerritoryId = territoryId;
        automaticZoneEnemyExportPending = true;
    }

    private CancellationTokenSource CreateOwnedCancellationTokenSource()
    {
        var cts = new CancellationTokenSource();
        ownedCancellationSources.Add(cts);
        return cts;
    }

    private void ProcessPendingAutomaticZoneEnemyExport()
    {
        if (isDisposed || !automaticZoneEnemyExportPending)
        {
            return;
        }

        if (!exportTask.IsCompleted)
        {
            return;
        }

        var scheduledTerritoryId = pendingAutomaticZoneEnemyTerritoryId;
        if (!config.EnableAutomaticZoneEnemyExport)
        {
            automaticZoneEnemyExportPending = false;
            return;
        }

        if (clientState.TerritoryType != scheduledTerritoryId)
        {
            automaticZoneEnemyExportPending = false;
            return;
        }

        automaticZoneEnemyExportPending = false;
        StartZoneEnemyExport(isAutomatic: true);
    }

    private string GetCurrentLocationFolderName(uint territoryId)
    {
        try
        {
            var territory = dataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);
            var placeName = territory.PlaceName.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(placeName))
            {
                return $"{placeName.SanitizeFileName()}_{territoryId:D4}";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve place name for territory {TerritoryId}", territoryId);
        }

        return $"territory_{territoryId:D4}";
    }

    private void UpdateUnsavedEnemyStatus(IReadOnlyList<CharacterBatchCandidate> candidates)
    {
        lastUnsavedEnemyCount = candidates.Count;
        lastUnsavedEnemyPreview = candidates.Count > 0 ? candidates[0].ExportName : null;
        lastUnsavedEnemyDetectedUtc = candidates.Count > 0 ? DateTime.UtcNow : null;
    }

    private void ClearUnsavedEnemyStatus()
    {
        lastUnsavedEnemyCount = 0;
        lastUnsavedEnemyPreview = null;
        lastUnsavedEnemyDetectedUtc = null;
    }

    private static string GetEquipmentSlotFolderName(EquipmentSlot slot)
    {
        return slot switch
        {
            EquipmentSlot.MainHand => "MainHand",
            EquipmentSlot.OffHand => "OffHand",
            EquipmentSlot.Head => "Head",
            EquipmentSlot.Body => "Body",
            EquipmentSlot.Hands => "Hands",
            EquipmentSlot.Legs => "Legs",
            EquipmentSlot.Feet => "Shoes",
            EquipmentSlot.Earrings => "Earrings",
            EquipmentSlot.Necklace => "Necklace",
            EquipmentSlot.Wrists => "Wrists",
            EquipmentSlot.Ring => "Rings",
            _ => "Other"
        };
    }

    private BattleNpcIdentity ResolveBattleNpcIdentity(ICharacter character, IReadOnlyList<string> modelPaths)
    {
        var displayName = string.IsNullOrWhiteSpace(character.Name.TextValue)
            ? $"battle_npc_{character.BaseId}"
            : character.Name.TextValue.Trim();
        var bnpcNameId = GetUIntPropertyValue(character, "NameId", "BNpcNameId");
        var bnpcBaseId = character.BaseId;
        var bnpcModelId = ResolveBattleNpcModelId(bnpcBaseId);
        var exportName = $"{displayName.SanitizeFileName()}__{bnpcNameId}__{bnpcBaseId}__{bnpcModelId}";
        var lookupKey = string.Join("|",
        [
            displayName,
            bnpcNameId.ToString(),
            bnpcBaseId.ToString(),
            bnpcModelId.ToString(),
            .. modelPaths
        ]);

        return new BattleNpcIdentity(displayName, exportName, lookupKey, bnpcNameId, bnpcBaseId, bnpcModelId);
    }

    private uint ResolveBattleNpcModelId(uint bnpcBaseId)
    {
        if (bnpcBaseId == 0)
        {
            return 0;
        }

        try
        {
            var row = dataManager.GetExcelSheet<BNpcBase>().GetRow(bnpcBaseId);
            return row.ModelChara.RowId;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve BNpcBase model id for base id {BNpcBaseId}", bnpcBaseId);
            return 0;
        }
    }

    private static uint GetUIntPropertyValue(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
            switch (value)
            {
                case uint uintValue:
                    return uintValue;
                case ushort ushortValue:
                    return ushortValue;
                case byte byteValue:
                    return byteValue;
                case int intValue when intValue >= 0:
                    return (uint)intValue;
            }
        }

        return 0;
    }

    private IEnumerable<EquipmentTemplate> BuildEquipmentTemplates(
        nint templateSourceCharacterAddress,
        ParsedCharacterInfo templateCharacter,
        IReadOnlySet<EquipmentSlot> selectedSlots)
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

        var fallbackTemplates = new List<EquipmentTemplate>();
        unsafe
        {
            var templateSourceCharacter = (Character*)templateSourceCharacterAddress;
            if (templateSourceCharacter == null || templateSourceCharacter->DrawObject == null)
            {
                yield break;
            }

            var drawObject = templateSourceCharacter->DrawObject;
            if (drawObject->GetObjectType() != ObjectType.CharacterBase)
            {
                yield break;
            }

            var characterBase = (CharacterBase*)drawObject;
            if (characterBase->GetModelType() != CharacterBase.ModelType.Human)
            {
                yield break;
            }

            foreach (var slot in selectedSlots)
            {
                if (yieldedSlots.Contains(slot))
                {
                    continue;
                }

                if (!TryResolveFallbackTemplate((nint)characterBase, slot, out var fallbackTemplate))
                {
                    continue;
                }

                yieldedSlots.Add(slot);
                fallbackTemplates.Add(fallbackTemplate);
            }
        }

        foreach (var fallbackTemplate in fallbackTemplates)
        {
            yield return fallbackTemplate;
        }
    }

    private bool TryResolveFallbackTemplate(nint characterBaseAddress, EquipmentSlot slot, out EquipmentTemplate template)
    {
        template = null!;
        var slotIndex = GetHumanModelSlotIndex(slot);
        if (slotIndex == null)
        {
            return false;
        }

        unsafe
        {
            var characterBase = (CharacterBase*)characterBaseAddress;
            if (characterBase == null)
            {
                return false;
            }

            var modelPath = characterBase->ResolveMdlPath((byte)slotIndex.Value);
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return false;
            }

            var parsedModel = resolverService.ParseModelFromPath(modelPath);
            if (parsedModel == null)
            {
                return false;
            }

            template = new EquipmentTemplate(slot, parsedModel);
            return true;
        }
    }

    private static HumanModelSlotIndex? GetHumanModelSlotIndex(EquipmentSlot slot)
    {
        return slot switch
        {
            EquipmentSlot.Head => HumanModelSlotIndex.Head,
            EquipmentSlot.Body => HumanModelSlotIndex.Top,
            EquipmentSlot.Hands => HumanModelSlotIndex.Arms,
            EquipmentSlot.Legs => HumanModelSlotIndex.Legs,
            EquipmentSlot.Feet => HumanModelSlotIndex.Feet,
            EquipmentSlot.Earrings => HumanModelSlotIndex.Ear,
            EquipmentSlot.Necklace => HumanModelSlotIndex.Neck,
            EquipmentSlot.Wrists => HumanModelSlotIndex.Wrist,
            EquipmentSlot.Ring => HumanModelSlotIndex.RFinger,
            _ => null
        };
    }

    private bool TryBuildItemExportModel(Item item, EquipmentSlot slot, EquipmentTemplate? template, string raceCode, out ParsedModelInfo exportModel)
    {
        exportModel = null!;
        if (!TryResolveItemModelInfo(item, slot, out var itemModel))
        {
            return false;
        }

        var candidatePaths = new List<string>();
        if (template != null)
        {
            candidatePaths.Add(ApplyModelToTemplatePath(template.Model.Path.FullPath, slot, itemModel));
        }

        candidatePaths.AddRange(BuildCanonicalItemModelPaths(slot, itemModel, raceCode));
        var candidatePath = candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => pack.FileExists(path, out _));

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        if (template != null && string.Equals(candidatePath, ApplyModelToTemplatePath(template.Model.Path.FullPath, slot, itemModel), StringComparison.OrdinalIgnoreCase))
        {
            var exportMaterials = ApplyModelToTemplateMaterials(template.Model.Materials, slot, itemModel);
            exportModel = new ParsedModelInfo(
                candidatePath,
                ApplyModelToTemplatePath(template.Model.Path.GamePath, slot, itemModel),
                true,
                template.Model.Deformer,
                template.Model.ShapeAttributeGroup,
                exportMaterials,
                null,
                null);

            return true;
        }

        var resolvedModel = resolverService.ParseModelFromPath(candidatePath);
        if (resolvedModel == null)
        {
            return false;
        }

        exportModel = new ParsedModelInfo(
            candidatePath,
            candidatePath,
            true,
            template?.Model.Deformer,
            template?.Model.ShapeAttributeGroup,
            resolvedModel.Materials,
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

    private static IEnumerable<string> BuildCanonicalItemModelPaths(EquipmentSlot slot, ItemModelInfo itemModel, string raceCode)
    {
        switch (slot)
        {
            case EquipmentSlot.MainHand:
            case EquipmentSlot.OffHand:
                yield return $"chara/weapon/w{itemModel.PrimaryId:D4}/obj/body/b{itemModel.SecondaryId:D4}/model/w{itemModel.PrimaryId:D4}b{itemModel.SecondaryId:D4}.mdl";
                yield break;
            case EquipmentSlot.Head:
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/{raceCode}e{itemModel.PrimaryId:D4}_met.mdl";
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/c0101e{itemModel.PrimaryId:D4}_met.mdl";
                yield break;
            case EquipmentSlot.Body:
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/{raceCode}e{itemModel.PrimaryId:D4}_top.mdl";
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/c0101e{itemModel.PrimaryId:D4}_top.mdl";
                yield break;
            case EquipmentSlot.Hands:
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/{raceCode}e{itemModel.PrimaryId:D4}_glv.mdl";
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/c0101e{itemModel.PrimaryId:D4}_glv.mdl";
                yield break;
            case EquipmentSlot.Legs:
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/{raceCode}e{itemModel.PrimaryId:D4}_dwn.mdl";
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/c0101e{itemModel.PrimaryId:D4}_dwn.mdl";
                yield break;
            case EquipmentSlot.Feet:
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/{raceCode}e{itemModel.PrimaryId:D4}_sho.mdl";
                yield return $"chara/equipment/e{itemModel.PrimaryId:D4}/model/c0101e{itemModel.PrimaryId:D4}_sho.mdl";
                yield break;
            case EquipmentSlot.Earrings:
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/{raceCode}a{itemModel.PrimaryId:D4}_ear.mdl";
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/c0101a{itemModel.PrimaryId:D4}_ear.mdl";
                yield break;
            case EquipmentSlot.Necklace:
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/{raceCode}a{itemModel.PrimaryId:D4}_nek.mdl";
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/c0101a{itemModel.PrimaryId:D4}_nek.mdl";
                yield break;
            case EquipmentSlot.Wrists:
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/{raceCode}a{itemModel.PrimaryId:D4}_wrs.mdl";
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/c0101a{itemModel.PrimaryId:D4}_wrs.mdl";
                yield break;
            case EquipmentSlot.Ring:
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/{raceCode}a{itemModel.PrimaryId:D4}_rir.mdl";
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/{raceCode}a{itemModel.PrimaryId:D4}_ril.mdl";
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/c0101a{itemModel.PrimaryId:D4}_rir.mdl";
                yield return $"chara/accessory/a{itemModel.PrimaryId:D4}/model/c0101a{itemModel.PrimaryId:D4}_ril.mdl";
                yield break;
        }
    }

    private static string GetRaceCode(Meddle.Utils.Constants.GenderRace genderRace)
    {
        var raceCode = (ushort)genderRace;
        return raceCode == 0 ? "c0101" : $"c{raceCode:D4}";
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
        int? ItemId = null,
        string? ExportSubfolder = null);
    private sealed record BattleNpcIdentity(
        string DisplayName,
        string ExportName,
        string LookupKey,
        uint BNpcNameId,
        uint BNpcBaseId,
        uint BNpcModelId);

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
