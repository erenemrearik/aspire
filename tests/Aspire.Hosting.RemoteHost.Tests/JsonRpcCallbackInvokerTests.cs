// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class JsonRpcCallbackInvokerTests
{
    [Fact]
    public async Task IntegrationCallbackTimeoutRetiresConnection()
    {
        using var connection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(Array.Empty<object>()),
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true"
        }).Build();
        await using var invoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance, configuration);
        invoker.SetConnection(connection.ServerRpc);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => invoker.InvokeAsync<JsonNode>(
            "stall", null, TestContext.Current.CancellationToken, TimeSpan.Zero));

        Assert.True(invoker.LifetimeToken.IsCancellationRequested);
        Assert.Equal("Callback 'stall' timed out after 0s; its owner connection was retired.", exception.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.InvokeAsync<JsonNode>(
            "responsive", null, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task OrdinaryCallbackTimeoutLeavesConnectionUsable()
    {
        using var connection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(Array.Empty<object>()),
            async (callbackId, _, cancellationToken) =>
            {
                if (callbackId == "stall")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return JsonValue.Create("responsive");
            });
        await using var invoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance);
        invoker.SetConnection(connection.ServerRpc);

        await Assert.ThrowsAsync<TimeoutException>(() => invoker.InvokeAsync<JsonNode>(
            "stall", null, TestContext.Current.CancellationToken, TimeSpan.Zero));

        Assert.True(invoker.IsConnected);
        var result = await invoker.InvokeAsync<JsonNode>(
            "responsive", null, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10));
        Assert.Equal("responsive", result.GetValue<string>());
    }
}
