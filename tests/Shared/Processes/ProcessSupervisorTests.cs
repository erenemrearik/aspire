// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Shared.Tests;

[RequiresTools(["dotnet"])]
[Collection(ProcessTestCollection.Name)]
public class ProcessSupervisorTests(ProcessTestFixture fixture)
{
    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task CommandExit_PreservesCompletionCodeAndReapsWorkers(int exitCode)
    {
        var directory = fixture.CreateDirectory();
        using var readiness = new ProcessTestReadiness();
        var completionPath = Path.Combine(directory.FullName, "exit-code");
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree-exit", readiness.Name, exitCode.ToString(CultureInfo.InvariantCulture)),
            completionPath), new ChildProcessOptions());
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test completion");
        var identities = await readiness.ReadTreeAsync();

        await scope.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
        await scope.DisposeAsync();

        Assert.Equal(exitCode.ToString(CultureInfo.InvariantCulture), await File.ReadAllTextAsync(completionPath));
        await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Fact]
    public async Task Cancellation_ReapsGuardianRuntimeAndWorker()
    {
        using var readiness = new ProcessTestReadiness();
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree", readiness.Name)), new ChildProcessOptions());
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test cancellation");
        var guardianIdentity = ProcessTestIdentity.Capture(scope.ProcessId);
        var identities = await readiness.ReadTreeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.WaitForExitAsync(cancellation.Token));
        await scope.DisposeAsync();

        await Task.WhenAll(identities.Append(guardianIdentity).Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Fact]
    public async Task GuardianCrash_ReapsRuntimeAndWorker()
    {
        using var readiness = new ProcessTestReadiness();
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree", readiness.Name)), new ChildProcessOptions());
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test guardian crash");
        var identities = await readiness.ReadTreeAsync();
        using var observedGuardian = Process.GetProcessById(scope.ProcessId);

        observedGuardian.Kill(entireProcessTree: false);
        await scope.DisposeAsync();

        await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Fact]
    public async Task OwnerCrash_GuardianIndependentlyReapsRuntimeAndWorker()
    {
        using var readiness = new ProcessTestReadiness();
        await using var owner = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("owner", readiness.Name), new ChildProcessOptions());
        await owner.StartAsync(TestContext.Current.CancellationToken);
        var guardianIdentity = await readiness.ReadGuardianAsync();
        ProcessTestIdentity[] identities = [];
        try
        {
            identities = await readiness.ReadTreeAsync();
            using var observedOwner = Process.GetProcessById(owner.ProcessId);

            observedOwner.Kill(entireProcessTree: false);
            await owner.WaitForRootExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));

            await Task.WhenAll(identities.Append(guardianIdentity).Select(ProcessTestFixture.AssertExitedAsync));
        }
        finally
        {
            ProcessTestFixture.KillIfRunning(guardianIdentity);
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }
}
