namespace Linkbase.Core;

public static class RepoPaths
{
    public const string Corrections = "data/corrections.json";

    // Default paths are relative to the repo root, wherever the app was started from.
    public static string Resolve(string relativePath, string? startDirectory = null)
    {
        var directory = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Linkbase.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("Run this from inside the repo: Linkbase.sln wasn't found above the current folder.")
            : Path.Combine(directory.FullName, relativePath);
    }
}
