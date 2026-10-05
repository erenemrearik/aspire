// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Processes;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Applies the CLI's central graceful-shutdown policy to shared process execution.
/// </summary>
internal sealed class ProcessExecution : ChildProcess, IProcessExecution
{
    private readonly ProcessInvocationOptions _options;

    internal ProcessExecution(
        ProcessStartInfo startInfo,
        ILogger logger,
        ProcessInvocationOptions options,
        IEnvironment hostEnvironment)
        : base(startInfo, logger, new ChildProcessOptions
        {
            StandardOutputCallback = line => options.StandardOutputCallback?.Invoke(line),
            StandardErrorCallback = line => options.StandardErrorCallback?.Invoke(line),
            KillEntireProcessTreeOnCancel = options.KillEntireProcessTreeOnCancel,
            Detached = options.Detached
        }, hostEnvironment.IsWindows())
    {
        _options = options;
    }

    protected override Task ShutdownOnCancelAsync(Process process)
    {
        var signaler = _options.GracefulShutdownSignaler;
        var gracefulShutdownWindow = _options.ShutdownService;
        if (signaler is not null && gracefulShutdownWindow is { IsEnabled: true })
        {
            // Disposal can initiate teardown before Ctrl+C. Arm the same central clock rather
            // than giving each child a new graceful budget.
            gracefulShutdownWindow.BeginGracefulWindow();

            return ShutdownLadderAsync(process, signaler, gracefulShutdownWindow.GracefulShutdownToken);
        }

        return base.ShutdownOnCancelAsync(process);
    }

    private async Task ShutdownLadderAsync(Process process, IProcessTreeGracefulShutdownSignaler signaler, CancellationToken gracefulToken)
    {
        // Dispatch independently: the signal request itself waits for exit, so awaiting it before
        // the exit wait would consume the entire graceful budget. Dispatch even if it has expired.
        var signalTask = InvokeSignalerAsync(signaler, GetSafePid(process), gracefulToken);
        try
        {
            try
            {
                await process.WaitForExitAsync(gracefulToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (process.HasExited)
            {
                return;
            }

            // Always tree-kill on escalation, including wrappers that swallow Windows console signals.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill {FileName} (pid {Pid}).", FileName, GetSafePid(process));
                return;
            }

            try
            {
                using var killDrain = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(killDrain.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error draining killed {FileName} (pid {Pid}).", FileName, GetSafePid(process));
            }
        }
        finally
        {
            // Observe the yielding signaler on every path so the dispatch cannot be abandoned.
            if (signalTask.IsCompleted)
            {
                await signalTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            else
            {
                using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await signalTask.WaitAsync(drainCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task InvokeSignalerAsync(IProcessTreeGracefulShutdownSignaler signaler, int pid, CancellationToken gracefulToken)
    {
        try
        {
            await Task.Yield();
            await signaler.RequestProcessTreeGracefulShutdownAsync(
                pid,
                startTime: null,
                includeStartTimeForDcp: false,
                gracefulToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gracefulToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to issue graceful shutdown to {FileName} (pid {Pid}); escalating to kill.", FileName, pid);
        }
    }
}
