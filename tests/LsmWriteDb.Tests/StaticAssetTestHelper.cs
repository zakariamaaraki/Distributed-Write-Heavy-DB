using System.Runtime.CompilerServices;

namespace LsmWriteDb.Tests;

internal static class StaticAssetTestHelper
{
    public static string Read(
        string relativePath,
        [CallerFilePath] string sourceFilePath = "")
    {
        foreach (var root in CandidateRoots(sourceFilePath))
        {
            var candidate = Path.Combine(root, "wwwroot", relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not find static asset '{relativePath}'.");
    }

    private static IEnumerable<string> CandidateRoots(string sourceFilePath)
    {
        foreach (var start in new[]
        {
            Path.GetDirectoryName(sourceFilePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LsmWriteDb.csproj")))
                {
                    yield return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
    }
}
