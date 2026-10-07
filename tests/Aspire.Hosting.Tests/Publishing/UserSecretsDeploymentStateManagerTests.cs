// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREUSERSECRETS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json;
using Aspire.Hosting.Pipelines.Internal;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class UserSecretsDeploymentStateManagerTests
{
    [Fact]
    public async Task SaveSectionAsync_DoesNotLogAzureMessage_WhenSavingNonAzureSection()
    {
        var logger = new FakeLogger<UserSecretsDeploymentStateManager>();
        var stateManager = new UserSecretsDeploymentStateManager(logger, new MockUserSecretsManager());

        var section = await stateManager.AcquireSectionAsync("Parameters");
        section.Data["foo"] = "bar";
        await stateManager.SaveSectionAsync(section);

        var logs = logger.Collector.GetSnapshot();
        Assert.DoesNotContain(logs, log => log.Message.Contains("Azure", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Level == LogLevel.Debug &&
            log.Message.Contains("Deployment state saved to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveSectionAsync_DoesNotLogAzureError_WhenUserSecretsAreMalformed()
    {
        var logger = new FakeLogger<UserSecretsDeploymentStateManager>();
        var userSecretsManager = new MockUserSecretsManager { SaveStateException = new JsonException() };
        var stateManager = new UserSecretsDeploymentStateManager(logger, userSecretsManager);

        var section = await stateManager.AcquireSectionAsync("Parameters");
        section.Data["foo"] = "bar";
        await Assert.ThrowsAsync<JsonException>(() => stateManager.SaveSectionAsync(section));

        var error = Assert.Single(logger.Collector.GetSnapshot(), log => log.Level == LogLevel.Error);
        Assert.DoesNotContain("Azure", error.Message, StringComparison.Ordinal);
    }
}
