// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class IntegrationHostLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RuntimeCrash_ReapsWorkersAndRecoversCommands() => RunScenarioAsync("runtime");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task WrapperCrash_ReapsWorkersAndRecoversCommands() => RunScenarioAsync("wrapper");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task GuardianCrash_ReapsBlockedRuntimeAndRecoversCommands() => RunScenarioAsync("supervisor");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task ServerCrash_ReapsSessionAndAllowsExplicitRestart() => RunScenarioAsync("server");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CliCrash_ReapsSessionAndAllowsExplicitRestart() => RunScenarioAsync("cli");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CrashLoop_SurfacesExhaustedRecoveryAndAllowsExplicitRestart() => RunScenarioAsync("crashloop");

    private async Task RunScenarioAsync(string target)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(3));
        IntegrationHostLifetimeTestHelper.WriteFixture(workspace.WorkspaceRoot.FullName, repoRoot);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        // A foreground run launched in the test shell's background retains the real
        // CLI as session owner. Killing an 'aspire start' caller would only kill the
        // already-exited handoff process and would not exercise CLI-owner death.
        await auto.RunCommandAsync(
            "aspire run --non-interactive --log-file \"$PWD/session.log\" > run.log 2>&1 & echo $! > cli.pid",
            counter);
        await auto.ExecuteCommandUntilOutputAsync(
            counter,
            "node process-control.mjs ready",
            "PROBE_READY",
            timeout: TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("aspire resource probe probe > probe-before.log 2>&1", counter);
        await auto.RunCommandAsync("node process-control.mjs probe && node process-control.mjs snapshot", counter);

        if (target is "supervisor" or "server" or "cli")
        {
            await auto.RunCommandAsync("node process-control.mjs block", counter, TimeSpan.FromSeconds(75));
        }
        await auto.RunCommandAsync(
            target == "crashloop" ? "node process-control.mjs crashloop" : $"node process-control.mjs kill {target}",
            counter);
        if (target is "server" or "cli" or "crashloop")
        {
            await auto.RunCommandAsync("node process-control.mjs stopped", counter, TimeSpan.FromSeconds(75));
            if (target is "server" or "crashloop")
            {
                // Owner death stops the session, rather than replaying AppHost model
                // construction. Users must see the failure and can explicitly restart.
                await auto.RunCommandAsync(
                    "wait $(cat cli.pid); status=$?; cat run.log; test \"$status\" -ne 0",
                    counter);
                await auto.RunCommandAsync("grep -iE 'exited|failed|disconnect' run.log", counter);
            }
            if (target is "crashloop")
            {
                await auto.RunCommandAsync("node process-control.mjs exhausted", counter);
            }
            await auto.RunCommandAsync("aspire start --non-interactive > restart.log 2>&1", counter, TimeSpan.FromMinutes(3));
        }
        else
        {
            await auto.RunCommandAsync("node process-control.mjs recovered", counter, TimeSpan.FromSeconds(75));
        }

        await auto.RunCommandAsync("aspire resource probe probe > probe-after.log 2>&1", counter);
        await auto.RunCommandAsync("node process-control.mjs probe && cat probe-after.log", counter);
        await auto.RunCommandAsync("aspire describe probe --format json > recovered-resource.json", counter);
        await auto.RunCommandAsync("node process-control.mjs describe", counter);
        await auto.RunCommandAsync("aspire stop --non-interactive > stop.log 2>&1", counter, TimeSpan.FromMinutes(1));
        await auto.RunCommandAsync("node process-control.mjs clean", counter, TimeSpan.FromSeconds(75));
        var testName = Hex1bTestHelpers.ResolveTestMethodName(nameof(RunScenarioAsync));
        var evidence = IntegrationHostLifetimeTestHelper.CaptureEvidence(workspace.WorkspaceRoot.FullName, testName, target);
        output.WriteLine($"Process identities, diagnostics, and user-visible command results: {evidence}");
    }
}
