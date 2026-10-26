// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

internal static class ReadmeFile
{
    internal const int MaxFileSize = 1024 * 1024;

    public static string? FindPath(string? directory, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath, directory ?? Environment.CurrentDirectory);
        }

        // Standalone dashboards must not accidentally expose files from their installation directory.
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string? nearestReadme = null;
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var readme = Path.Combine(current.FullName, "README.md");
            var exists = File.Exists(readme);
            if (exists)
            {
                nearestReadme ??= readme;
            }

            // Worktrees have a .git file rather than a directory. Never search outside the repository.
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return exists ? readme : nearestReadme;
            }
        }

        return nearestReadme;
    }

    public static async Task<string> ReadAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
        // Bound the actual read as well as the initial length because an editor can change the file while it is open.
        if (stream.Length > MaxFileSize)
        {
            throw new InvalidDataException("The README exceeds the maximum supported size of 1 MiB.");
        }

        using var reader = new StreamReader(stream);
        var buffer = new char[MaxFileSize + 1];
        var count = await reader.ReadBlockAsync(buffer).ConfigureAwait(false);
        if (count > MaxFileSize)
        {
            throw new InvalidDataException("The README exceeds the maximum supported size of 1 MiB.");
        }

        return new string(buffer, 0, count);
    }
}
