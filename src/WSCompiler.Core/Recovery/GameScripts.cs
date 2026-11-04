using System.IO;

namespace WSCompiler.Core.Scripts;

// Find vanilla scripts from the blob's location when the caller has not provided them.
public static class GameScripts
{
    // Fallback game installation for blobs outside the game directory.
    public static string? GameFolder { get; set; }

    public static string? FindFolder(string nearPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(nearPath)) ?? "");
        for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "content0", "scripts");
            if (Directory.Exists(candidate)) return candidate;
        }
        if (!string.IsNullOrWhiteSpace(GameFolder))
        {
            string fallback = Path.Combine(GameFolder, "content", "content0", "scripts");
            if (Directory.Exists(fallback)) return fallback;
        }
        return null;
    }
}
