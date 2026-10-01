// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Projects;

public class AppHostConfigurationProjectorTests
{
    private const string ConfiguredImage = "example.com/aspire-tunnel:configured";

    [Fact]
    public void ApplyEnvironmentVariables_ProjectsConfiguredValue()
    {
        var configuration = new ConfigurationManager
        {
            [AspireConfigContainerTunnel.BaseImageConfigPath] = ConfiguredImage
        };
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(configuration, new TestEnvironment());

        projector.ApplyEnvironmentVariables(environmentVariables);

        Assert.Equal(ConfiguredImage, environmentVariables[KnownConfigNames.ContainerTunnelBaseImage]);
    }

    [Fact]
    public void ApplyEnvironmentVariables_DoesNotReplaceExplicitLaunchValue()
    {
        const string launchImage = "example.com/aspire-tunnel:launch";
        var configuration = new ConfigurationManager
        {
            [AspireConfigContainerTunnel.BaseImageConfigPath] = ConfiguredImage
        };
        var environmentVariables = new Dictionary<string, string>
        {
            [KnownConfigNames.ContainerTunnelBaseImage] = launchImage
        };
        var projector = new AppHostConfigurationProjector(configuration, new TestEnvironment());

        projector.ApplyEnvironmentVariables(environmentVariables);

        Assert.Equal(launchImage, environmentVariables[KnownConfigNames.ContainerTunnelBaseImage]);
    }

    [Fact]
    public void ApplyEnvironmentVariables_DoesNotReplaceAmbientValue()
    {
        const string ambientImage = "example.com/aspire-tunnel:ambient";
        var configuration = new ConfigurationManager
        {
            [AspireConfigContainerTunnel.BaseImageConfigPath] = ConfiguredImage
        };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [KnownConfigNames.ContainerTunnelBaseImage] = ambientImage
        });
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(configuration, environment);

        projector.ApplyEnvironmentVariables(environmentVariables);

        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.ContainerTunnelBaseImage));
    }

    [Fact]
    public void ApplyEnvironmentVariables_IgnoresEmptyConfiguredValue()
    {
        var configuration = new ConfigurationManager
        {
            [AspireConfigContainerTunnel.BaseImageConfigPath] = string.Empty
        };
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(configuration, new TestEnvironment());

        projector.ApplyEnvironmentVariables(environmentVariables);

        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.ContainerTunnelBaseImage));
    }
}
