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
            // Named sources can be paths or URLs, e.g.
            // { "templates": { "local": "/absolute/path", "remote": "https://host/repo" } }.
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
                if (string.IsNullOrWhiteSpace(entry.Name) ||
                    entry.Value.ValueKind is not JsonValueKind.String ||
                    entry.Value.GetString() is not { } value ||
                    string.IsNullOrWhiteSpace(value) ||
                    value.Any(char.IsControl))
                {
                    Warn(entry.Name, NewCommandStrings.InvalidConfiguredTemplateSource);
                    continue;
                }

                if (Path.IsPathFullyQualified(value))
                {
                    sources.Add(new(entry.Name, new TemplateSource(value, null, null)));
                }
                else if (GitRepositoryUrl.IsValid(value))
                {
                    sources.Add(new(entry.Name, new TemplateSource(".", value, null)));
                }
                else
                {
                    Warn(entry.Name, NewCommandStrings.InvalidConfiguredTemplateSource);
                }
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
