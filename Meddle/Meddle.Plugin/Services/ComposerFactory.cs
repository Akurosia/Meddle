using Meddle.Plugin.Models.Composer;

namespace Meddle.Plugin.Services;

public class ComposerFactory : IService
{
    private readonly SqPack.SqPack pack;

    public ComposerFactory(SqPack.SqPack pack)
    {
        this.pack = pack;
    }
    
    public InstanceComposer CreateComposer(string outDir,
                                           Configuration.ExportConfiguration exportConfig,
                                           CancellationToken cancellationToken = default)
    {
        return new InstanceComposer(pack, exportConfig,
                                    outDir, cancellationToken);

    }
    
    public CharacterComposer CreateCharacterComposer(string outDir,
                                                     Configuration.ExportConfiguration exportConfig,
                                                     CancellationToken cancellationToken = default)
    {
        return new CharacterComposer(pack, exportConfig, outDir, cancellationToken);
    }
}
