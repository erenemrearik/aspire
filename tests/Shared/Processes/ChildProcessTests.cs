// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.TestUtilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aspire.Shared.Tests;

[RequiresTools(["dotnet"])]
[Collection(ProcessTestCollection.Name)]
public class ChildProcessTests(ProcessTestFixture fixture)
{
    [Fact]
    public async Task StartAsync_CancellationAndDisposalDoNotLaunch()
    {
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "0"), new ChildProcessOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.StartAsync(cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => process.ProcessId);
        await process.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => process.StartAsync(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => process.ProcessId);
    }

    [Fact]
    public async Task WaitForExitAsync_ThrowingCallbackStillDrainsBothStreams()
    {
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "23"),
            new ChildProcessOptions
            {
                TimeProvider = new FakeTimeProvider(),
                StandardOutputCallback = line =>
                {
                    stdout.Enqueue(line);
                    if (line == "stdout:0")
                    {
                        throw new InvalidOperationException("Test callback failure.");
                    }
                },
                StandardErrorCallback = stderr.Enqueue
            });
        await process.StartAsync(TestContext.Current.CancellationToken);

        var exitCode = await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(23, exitCode);
        Assert.Equal([$"runtime:{ProcessTestFixture.RuntimeMajor}", "stdin:0", .. Enumerable.Range(0, 256).Select(i => $"stdout:{i}")], stdout.ToArray());
        Assert.Equal(Enumerable.Range(0, 256).Select(i => $"stderr:{i}"), stderr);
    }

    [Fact]
    public async Task WaitForExitAsync_DrainsBufferedTailAfterLongIdlePeriod()
    {
        var clock = new FakeTimeProvider();
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "0"),
            new ChildProcessOptions
            {
                TimeProvider = clock,
                StandardOutputCallback = line =>
                {
                    if (line.StartsWith("runtime:", StringComparison.Ordinal))
                    {
                        firstLine.TrySetResult();
                        releaseConsumer.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    }
                    stdout.Enqueue(line);
                },
                StandardErrorCallback = stderr.Enqueue
            });
        await process.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await firstLine.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await process.WaitForRootExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            // Exceed the five-second idle window before observing exit. Exit must reset that
            // budget, and completing the reader must wake it without advancing the clock again.
            clock.Advance(TimeSpan.FromSeconds(10));
            var exit = process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.False(exit.IsCompleted);
            releaseConsumer.TrySetResult();
            Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal([$"runtime:{ProcessTestFixture.RuntimeMajor}", "stdin:0", .. Enumerable.Range(0, 256).Select(i => $"stdout:{i}")], stdout.ToArray());
            Assert.Equal(Enumerable.Range(0, 256).Select(i => $"stderr:{i}"), stderr);
        }
        finally
        {
            releaseConsumer.TrySetResult();
        }
    }

    [Fact]
    public async Task StartAsync_RejectsSecondLaunch()
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name), new ChildProcessOptions());
        await process.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => process.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForExitAsync_CancellationTerminatesStartedProcess()
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name), new ChildProcessOptions());
        await process.StartAsync(TestContext.Current.CancellationToken);
        var identity = await readiness.ReadRuntimeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            process.WaitForExitAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));

        await ProcessTestFixture.AssertExitedAsync(identity);
    }

    [Fact]
    public async Task DisposeAsync_TerminatesStartedProcessWithoutAnExitWait()
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name), new ChildProcessOptions());
        await process.StartAsync(TestContext.Current.CancellationToken);
        var identity = await readiness.ReadRuntimeAsync();
        using var observedProcess = Process.GetProcessById(identity.ProcessId);

        await process.DisposeAsync();

        await observedProcess.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(observedProcess.HasExited);
        await process.DisposeAsync();
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task StartAsync_DetachedExecutionUsesNullStandardHandles()
    {
        var startInfo = fixture.CreateStartInfo("output", "0");
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        var forwardedLines = new ConcurrentQueue<string>();
        await using var process = ProcessTestFixture.CreateProcess(startInfo, new ChildProcessOptions
        {
            Detached = true,
            StandardOutputCallback = forwardedLines.Enqueue,
            StandardErrorCallback = forwardedLines.Enqueue
        });

        await process.StartAsync(TestContext.Current.CancellationToken);
        var exitCode = await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Empty(forwardedLines);
    }
#else
    [Fact]
    public async Task StartAsync_DetachedExecutionIsExplicitlyUnsupported()
    {
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "0"),
            new ChildProcessOptions { Detached = true });

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => process.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Detached process execution requires .NET 11.", exception.Message);
        Assert.Throws<InvalidOperationException>(() => process.ProcessId);
    }
#endif
}
