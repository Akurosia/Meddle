using Dalamud.Plugin.Services;
using Meddle.Formats.Helpers;

namespace Meddle.Plugin.Utils;

public static class PathUtil
{
    // Lumina still fails on certain textures, such as arrays.
    public static byte[]? GetFileOrReadFromDisk(this IDataManager pack, string path)
    {
        path = path.TrimHandlePath();

        if (Path.IsPathRooted(path))
        {
            return File.ReadAllBytes(path);
        }

        return pack.GetFile(path)?.Data;
    }
}
