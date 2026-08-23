using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Interop;
using Meddle.Formats.Files;
using Meddle.Formats.Files.MdlFile;
using Meddle.Formats.Files.MtrlFile;
using Meddle.Formats.Helpers;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Layout;
using Meddle.Plugin.Utils;
using Meddle.Utils.Helpers;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

public class ResolverService : IService
{
    private readonly ILogger<ResolverService> logger;
    private readonly LayoutService layoutService;
    private readonly SqPack.SqPack pack;
    private readonly IFramework framework;
    private readonly PbdHooks pbdHooks;
    private readonly SigUtil sigUtil;

    public ResolverService(
        ILogger<ResolverService> logger,
        LayoutService layoutService,
        SqPack.SqPack pack,
        IFramework framework,
        PbdHooks pbdHooks,
        SigUtil sigUtil)
    {
        this.logger = logger;
        this.layoutService = layoutService;
        this.pack = pack;
        this.framework = framework;
        this.pbdHooks = pbdHooks;
        this.sigUtil = sigUtil;
    }
    
    
    public void ResolveInstances(params ParsedInstance[] instances)
    {
        framework.RunOnTick(() =>
        {
            foreach (var instance in instances)
            {
                ResolveInstance(instance);
            }
        }).GetAwaiter().GetResult();
    }
    
    public static bool IsCharacterKind(ObjectKind kind)
    {
        return kind switch
        {
            ObjectKind.Pc => true,
            ObjectKind.Mount => true,
            ObjectKind.Companion => true,
            ObjectKind.Retainer => true,
            ObjectKind.BattleNpc => true,
            ObjectKind.EventNpc => true,
            ObjectKind.Ornament => true,
            _ => false
        };
    }

    private unsafe void ResolveParsedCharacterInstance(ParsedCharacterInstance characterInstance)
    {
        var objects = layoutService.ParseObjects();
        // check to ensure the character instance is still valid
        if (objects.Any(o => o.Id == characterInstance.Id))
        {
            if (characterInstance.IdType == ParsedCharacterInstance.ParsedCharacterInstanceIdType.CharacterBase)
            {
                var cBase = (CharacterBase*)characterInstance.Id;
                var characterInfo = ParseMaterialUtil.ParseDrawObject(&cBase->DrawObject, pbdHooks);
                characterInstance.CharacterInfo = characterInfo;
            }
            else
            {
                var gameObject = (GameObject*)characterInstance.Id;
                if (IsCharacterKind(gameObject->ObjectKind))
                {
                    var characterInfo = ParseCharacter((Character*)gameObject, true);
                    characterInstance.CharacterInfo = characterInfo;
                }
                else
                {
                    var characterInfo = ParseMaterialUtil.ParseDrawObject(gameObject->DrawObject, pbdHooks);
                    characterInstance.CharacterInfo = characterInfo;
                }
            }
        }
        else
        {
            logger.LogWarning("Character instance {Id} no longer exists", characterInstance.Id);
        }
    }
    
    private void ResolveParsedTerrainInstance(ParsedTerrainInstance terrainInstance)
    {
        var path = terrainInstance.Path;
        var teraPath = $"{terrainInstance.Path.GamePath}/bgplate/terrain.tera";
        var teraData = pack.GetFileOrReadFromDisk(teraPath);
        
        if (teraData == null)
        {
            logger.LogWarning("Failed to load terrain.tera for {Path}", path);
            return;
        }
        
        var terrain = new TeraFile(teraData);
        terrainInstance.Data = new ParsedTerrainInstanceData(terrain);
    }
    
    private void ResolveInstance(ParsedInstance instance)
    {
        if (instance is ParsedCharacterInstance {IsResolved: false} characterInstance)
        {
            ResolveParsedCharacterInstance(characterInstance);
        }

        if (instance is ParsedTerrainInstance {IsResolved: false} terrainInstance)
        {
            ResolveParsedTerrainInstance(terrainInstance);
        }

        if (instance is ParsedSharedInstance sharedInstance)
        {
            foreach (var child in sharedInstance.Children)
            {
                ResolveInstance(child);
            }
        }
    }
    
    /// <summary>
    /// Used for terrain
    /// </summary>
    public ParsedModelInfo? ParseModelFromPath(string path)
    {
        var modelData = pack.GetFileOrReadFromDisk(path);
        if (modelData == null)
        {
            logger.LogWarning("Failed to load model file: {Path}", path);
            return null;
        }

        var mdlFile = new MdlFile(modelData);
        var materials = new List<ParsedMaterialInfo>();
        var mtrlNames = mdlFile.GetMaterialNames().Select(x => x.Value)
                               .ToArray();
        foreach (var mtrlName in mtrlNames)
        {
            var resolvedMtrlPath = ResolveRelatedGamePath(path, mtrlName, "material");
            
            var mtrlData = pack.GetFileOrReadFromDisk(resolvedMtrlPath);
            if (mtrlData == null)
            {
                logger.LogWarning("Failed to load material file: {Path}", resolvedMtrlPath);
                continue;
            }
            
            var mtrlFile = new MtrlFile(mtrlData);
            var shaderName = mtrlFile.GetShaderPackageName();
            var colorTable = mtrlFile.GetColorTable();
            
            var textures = new List<ParsedTextureInfo>();
            var textureNames = mtrlFile.GetTexturePaths().Select(x => x.Value)
                                       .ToArray();
            for (var texIdx = 0; texIdx < textureNames.Length; texIdx++)
            {
                var texName = ResolveRelatedGamePath(resolvedMtrlPath, textureNames[texIdx], "texture");
                var texData = pack.GetFileOrReadFromDisk(texName);
                if (texData == null)
                {
                    logger.LogWarning("Failed to load texture file: {Path}", texName);
                    continue;
                }
                
                var texFile = new TexFile(texData);
                var texRes = texFile.ToResource();
                var texInfo = new ParsedTextureInfo(texName, texName, texRes);
                textures.Add(texInfo);
            }

            var materialInfo = new ParsedMaterialInfo(resolvedMtrlPath, resolvedMtrlPath, shaderName, null, colorTable, textures.ToArray());
            
            materials.Add(materialInfo);
        }

        var modelInfo = new ParsedModelInfo(path, path, true, null, null, materials.ToArray(), null, null);
        return modelInfo;
    }

    private static string ResolveRelatedGamePath(string sourcePath, string targetPath, string siblingFolder)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return targetPath;
        }

        if (!targetPath.StartsWith('/'))
        {
            return targetPath;
        }

        var normalizedSource = sourcePath.Replace('\\', '/');
        var sourceDir = normalizedSource[..normalizedSource.LastIndexOf('/')];

        if (siblingFolder == "material" && sourceDir.EndsWith("/model", StringComparison.OrdinalIgnoreCase))
        {
            var siblingDir = $"{sourceDir[..^"/model".Length]}/{siblingFolder}/v0001";
            return $"{siblingDir}/{targetPath.TrimStart('/')}";
        }

        if (sourceDir.Contains("/material/", StringComparison.OrdinalIgnoreCase))
        {
            var materialIndex = sourceDir.LastIndexOf("/material/", StringComparison.OrdinalIgnoreCase);
            var materialRoot = sourceDir[..materialIndex];
            var versionSegment = sourceDir[(materialIndex + "/material/".Length)..];
            var version = versionSegment.Split('/')[0];
            return $"{materialRoot}/{siblingFolder}/{version}/{targetPath.TrimStart('/')}";
        }

        return $"{sourceDir}/{targetPath.TrimStart('/')}";
    }

    public ParsedCharacterInfo? ParseDrawObject(Pointer<DrawObject> drawObject)
    {
        return ParseMaterialUtil.ParseDrawObject(drawObject, pbdHooks);
    }

    private unsafe Character* FindCharacterByDrawObject(DrawObject* drawObject)
    {
        if (drawObject == null)
            return null;

        var gameObjectManager = sigUtil.GetGameObjectManager();
        if (gameObjectManager == null)
            return null;

        for (var idx = 0; idx < gameObjectManager->Objects.GameObjectIdSorted.Length; idx++)
        {
            var objectPtr = gameObjectManager->Objects.GameObjectIdSorted[idx];
            if (objectPtr.Value == null)
                continue;

            var obj = objectPtr.Value;
            if (!IsCharacterKind(obj->GetObjectKind()))
                continue;

            if (obj->DrawObject == drawObject)
                return (Character*)obj;
        }

        return null;
    }

    public unsafe ParsedCharacterInfo? ParseCharacter(Character* character, bool includedLinkedAttaches = false)
    {
        if (character == null)
        {
            return null;
        }
        
        var drawObject = character->DrawObject;
        if (drawObject == null)
        {
            return null;
        }
        
        var characterInfo = ParseMaterialUtil.ParseDrawObject(drawObject, pbdHooks);
        if (characterInfo == null)
        {
            return null;
        }
        
        var attaches = new List<ParsedCharacterInfo>();
        var mountInfo = ParseCharacter(character->Mount.MountObject);
        if (mountInfo != null)
        {
            attaches.Add(mountInfo);
        }
        
        var ornamentInfo = ParseCharacter((Character*)character->OrnamentData.OrnamentObject);
        if (ornamentInfo != null)
        {
            attaches.Add(ornamentInfo);
        }

        foreach (var weapon in character->DrawData.WeaponData)
        {
            var weaponInfo = ParseMaterialUtil.ParseDrawObject(weapon.DrawData.DrawObject, pbdHooks);
            if (weaponInfo != null)
            {
                attaches.Add(weaponInfo);
            }
        }

        if (includedLinkedAttaches)
        {
            List<Pointer<CharacterBase>> linked = [];
            try
            {
                linked = StructExtensions.GetLinkedAttaches(character, sigUtil);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve Attach vtable, skipping linked attaches");
            }

            foreach (var childCBasePtr in linked)
            {
                var childCBase = childCBasePtr.Value;
                if (childCBase == null)
                    continue;

                var linkedCharacter = FindCharacterByDrawObject((DrawObject*)childCBase);
                var linkedInfo = linkedCharacter != null
                    ? ParseCharacter(linkedCharacter, true)
                    : ParseMaterialUtil.ParseDrawObject((DrawObject*)childCBase, pbdHooks);
                if (linkedInfo != null)
                    attaches.Add(linkedInfo);
            }
        }

        characterInfo.Attaches = attaches.ToArray();

        return characterInfo;
    }
}
