// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Combines evaluated ambient NuGet settings with a selected package-source policy.
/// </summary>
internal static class NuGetConfigurationBuilder
{
    public static NuGetConfiguration Build(
        NuGetSettingsInfo settings,
        string workloadId,
        PackageMapping[]? selectedMappings,
        bool restrictToSelectedSources,
        string? packageScopedAppendSource,
        bool hasAuthoritativeAspirePolicy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);
        if (restrictToSelectedSources && selectedMappings is not { Length: > 0 })
        {
            throw new ArgumentException("Source-restricted configuration requires a selected source.", nameof(selectedMappings));
        }

        var configSources = ResolveSourceAliases(
            selectedMappings,
            workloadId,
            settings.Sources,
            settings.ReservedPackageSourceKeys,
            settings.SourceIdentityKey);

        var overlay = selectedMappings is null
            ? null
            : ComposeOverlay(
                selectedMappings,
                settings,
                configSources,
                restrictToSelectedSources,
                globalPackagesFolder: null,
                packageScopedAppendSource,
                hasAuthoritativeAspirePolicy);

        return new NuGetConfiguration(
            settings,
            configSources,
            overlay);
    }

    public static NuGetConfigSource[] ResolveSourceAliases(
        PackageMapping[]? mappings,
        string workloadId,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<string> reservedPackageSourceKeys,
        ReadOnlySpan<byte> sourceIdentityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        if (mappings is null)
        {
            return [];
        }

        var usedKeys = reservedPackageSourceKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedSourceKeyPrefix = $"aspire-{workloadId}";
        var sourceIndex = 0;
        var nextAdditionalSourceKey = 0;

        var resolvedSources = new List<NuGetConfigSource>();
        foreach (var source in mappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer))
        {
            // Source order still determines the primary source when its ambient alias is reused.
            var isPrimarySource = sourceIndex++ == 0;
            var sourceIdentity = NuGetSourceIdentity.Compute(source, sourceIdentityKey);
            var ambientMatches = ambientSources
                .Where(candidate => string.Equals(candidate.Identity, sourceIdentity, StringComparison.Ordinal))
                .ToArray();
            if (ambientMatches.Length > 0)
            {
                var enabledMatches = ambientMatches
                    .Where(static ambientSource => ambientSource.IsEnabled)
                    .ToArray();
                var selectedMatches = enabledMatches;
                if (selectedMatches.Length == 0)
                {
                    // Credentials and client certificates are attached to source aliases. When an
                    // equivalent source must be re-enabled, preserve the authenticated alias.
                    var preferredDisabledMatch = ambientMatches.FirstOrDefault(static ambientSource =>
                        ambientSource.HasCredentials || ambientSource.HasClientCertificates);
                    selectedMatches = [preferredDisabledMatch ?? ambientMatches[0]];
                }

                foreach (var ambientSource in selectedMatches)
                {
                    resolvedSources.Add(new NuGetConfigSource(
                        ambientSource.Name,
                        source,
                        IsAmbient: true,
                        ambientSource.IsEnabled));
                }

                continue;
            }

            string key;
            if (isPrimarySource)
            {
                key = generatedSourceKeyPrefix;
                if (!usedKeys.Add(key))
                {
                    throw new InvalidOperationException(
                        $"The generated NuGet source key '{key}' conflicts with an existing NuGet configuration key.");
                }
            }
            else
            {
                do
                {
                    key = $"{generatedSourceKeyPrefix}-{nextAdditionalSourceKey++}";
                }
                while (!usedKeys.Add(key));
            }

            resolvedSources.Add(new NuGetConfigSource(key, source, IsAmbient: false, IsEnabled: true));
        }

        return [.. resolvedSources];
    }

    public static NuGetConfigOverlay ComposeOverlay(
        PackageMapping[] selectedMappings,
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        bool restrictToSelectedSources,
        string? globalPackagesFolder,
        string? packageScopedAppendSource = null,
        bool hasAuthoritativeAspirePolicy = false)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var enabledSourceKeys = selectedSources
            .Where(static source => source.IsAmbient && !source.IsEnabled)
            .Select(static source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clearDisabledPackageSources = enabledSourceKeys.Count > 0;
        var disabledPackageSourceKeys = clearDisabledPackageSources
            ? settings.DisabledPackageSourceKeys
                .Where(key => !enabledSourceKeys.Contains(key))
                .ToArray()
            : [];
        var packageSourceMappings = ComposePackageSourceMappings(
            selectedMappings,
            settings.PackageSourceMappings,
            settings.Sources,
            selectedSources,
            packageScopedAppendSource,
            hasAuthoritativeAspirePolicy);

        if (restrictToSelectedSources)
        {
            // Search ignores mappings. Source-restricted operations must disable other aliases
            // without replacing the hierarchy that owns authentication and transport settings.
            var selectedKeys = selectedSources
                .Select(static source => source.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            clearDisabledPackageSources = true;
            disabledPackageSourceKeys = settings.Sources
                .Select(static source => source.Name)
                .Concat(settings.DisabledPackageSourceKeys)
                .Where(key => !selectedKeys.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // A disabled source's exact mapping can outrank the selected source's '*' mapping.
            // Remove its mappings as well so template installation can resolve from --source.
            packageSourceMappings = packageSourceMappings
                .Where(mapping => selectedKeys.Contains(mapping.SourceKey))
                .ToArray();
        }

        return new NuGetConfigOverlay(
            selectedSources
                .Where(static source => !source.IsAmbient)
                .Select(static source => (Key: source.Key, Source: source.Source))
                .ToArray(),
            packageSourceMappings,
            clearDisabledPackageSources,
            disabledPackageSourceKeys,
            globalPackagesFolder);
    }

    public static NuGetPackageSourceMapping[] ComposePackageSourceMappings(
        IReadOnlyList<PackageMapping> selectedMappings,
        IReadOnlyList<NuGetPackageSourceMapping> ambientMappings,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        string? packageScopedAppendSource = null,
        bool hasAuthoritativeAspirePolicy = false)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(ambientMappings);
        ArgumentNullException.ThrowIfNull(ambientSources);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var patternsBySourceKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hasPackageScopedAppendSource = packageScopedAppendSource is not null;

        if (ambientMappings.Count == 0)
        {
            // Enabling package-source mapping changes NuGet from "every enabled source can serve
            // every package" to deny-by-default. Reproduce that existing eligibility first.
            foreach (var ambientSource in ambientSources.Where(static source => source.IsEnabled))
            {
                AddPattern(patternsBySourceKey, ambientSource.Name, PackageMapping.AllPackages);
            }
        }
        else
        {
            foreach (var ambientMapping in ambientMappings)
            {
                foreach (var pattern in ambientMapping.Patterns)
                {
                    AddPattern(patternsBySourceKey, ambientMapping.SourceKey, pattern);
                }
            }
        }

        if (hasPackageScopedAppendSource && !hasAuthoritativeAspirePolicy)
        {
            // With no selected channel policy, ambient '*' mappings previously made those sources
            // eligible for Aspire packages. Promote that eligibility to the appended Aspire*
            // specificity so --source does not accidentally replace the ambient feeds.
            foreach (var (sourceKey, patterns) in patternsBySourceKey.ToArray())
            {
                if (patterns.Contains(PackageMapping.AllPackages, StringComparer.OrdinalIgnoreCase))
                {
                    AddPattern(
                        patternsBySourceKey,
                        sourceKey,
                        PackageSourceOverrideMappings.DefaultPackagePattern);
                }
            }
        }

        var authoritativePatterns = selectedMappings
            .Where(mapping =>
                mapping.PackageFilter != PackageMapping.AllPackages &&
                !(!hasAuthoritativeAspirePolicy &&
                  PackageSourceIdentity.Comparer.Equals(mapping.Source, packageScopedAppendSource) &&
                  string.Equals(
                      mapping.PackageFilter,
                      PackageSourceOverrideMappings.DefaultPackagePattern,
                      StringComparison.OrdinalIgnoreCase)))
            .Select(static mapping => mapping.PackageFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var authoritativeSources = selectedMappings
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .Select(static mapping => mapping.Source)
            .ToHashSet(PackageSourceIdentity.Comparer);
        foreach (var patterns in patternsBySourceKey.Values)
        {
            patterns.RemoveAll(pattern => authoritativePatterns.Any(
                authoritativePattern => PackageSourceOverrideMappings.CompetesWithAuthoritativePattern(
                    pattern,
                    authoritativePattern)));
        }

        foreach (var mapping in selectedMappings)
        {
            if (ambientMappings.Count > 0 &&
                mapping.PackageFilter == PackageMapping.AllPackages &&
                !authoritativeSources.Contains(mapping.Source))
            {
                // Existing mapping policy already owns unrelated package eligibility. Do not
                // broaden it with a channel fallback source.
                continue;
            }

            foreach (var source in selectedSources.Where(
                source => PackageSourceIdentity.Comparer.Equals(source.Source, mapping.Source)))
            {
                AddPattern(patternsBySourceKey, source.Key, mapping.PackageFilter);
            }
        }

        return patternsBySourceKey
            .Where(static mapping => mapping.Value.Count > 0)
            .Select(static mapping => new NuGetPackageSourceMapping(
                mapping.Key,
                [.. mapping.Value]))
            .ToArray();
    }

    private static void AddPattern(
        Dictionary<string, List<string>> patternsBySourceKey,
        string sourceKey,
        string pattern)
    {
        if (!patternsBySourceKey.TryGetValue(sourceKey, out var patterns))
        {
            patterns = [];
            patternsBySourceKey.Add(sourceKey, patterns);
        }

        if (!patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            patterns.Add(pattern);
        }
    }
}
