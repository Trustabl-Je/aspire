// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Templating;

internal sealed class ConfiguredTemplateSourceService(
    IConfigurationService configurationService,
    ITemplateProvider templateProvider,
    IInteractionService interactionService)
{
    internal async Task<IReadOnlyList<KeyValuePair<string, TemplateSource>>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var settingsPath = configurationService.GetSettingsFilePath(isGlobal: true);
        try
        {
            using var settings = JsonDocument.Parse(
                await File.ReadAllTextAsync(settingsPath, cancellationToken), ConfigurationHelper.ParseOptions);
            // Sources are path/URL strings or Git objects, e.g.
            // { "templates": { "local": "/absolute/path",
            //   "remote": { "repository": "https://host/repo", "ref": "main", "path": "templates" } } }.
            // Read the JSON directly to preserve value types and avoid project-config overrides.
            if (settings.RootElement.ValueKind is not JsonValueKind.Object)
            {
                Warn(settingsPath, NewCommandStrings.TemplateSourcesMustBeObject);
                return [];
            }
            if (!settings.RootElement.TryGetProperty("templates", out var templates))
            {
                return [];
            }
            if (templates.ValueKind is not JsonValueKind.Object)
            {
                Warn(settingsPath, NewCommandStrings.TemplateSourcesMustBeObject);
                return [];
            }

            List<KeyValuePair<string, TemplateSource>> sources = [];
            foreach (var entry in templates.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.Name) || ParseSource(entry.Value) is not { } source)
                {
                    Warn(entry.Name, NewCommandStrings.InvalidConfiguredTemplateSource);
                    continue;
                }

                sources.Add(new(entry.Name, source));
            }

            return sources;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warn(settingsPath, ex.Message);
            return [];
        }
    }

    private static TemplateSource? ParseSource(JsonElement value)
    {
        if (ReadSourceString(value) is { } source)
        {
            return Path.IsPathFullyQualified(source)
                ? new TemplateSource(source, null, null)
                : GitRepositoryUrl.IsValid(source) ? new TemplateSource(".", source, null) : null;
        }

        if (value.ValueKind is not JsonValueKind.Object ||
            value.EnumerateObject().Any(property => property.Name is not ("repository" or "ref" or "path")) ||
            !value.TryGetProperty("repository", out var repositoryElement) ||
            ReadSourceString(repositoryElement) is not { } repository ||
            !GitRepositoryUrl.IsValid(repository))
        {
            return null;
        }

        var path = value.TryGetProperty("path", out var pathElement) ? ReadSourceString(pathElement) : ".";
        if (path is null)
        {
            return null;
        }

        string? reference = null;
        if (value.TryGetProperty("ref", out var refElement))
        {
            reference = ReadSourceString(refElement);
            if (reference is null)
            {
                return null;
            }
        }

        // The shared acquisition pipeline validates repository-relative paths before fetching
        // and checks for symlink traversal after checkout, just as it does for explicit options.
        return new TemplateSource(path, repository, reference);
    }

    private static string? ReadSourceString(JsonElement value) =>
        value.ValueKind is JsonValueKind.String &&
        value.GetString() is { } text &&
        !string.IsNullOrWhiteSpace(text) &&
        !text.Any(char.IsControl) ? text : null;

    public async Task<IReadOnlyList<ConfiguredTemplate>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(cancellationToken);
        if (sources.Count == 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, NewCommandStrings.NoConfiguredTemplateSources);
            return [];
        }

        List<ConfiguredTemplate> results = [];
        // The template engine owns mutable mount/resource state. Discover sequentially rather
        // than sharing a factory across concurrent source installations.
        foreach (var (name, source) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = string.Format(CultureInfo.CurrentCulture, NewCommandStrings.SearchingTemplateSource, name);
                IEnumerable<ITemplate> templates;
                if (source.Repository is null)
                {
                    templates = await interactionService.ShowStatusAsync(status,
                        () => templateProvider.GetTemplatesAsync(source, cancellationToken));
                }
                else
                {
                    // The Git acquisition path owns its resolve/clone spinners. An enclosing
                    // status would hide those stages behind a generic search spinner.
                    interactionService.DisplaySubtleMessage(status);
                    templates = await templateProvider.GetTemplatesAsync(source, cancellationToken);
                }

                results.AddRange(templates
                    .Where(template => template.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        template.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(template => new ConfiguredTemplate(name, template)));
            }
            catch (Exception ex) when (ex is TemplateDiscoveryException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Warn(name, ex.Message);
            }
        }

        return results.OrderBy(result => result.Template.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Warn(string source, string message) =>
        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(
            CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed, source, message));
}

internal sealed record ConfiguredTemplate(string SourceName, ITemplate Template);
