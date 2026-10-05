// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

/// <summary>
/// Holds the evaluated ambient snapshot, selected source aliases, and composed policy overlay.
/// </summary>
internal sealed class NuGetConfiguration
{
    public NuGetConfiguration(
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> configSources,
        NuGetConfigOverlay? overlay)
    {
        Settings = settings with
        {
            ConfigPaths = [.. settings.ConfigPaths],
            Sources = [.. settings.Sources],
            SensitiveSourceValues = [.. settings.SensitiveSourceValues],
            PackageSourceMappings = [.. settings.PackageSourceMappings],
            DisabledPackageSourceKeys = [.. settings.DisabledPackageSourceKeys],
            ReservedPackageSourceKeys = [.. settings.ReservedPackageSourceKeys],
            SourceIdentityKey = [.. settings.SourceIdentityKey]
        };
        ConfigSources = [.. configSources];
        Overlay = overlay;
    }

    public NuGetSettingsInfo Settings { get; }

    public IReadOnlyList<NuGetConfigSource> ConfigSources { get; }

    public NuGetConfigOverlay? Overlay { get; }
}

internal sealed record NuGetConfigSource(
    string Key,
    string Source,
    bool IsAmbient,
    bool IsEnabled);
