# Directory and Git-backed templates

This document describes the current implementation of custom templates in
`aspire new`. It records the command contract, component responsibilities, Git
acquisition model, parameter handling, and NuGet configuration ownership rules
for subsequent implementation work. It is not a proposal for repository
catalogs or centralized configuration.

## Scope and approach

Custom templates are unpacked .NET template packages, supplied either as a local
directory or as a directory inside a Git repository. Both sources use the same
in-process .NET template engine after source acquisition.

The implementation fits the existing `ITemplateProvider` / `ITemplateFactory`
model rather than adding a separate project-generation mechanism. It does not
invoke `dotnet new`, install templates into the user's `dotnet new` hive, or
require a .NET SDK for template generation. Concrete built-in engine components
are registered instead of dynamically loading template-engine extensions, so
the flow works in the Native AOT CLI.

The boundaries are deliberate: Git acquisition provides a pinned source tree;
the engine understands template metadata and transformations; the CLI owns
interaction, identity projection, and optional creation of missing NuGet
configuration.

## Command contract

Use an unpacked template package:

```bash
aspire new --template-path ./my-template
aspire new --template-path ./my-template --name MyApp --output ./MyApp
```

Use a repository root or a template subdirectory:

```bash
aspire new --template-repository https://github.com/example/templates.git
aspire new --template-repository https://github.com/example/templates.git --template-path templates/my-template
aspire new --template-repository git@github.com:example/templates.git --template-path templates/my-template --name MyApp --output ./MyApp
aspire new --template-repository https://github.com/example/templates.git --template-path templates/my-template --template-repository-ref v1.0
```

| Option | Behavior |
| --- | --- |
| `--template-path` without a repository | Absolute path, or a path relative to the invocation's working directory |
| `--template-path` with a repository | Repository-relative directory; defaults to `.` when omitted |
| `--template-repository` | Repository address passed to the Git acquisition layer |
| `--template-repository-ref` | Branch, tag, or full commit SHA; requires a repository and defaults to remote `HEAD` |

Repository-relative paths must not be absolute or escape the repository.
The selected directory and its ancestors within the checkout are checked for
symbolic links on both cache hits and cache misses.

These source options belong to `aspire new`; they are not inherited by its
built-in template subcommands. Custom sources cannot be combined with a built-in
template name, `--source`, `--version`, `--channel`, or `--language`. They do not
query NuGet for template packages.

The package directory must contain `.template.config/template.json`, directly
or within template subdirectories. When multiple templates are discovered, an
interactive invocation prompts for selection. Noninteractive invocations require
a source that resolves to a single template.

Existing non-empty output directories are rejected. Project name and output
selection use the existing new-command prompting and validation helpers.
Optional agent initialization still applies; `--suppress-agent-init` skips it.

## Component responsibilities

| Component | Responsibility |
| --- | --- |
| [`NewCommand`](../../src/Aspire.Cli/Commands/NewCommand.cs) | Validate option combinations, select a discovered template, and complete the normal new-command flow |
| [`TemplateProvider`](../../src/Aspire.Cli/Templating/TemplateProvider.cs) | Aggregate factory results through the existing provider abstraction |
| [`TemplateEngineFactory`](../../src/Aspire.Cli/Templating/TemplateEngineFactory.cs) | Acquire source trees, own the checkout cache, discover templates, collect parameters, and instantiate output |
| `TemplateEngineHost` | Register concrete engine components and exclude Git metadata from the engine's source view |
| [`TemplateHostConfiguration`](../../src/Aspire.Cli/Templating/TemplateHostConfiguration.cs) | Read the selected host file's parameter visibility and prompt labels without merging fallback files |
| `TemplateParameterPromptState` | Evaluate interactive parameter conditions and order their dependencies using engine types and expressions |
| [`IGitRunner` / `GitRunner`](../../src/Aspire.Cli/Git/GitRunner.cs) | Resolve refs, acquire exact commits, and expose checkout-free file access |
| [`TemplateNuGetConfigService`](../../src/Aspire.Cli/Templating/TemplateNuGetConfigService.cs) | Create missing output-root NuGet configuration without taking ownership of template-generated configuration |
| [`PackageChannel`](../../src/Aspire.Cli/Packaging/PackageChannel.cs) and `PackagingService` | Resolve package-source policy from the running CLI identity and overrides |
| [`BundleNuGetService`](../../src/Aspire.Cli/NuGet/BundleNuGetService.cs) | Build and persist NuGet policy using the existing configuration hierarchy |

The template-engine host, settings, and package manager remain alive until the
factory is disposed because returned template callbacks still use their mounts.
The engine's settings hive is virtualized and separate from the user's
`dotnet new` state.

## Git acquisition and caching

### Reference resolution

Named refs are resolved to a commit before selecting a cache entry. Resolution
runs on each invocation even when a previous checkout exists, so a moving branch
or tag selects its new commit instead of silently using an old checkout.

An unqualified branch name takes precedence over a tag with the same name.
Annotated tags resolve to their peeled commit. Use `refs/tags/name` to select a
tag explicitly when a branch has the same name.

Full 40- or 64-character hexadecimal object IDs are accepted as commit pins and
normalized to lowercase without a remote ref lookup. Abbreviated object IDs are
not supported as commit pins. Git must be on `PATH` for remote ref resolution or
acquisition; an existing checkout pinned by a full commit ID does not require a
new Git invocation.

On a cache miss, acquisition initializes a repository, fetches only the resolved
commit with depth one and no tag traversal, checks it out detached, and verifies
`HEAD`. It fetches the commit ID rather than the original branch or tag, so a ref
moving between resolution and download cannot change the selected version.
A server that refuses the pinned fetch causes a failure, not a fallback to a
different ref.

### Cache identity and layout

The default cache location is:

```text
~/.aspire/cache/git/<hash>/
  .git/
  repository/
    <checked-out files>
```

The root is derived from `CliExecutionContext.CacheDirectory`. The key is the
lowercase hexadecimal XxHash3 hash of the UTF-8 repository address, a NUL
separator, and the resolved commit SHA. It is a cache identifier, not a
cryptographic integrity check.

The template path and original ref name are not part of the key. Different
template directories and refs resolving to the same repository and commit share
one checkout. Repository addresses are used as supplied; equivalent URL spellings
are not canonicalized into a common cache identity.

Acquisition uses a leased temporary directory. The checkout is validated before
atomic publication into the final cache location. If a concurrent invocation
publishes the same entry first, the completed winner is reused and the losing
temporary checkout is cleaned up. Failure and cancellation do not publish an
incomplete entry; cleanup failures are logged.

On Unix and macOS, the cache root uses owner-only (`0700`) permissions, including
repair of existing root permissions on reuse. Completed entries are retained
after generation. There is no automatic eviction policy; manual removal must
avoid entries in active use.

Cached checkouts are treated as immutable source snapshots. Reuse validates the
selected path but does not perform a fresh checkout or an integrity scan of every
cached file. Generated files and NuGet configuration are written to the output
directory, not into the source tree or retained cache.

### Authentication, environment, and failures

Normal Git credential helpers and SSH authentication configuration remain
available. Git's terminal credential prompts are always disabled with
`GIT_TERMINAL_PROMPT=0`, including when Aspire itself is interactive.
Authentication must be configured before invocation.

Git processes run through `IProcessExecutionFactory`, with `LC_ALL=C` and
`--literal-pathspecs`. Repository-routing environment variables are filtered
case-insensitively:

```text
GIT_DIR
GIT_WORK_TREE
GIT_INDEX_FILE
GIT_OBJECT_DIRECTORY
GIT_ALTERNATE_OBJECT_DIRECTORIES
GIT_COMMON_DIR
GIT_NAMESPACE
GIT_SHALLOW_FILE
```

Authentication settings are not removed by this filter. Arguments, stdout, and
raw stderr diagnostics are suppressed from process logging because they can
contain private repository information or credentials. The runner classifies
failures and exposes localized messages through `GitFailure`; callers translate
them into command errors.

Each public Git operation has a two-minute deadline. Initialization, fetching,
checkout, and verification share one deadline within a clone operation. Caller
cancellation remains cancellation; expiry of the runner's own deadline becomes a
classified timeout.

### Reusable checkout-free operations

The Git abstraction is not template-specific. In addition to ref resolution and
checkout acquisition, it supports:

- Shallow fetching an exact commit into a bare repository and verifying it.
- Looking up the blob ID of a regular file at an exact commit without checkout.
- Reading blob bytes with a caller-supplied maximum size.

Blob lookup uses repository-relative forward-slash paths and rejects empty,
`.` and `..` segments, backslashes, and control characters. Symlinks, directories,
and gitlinks are not accepted as regular files. Blob reads check the object size
before reading content and verify the resulting byte count.

Blob stdout is redirected through a caller-owned `StandardOutputHandle` in the
shared process-execution options. This preserves encoding and line endings rather
than passing binary content through line-oriented text callbacks. The caller
owns the handle's lifetime.

Consumers own parsing, schema validation, size policy, and cache lifetime. The
template factory currently owns its checkout cache; these APIs do not introduce
a shared bare-object pool or a cache manager for unrelated consumers.

## Template parameters and generation

### Host-specific presentation

The engine host identifier is `aspirecli`, with `dotnetcli` as its fallback.
For each template, host metadata is selected from its own `.template.config`
directory in this order:

1. `aspirecli.host.json`.
2. `dotnetcli.host.json`, only when the Aspire-specific file is absent.
3. Default prompting when neither file exists.

One file is authoritative. There is no property-level merging: an empty Aspire
host file deliberately replaces the .NET CLI host file, and an invalid selected
file is an error rather than a reason to fall back. `ide.host.json` is not used;
it has a different schema and describes an IDE-specific experience.

Aspire supports the .NET CLI host file's `symbolInfo` object and `isHidden`
property, plus an optional `displayName` override for terminal prompt labels:

```json
{
  "symbolInfo": {
    "Framework": { "displayName": "Target framework" },
    "appHostHttpPort": { "isHidden": true },
    "skipRestore": { "isHidden": true },
    "UseRedisCache": { "displayName": "Use Redis for caching" }
  }
}
```

`isHidden` accepts JSON Booleans and the strings `"true"` and `"false"`, as
allowed by the .NET CLI schema. Omitted entries and omitted `isHidden` properties
leave parameters visible. Other .NET CLI metadata, such as argument aliases,
does not introduce custom-parameter command-line switches in Aspire.

Visibility controls prompting, not template semantics. Hidden parameters are
left unset rather than submitted with their displayed defaults. This distinction
matters for `coalesce` symbols, which distinguish user-supplied port overrides
from defaults and otherwise use generated random ports. Hidden controllers'
defaults still participate in interactive condition evaluation, and the engine
still rejects missing required values. The well-known `AspireVersion` and
`AspireNuGetSource` projections remain active even when marked hidden.

The existing templates under `src/Aspire.ProjectTemplates/templates` include
Aspire-specific host files. They retain framework, feature, test, HTTPS, and
localhost-TLD choices where applicable, while hiding port overrides and restore
controls. Prompt labels avoid IDE keyboard mnemonics. Because there is no merge,
each of these host files includes its complete set of hiding rules.

Host metadata changes the interactive questionnaire, not the generation model.
The .NET CLI itself primarily exposes arguments and applies defaults; Aspire
continues to prompt for ordinary visible parameters.

### Prompting and engine execution

The CLI prompts only for eligible parameter symbols, not derived, computed, or
generated symbols. Name handling uses the normal project-name flow; implicit and
disabled-precedence parameters are not prompted.

| Metadata | Interactive behavior |
| --- | --- |
| Text parameter | String prompt with the declared default and requiredness |
| Boolean parameter | Confirmation prompt using the declared default |
| Single choice | Selection using choice display names and the declared default |
| Optional single choice without a default | Initial **Skip** option that leaves the value unset |
| Multiple choice | Multiple selection with declared defaults preselected |
| `isEnabled` / `isRequired` expressions | Engine-backed condition evaluation; controlling parameters are collected before dependents |

Prompt labels use a nonempty host-file `displayName`, then the parameter display
name, description, and finally symbol name.
Ordinary interactive parameters still prompt when they have defaults. Multiple
choice values use the engine's pipe-delimited representation, such as
`web|worker`.

In noninteractive mode, the CLI injects its well-known values but leaves other
parameters unset for the engine to apply defaults and validate requirements.
Missing mandatory inputs are errors. A declared default should not be assumed to
bypass the engine's mandatory-input validation. There are no dynamically
generated command-line switches for supplying arbitrary custom parameter values.

The engine handles replacements, derived and generated symbols, conditional
content, and file renaming. A dry run validates the request before instantiation.
Template post-actions, including restore and scripts, are returned as metadata
and are not executed.

Git metadata, including `.git` directories and worktree/submodule pointer files,
is excluded from the engine's source view without modifying the original source.
Other Git-related files, including `.gitignore`, `.gitattributes`, and `.github`
contents, remain available to the template's normal inclusion rules.

After instantiation, the CLI applies missing-config policy before reporting
successful creation. Successful command completion also waits for required
bundle extraction so a generated .NET AppHost can run without relying on a
later CLI invocation to extract the bundle.

Generation is not a transaction with rollback: an error after instantiation,
such as failure to create required NuGet configuration, can leave generated
files behind. The command reports failure rather than successful creation.

## CLI identity and well-known parameters

Well-known parameter names are case-insensitive and override template defaults
without prompting. They must be declared as parameter symbols to participate.
Use string-valued parameters rather than closed choice lists so the engine can
accept the CLI's stable, prerelease, and local version or feed identities.

### `AspireVersion`

`AspireVersion` receives `CliExecutionContext.IdentitySdkVersion`: the running
CLI's package version, including its prerelease or PR suffix but excluding SemVer
build metadata (`+...`). CLI identity overrides are honored.

```json
{
  "symbols": {
    "AspireVersion": {
      "type": "parameter",
      "datatype": "string",
      "replaces": "{{aspireVersion}}"
    }
  }
}
```

Injecting this parameter also opts the template into automatic creation of a
missing output-root `nuget.config`. A template that does not participate in this
version projection does not automatically inherit the CLI's NuGet policy.

### `AspireNuGetSource`

`AspireNuGetSource` receives the source details of the resolved identity channel.
The current channel projection selects its first Aspire-prefixed mapping, or its
catch-all mapping when no Aspire-prefixed mapping exists. It is a single raw URL
or directory path, not the complete mapping policy.

Typical values are nuget.org for stable, the appropriate daily or staging feed,
and a local package directory for PR/local builds. Package-directory and NuGet
service-index identity overrides are honored.

Both well-known parameters resolve against the same identity channel, using
`PackagingService.GetChannelsAsync` without querying package feeds. A missing
matching channel or missing channel mappings causes an error before generation;
the flow does not silently choose an unrelated channel.

Templates own encoding the raw source value for their output format. For a
double-quoted XML attribute, the engine's `xmlEncode` form must be combined with
quote escaping:

```json
{
  "symbols": {
    "AspireNuGetSource": {
      "type": "parameter",
      "datatype": "string",
      "defaultValue": "https://api.nuget.org/v3/index.json"
    },
    "aspireNuGetSourceXml": {
      "type": "derived",
      "valueSource": "AspireNuGetSource",
      "valueTransform": "xmlAttribute",
      "replaces": "{{aspireNuGetSourceXml}}"
    }
  },
  "forms": {
    "xmlQuotes": {
      "identifier": "replace",
      "pattern": "\"",
      "replacement": "&quot;"
    },
    "xmlAttribute": {
      "identifier": "chain",
      "steps": ["xmlEncode", "xmlQuotes"]
    }
  }
}
```

This parameter remains useful for templates that intentionally generate their
own configuration. It is not required for automatic missing-config creation,
which uses the full resolved channel mappings instead.

## NuGet configuration ownership

There are two distinct ownership models:

- **Template-owned:** the engine emits a root `nuget.config`; the CLI preserves
  the generated file instead of merging its own policy into it.
- **CLI-created:** version projection opts in, no root config exists, and the
  resolved policy requires custom sources; the CLI creates the missing file.

The decision is based on actual resolved sources, not version suffixes or the
channel name alone. `PackageChannel.ShouldCreateNuGetConfig()` checks whether
any resolved mapping uses a source other than the default nuget.org source.
A stable-shaped version can require a staging feed, and a stable-named identity
can have an explicit local-package or service-index override.

### Scenario matrix

The automatic-creation rows assume the flow has injected `AspireVersion`.

| Scenario | Missing output-root config | Existing template-generated output-root config |
| --- | --- | --- |
| Released stable using normal nuget.org sources | Leave absent; use ambient configuration | Preserve |
| Public preview whose resolved sources are only nuget.org | Leave absent; prerelease status alone is not a reason to create a file | Preserve |
| Daily feed | Create the required source policy | Preserve |
| Staging feed, including stable-shaped versions | Create source policy and applicable channel package-cache settings | Preserve |
| PR/local package directory | Create policy using the resolved directory | Preserve |
| Stable identity with an explicit custom package source | Create policy for the override | Preserve |
| No version projection | Do not automatically create a config | Preserve |
| Required identity channel or mappings unavailable | Fail before generation | Fail before generation when well-known parameter injection requires that channel |
| Required configuration cannot be written | Fail the command without reporting successful creation | No CLI rewrite is attempted |

Existing root filenames are recognized case-insensitively, and preserved
byte-for-byte, including formatting and package-cache settings. Ambiguous root
files that differ only by case are rejected by the shared discovery helper.
The CLI does not parse, repair, or validate a template-owned config in this
preservation path.

Automatic creation reuses `BundleNuGetService` and the existing NuGet
configuration hierarchy. It does not construct policy by writing a source URL
into a fixed XML string. Inherited configuration is considered when calculating
the local policy; ancestor and user-level files are not edited. Nested configs
are not edited either and retain their normal NuGet precedence.

Only the missing output-root file is created. A nested config can override that
root policy; creating a root file is not a mechanism for forcing every nested
project to use the same sources.

Credential-bearing source URLs are not persisted by automatic creation. The
existing credential-material check conservatively rejects HTTP(S) URLs with
userinfo, query strings, or fragments. Authentication should use NuGet's normal
authentication configuration rather than credentials embedded in a source URL.
Creation uses exclusive create-new semantics rather than overwriting a file
that appeared after the missing-file check.

### Enterprise integrations sharing the Aspire prefix

An enterprise can publish private `Aspire.Hosting.*` packages alongside public
Aspire packages. The prefix alone does not identify which feed owns an
integration. Such a template can intentionally supply its own config with:

- A public fallback mapping, `*`, to nuget.org.
- A conditional `Aspire*` mapping to the CLI-projected daily, staging, or local
  source.
- Exact private-package mappings to an enterprise feed.

For example, private package IDs could include `Aspire.Hosting.Enterprise.Deployment`,
`Aspire.Provisioning.Enterprise.Deployment`, `Aspire.Hosting.Enterprise.Defaults`,
`Azure.Provisioning.Enterprise.Defaults`, and `Aspire.Hosting.Azure.EnterpriseGateway`.
These are representative fixture names, not a built-in package inventory.

Under [NuGet's mapping precedence](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping#package-pattern-precedence),
exact package IDs outrank prefix patterns, and longer prefixes outrank shorter
prefixes and `*`. Exact private mappings therefore continue to select the
enterprise feed even when a broad `Aspire*` mapping selects a different source.

| Package group | Normal stable source | Separate Aspire source enabled |
| --- | --- | --- |
| Exact enterprise package IDs | Enterprise feed | Enterprise feed |
| Other `Aspire*` packages | Public fallback | CLI-projected Aspire source |
| Other dependencies | Public fallback | Public fallback |

The template owns its conditional source emission and any independent enterprise
version constraints. The CLI neither discovers the private feed from package
names nor rewrites enterprise dependencies to match its own package versions.
Keeping the generated config authoritative avoids removing the exact mappings,
duplicating sources, or replacing enterprise package-cache settings.

## Coverage and maintenance

Unit coverage exercises generated output and effective package routing, not just
whether a source URL appears in XML.

| Tests | Coverage |
| --- | --- |
| [`NewCommandLocalTemplateTests`](../../tests/Aspire.Cli.Tests/Commands/NewCommandLocalTemplateTests.cs) | Discovery, metadata-driven prompts, engine defaults and validation, identity projection, missing-config scenarios, enterprise mappings, Git/cache preservation, and failure reporting |
| [`NewCommandLocalTemplateTests.HostConfiguration`](../../tests/Aspire.Cli.Tests/Commands/NewCommandLocalTemplateTests.HostConfiguration.cs) | Host-file precedence without merging, invalid configuration, all prompt types, generated-port fallbacks, hidden requirements and controllers, and curated prompts from the actual built-in template sources |
| [`PackageChannelTests`](../../tests/Aspire.Cli.Tests/Packaging/PackageChannelTests.cs) | Source-based config-creation decisions, including public previews and stable overrides |
| [`TemplateNuGetConfigServiceTests`](../../tests/Aspire.Cli.Tests/Templating/TemplateNuGetConfigServiceTests.cs) | Byte-preserving ownership, filename casing, inherited/nested configuration, and idempotence |
| `NewCommandTests`, `InitCommandTests`, and template-factory tests | Existing built-in new/init consumers of shared channel policy |
| `GitRunnerTests` and `GitBlobTests` | Ref resolution, exact-commit acquisition, environment isolation, deadlines, classified failures, and bounded byte-preserving reads |
| [`CustomTemplateTests`](../../tests/Aspire.Cli.EndToEnd.Tests/CustomTemplateTests.cs) | Installed native CLI generation without a .NET SDK, including directory, Git, and noninteractive-default scenarios |

NuGet routing assertions use `NuGetTestHelper.GetEligiblePackageSources`, which
evaluates the configuration with NuGet's actual mapping resolver. Enterprise
cases check each private package ID and the public/fallback routes separately.
Verify snapshots cover the generated files; byte-level assertions cover config
preservation.

The focused template and shared-policy regression selection is:

```bash
dotnet test --project tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj --no-launch-profile -- \
  --filter-class "*.NewCommandLocalTemplateTests" \
  --filter-class "*.PackageChannelTests" \
  --filter-class "*.TemplateNuGetConfigServiceTests" \
  --filter-class "*.DotNetTemplateFactoryTests" \
  --filter-class "*.CliTemplateFactoryTests" \
  --filter-class "*.NewCommandTests" \
  --filter-class "*.DotNetAppHostNuGetConfigMergerTests" \
  --filter-class "*.InitCommandTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

Use the repository's restore/build prerequisites before running tests. Filters
are Microsoft.Testing.Platform arguments after `--`, not VSTest `--filter`
expressions.

## Current boundaries

Repository catalogs, centralized template configuration, submodule acquisition,
automatic cache eviction, and arbitrary custom-parameter command-line switches
are not implemented by this flow. Template post-actions and dynamically loaded
template-engine extensions are not enabled.

The generic Git runner is available for other consumers, but sharing its
operations does not itself share checkout-cache ownership. A future consumer
must deliberately define cache lifetime, parsing, validation, and size limits.

Likewise, missing-config creation must not be treated as an existing-config
migration mechanism. Any later feature that merges or migrates template-owned
NuGet configuration needs its own ownership contract and tests preserving
enterprise routing. These are boundaries of the current implementation, not
promises that the additional features already exist.
