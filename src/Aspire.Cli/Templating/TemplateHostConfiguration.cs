// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Resources;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Utils;

namespace Aspire.Cli.Templating;

internal sealed class TemplateHostConfiguration
{
    private readonly Dictionary<string, SymbolInfo> _symbols = new(StringComparer.Ordinal);

    public bool IsHidden(string name) => _symbols.TryGetValue(name, out var symbol) && symbol.IsHidden;

    public string? GetDisplayName(string name) => _symbols.TryGetValue(name, out var symbol) ? symbol.DisplayName : null;

    public static async Task<TemplateHostConfiguration> LoadAsync(
        IEngineEnvironmentSettings settings, ITemplateInfo template, CancellationToken cancellationToken)
    {
        var configuration = new TemplateHostConfiguration();
        if (string.IsNullOrEmpty(template.HostConfigPlace))
        {
            return configuration;
        }

        // The engine selects one host file: aspirecli.host.json, falling back to
        // dotnetcli.host.json. Read that file from the template's mount, never merge them.
        if (!settings.TryGetMountPoint(template.MountPointUri, out var mount) || mount is null)
        {
            throw new FileNotFoundException(null, template.MountPointUri);
        }

        using (mount)
        {
            var file = mount.FileInfo(template.HostConfigPlace)
                ?? throw new FileNotFoundException(null, template.HostConfigPlace);
            try
            {
                using var stream = file.OpenRead();
                using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }, cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                }
                if (!root.TryGetProperty("symbolInfo", out var symbols))
                {
                    return configuration;
                }
                if (symbols.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                }

                // CLI host files use {"symbolInfo":{"Port":{"isHidden":true}}}.
                // The .NET CLI schema also permits the strings "true" and "false".
                foreach (var symbol in symbols.EnumerateObject())
                {
                    if (symbol.Value.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                    }

                    var hidden = false;
                    if (symbol.Value.TryGetProperty("isHidden", out var isHidden))
                    {
                        hidden = isHidden.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String when isHidden.GetString() == "true" => true,
                            JsonValueKind.String when isHidden.GetString() == "false" => false,
                            _ => throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat)
                        };
                    }

                    string? displayName = null;
                    if (symbol.Value.TryGetProperty("displayName", out var label))
                    {
                        if (label.ValueKind != JsonValueKind.String)
                        {
                            throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                        }
                        displayName = label.GetString();
                    }
                    configuration._symbols[symbol.Name] = new SymbolInfo(hidden, displayName);
                }
            }
            catch (JsonException ex)
            {
                throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                    string.Format(CultureInfo.CurrentCulture, TemplatingStrings.InvalidTemplateHostConfiguration,
                        file.GetDisplayPath(), ex.Message));
            }
        }

        return configuration;
    }

    private sealed record SymbolInfo(bool IsHidden, string? DisplayName);
}
