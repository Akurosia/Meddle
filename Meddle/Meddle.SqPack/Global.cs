using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meddle.SqPack;

public class Global
{
    public static ILogger Logger { get; set; } = NullLogger.Instance;
}
