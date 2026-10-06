// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Verifies termination of a guardian and its contained descendants before releasing its execution.
/// </summary>
internal sealed partial class ProcessScope : IAsyncDisposable
{
    private readonly TimeSpan _exitTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly IChildProcess _execution;
    private readonly ILogger _logger;
    private readonly string _description;
    private readonly int _processId;
    private int _disposed;

    internal ProcessScope(IChildProcess execution, ILogger logger, string description)
        : this(execution, logger, description, TimeSpan.FromSeconds(5), TimeProvider.System)
    {
    }

    internal ProcessScope(IChildProcess execution, ILogger logger, string description, TimeSpan exitTimeout, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exitTimeout, TimeSpan.Zero);
        _exitTimeout = exitTimeout;
        _timeProvider = timeProvider;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(execution.ProcessId);
        if (execution.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("A supervised child process scope cannot own its caller.");
        }
        _execution = execution;
        _processId = execution.ProcessId;
        _logger = logger;
        _description = description;
        Exit = execution.WaitForRootExitAsync(CancellationToken.None);
        _logger.LogDebug("Process scope '{Name}' started with supervisor PID {Pid}.", description, _processId);
    }

    public int ProcessId => _processId;

    public Task<int> Exit { get; }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Exit.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellationFailure) when (cancellationToken.IsCancellationRequested)
        {
            // Install cancellation has no cooperative RPC shutdown phase. Signal the whole scope
            // immediately, then let disposal verify exit and drain output with its own bounded budget.
            _logger.LogDebug("Cancellation requested for process scope '{Name}'; terminating it.", _description);
            try
            {
                RequestScopeTermination();
            }
            catch (Exception terminationFailure)
            {
                _logger.LogError(terminationFailure, "Failed to terminate cancelled process scope '{Name}' (supervisor PID {Pid}).", _description, _processId);
                throw new AggregateException($"Cancellation and termination of process scope '{_description}' both failed.",
                    cancellationFailure, terminationFailure);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            try
            {
                await Exit.WaitAsync(_exitTimeout, _timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Process scope '{Name}' did not stop gracefully within {Timeout}; terminating it.", _description, _exitTimeout);
            }

            RequestScopeTermination();

            await Exit.WaitAsync(_exitTimeout, _timeProvider).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                // The guardian can establish its group between the first group
                // signal and root termination. Signal again after reaping it so
                // descendants spawned in that startup window cannot survive.
                RequestGroupTermination();
                using var groupExitCancellation = new CancellationTokenSource(_exitTimeout, _timeProvider);
                try
                {
                    while (GroupExists(_processId))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, groupExitCancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException ex) when (groupExitCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Process group {_processId} for '{_description}' still exists after cleanup timeout {_exitTimeout}.", ex);
                }
            }
            _logger.LogDebug("Process scope '{Name}' (supervisor PID {Pid}) terminated and cleanup verified.", _description, _processId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up process scope '{Name}' (supervisor PID {Pid}, timeout {Timeout}).",
                _description, _processId, _exitTimeout);
            // An exit-observation failure must not skip termination of the owned group.
            // Keep both causes if the last cleanup attempt fails as well.
            try
            {
                RequestScopeTermination();
            }
            catch (Exception terminationFailure)
            {
                throw new AggregateException($"Could not clean up or terminate process scope '{_description}'.", ex, terminationFailure);
            }
            throw;
        }
        finally
        {
            await _execution.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask DisposeAsync(Exception? operationFailure)
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (operationFailure is not null)
        {
            throw new AggregateException(
                $"Operation '{_description}' failed and its process scope could not be cleaned up.",
                operationFailure, cleanupFailure);
        }
    }

    internal static void KillGroup(int processGroupId)
    {
        // A negative PID targets the entire POSIX process group, including members
        // whose original parent has exited. ESRCH means the scope is already gone.
        // https://pubs.opengroup.org/onlinepubs/9799919799/functions/kill.html
        if (kill(-processGroupId, 9) != 0 && Marshal.GetLastPInvokeError() is not 3)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not terminate process group {processGroupId}.");
        }
    }

    private void RequestGroupTermination()
    {
        try
        {
            KillGroup(_processId);
        }
        catch (Win32Exception ex) when (OperatingSystem.IsMacOS() && ex.NativeErrorCode == 1 && _execution.HasExited)
        {
            // Darwin can report EPERM while an exited guardian's orphaned group is being reaped.
            // This is not evidence of cleanup: the bounded GroupExists loop must still prove ESRCH.
            _logger.LogDebug(ex, "Process group {Pid} cannot be signaled after guardian exit; waiting to verify that it disappears.", _processId);
        }
    }

    private void RequestScopeTermination()
    {
        if (!OperatingSystem.IsWindows())
        {
            RequestGroupTermination();
        }
        // Before Unix setsid succeeds no group exists, but the guardian still needs reaping.
        // It cannot spawn a child before establishing containment.
        if (!_execution.HasExited)
        {
            try
            {
                _execution.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (_execution.HasExited)
            {
            }
        }
    }

    private static bool GroupExists(int processGroupId)
    {
        if (kill(-processGroupId, 0) == 0)
        {
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        // Signal zero still performs permission checks. EPERM establishes that the
        // group exists; on macOS it can be transient while orphaned members are reaped.
        // Keep waiting rather than mistaking lack of permission for completed cleanup.
        if (error == 1)
        {
            return true;
        }
        if (error == 3)
        {
            return false;
        }

        throw new Win32Exception(error, $"Could not verify termination of process group {processGroupId}.");
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
