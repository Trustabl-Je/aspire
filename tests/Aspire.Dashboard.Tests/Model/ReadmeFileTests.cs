// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ReadmeFileTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindPath_PrefersRepositoryRootReadme(bool worktree)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var appHost = workspace.CreateDirectory("src/AppHost");
        if (worktree)
        {
            File.WriteAllText(Path.Combine(workspace.Path, ".git"), "gitdir: elsewhere");
        }
        else
        {
            workspace.CreateDirectory(".git");
        }

        var rootReadme = Path.Combine(workspace.Path, "README.md");
        File.WriteAllText(rootReadme, "# Repository");
        File.WriteAllText(Path.Combine(appHost.FullName, "README.md"), "# AppHost");

        Assert.Equal(rootReadme, ReadmeFile.FindPath(appHost.FullName, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindPath_FallsBackToNearestReadme(bool gitRepository)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        if (gitRepository)
        {
            workspace.CreateDirectory(".git");
        }
        var appHost = workspace.CreateDirectory("src/AppHost");
        var readme = Path.Combine(appHost.FullName, "README.md");
        File.WriteAllText(readme, "# AppHost");
        File.WriteAllText(Path.Combine(workspace.Path, "src", "README.md"), "# Parent");

        Assert.Equal(readme, ReadmeFile.FindPath(appHost.FullName, null));
    }

    [Fact]
    public void FindPath_DoesNotSearchOutsideRepository()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        File.WriteAllText(Path.Combine(workspace.Path, "README.md"), "# Outside");
        workspace.CreateDirectory("repo/.git");
        var appHost = workspace.CreateDirectory("repo/AppHost");

        Assert.Null(ReadmeFile.FindPath(appHost.FullName, null));
    }

    [Fact]
    public void FindPath_NoConfiguration_DoesNotDiscoverFiles()
    {
        Assert.Null(ReadmeFile.FindPath(null, null));
    }

    [Fact]
    public void FindPath_ExplicitRelativePath_OverridesDiscovery()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        Assert.Equal(Path.Combine(workspace.Path, "tutorial.md"), ReadmeFile.FindPath(workspace.Path, "tutorial.md"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ReadmeFile.MaxFileSize)]
    public async Task ReadAsync_ReadsFileWithinLimit(int length)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Path.Combine(workspace.Path, "README.md");
        var content = new string('a', length);
        await File.WriteAllTextAsync(path, content);

        Assert.Equal(content, await ReadmeFile.ReadAsync(path));
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedFile()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Path.Combine(workspace.Path, "README.md");
        await File.WriteAllTextAsync(path, new string('a', ReadmeFile.MaxFileSize + 1));

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadmeFile.ReadAsync(path));
    }
}
