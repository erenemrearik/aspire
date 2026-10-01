// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Projects;

/// <summary>
/// Projects Aspire CLI configuration into the environment of a launched AppHost.
/// </summary>
internal sealed class AppHostConfigurationProjector(IConfiguration configuration, IEnvironment environment)
{
    private static readonly EnvironmentVariableProjection[] s_environmentVariableProjections =
    [
        new(AspireConfigContainerTunnel.BaseImageConfigPath, KnownConfigNames.ContainerTunnelBaseImage)
    ];

    /// <summary>
    /// Applies configured AppHost environment variables without replacing explicit launch or ambient values.
    /// </summary>
    public void ApplyEnvironmentVariables(IDictionary<string, string> environmentVariables)
    {
        foreach (var projection in s_environmentVariableProjections)
        {
            if (environmentVariables.Keys.Contains(projection.EnvironmentVariableName, StringComparer.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(environment.GetEnvironmentVariable(projection.EnvironmentVariableName)))
            {
                continue;
            }

            if (configuration[projection.ConfigurationPath] is { Length: > 0 } value)
            {
                environmentVariables[projection.EnvironmentVariableName] = value;
            }
        }
    }

    private readonly record struct EnvironmentVariableProjection(
        string ConfigurationPath,
        string EnvironmentVariableName);
}
