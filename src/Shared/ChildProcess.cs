// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
#if !NET11_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Threading.Channels;
#endif
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Shared process launch, identity, output forwarding, and handle ownership. Wraps a <see cref="Process"/> for
/// isolated-console, kill-on-parent-exit, detached, and ordinary redirected subprocesses. The child is
/// spawned lazily on <see cref="IChildProcess.StartAsync"/> so callers that build an execution but never start it (e.g.
/// the extension-host launch path, which reads <see cref="Arguments"/> /
/// <see cref="EnvironmentVariables"/> and returns before starting) don't orphan a process.
/// </summary>
internal class ChildProcess : IChildProcess
{
    private static readonly TimeSpan s_drainIdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_drainPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ProcessStartInfo _startInfo;
    protected readonly ILogger _logger;
    private readonly ChildProcessOptions _options;
    private readonly bool _isWindows;
    private readonly Lock _lifecycleLock = new();
    private Process? _process;
    private int _processId;
    private DateTimeOffset? _startTime;
    private Task _outputDrained = Task.CompletedTask;
    private bool _disposed;
    private long _lastActivityTimestamp;

    internal ChildProcess(
        ProcessStartInfo startInfo,
        ILogger logger,
        ChildProcessOptions options,
        bool isWindows)
    {
        _startInfo = startInfo;
        _logger = logger;
        _options = options;
        _isWindows = isWindows;
        _lastActivityTimestamp = options.TimeProvider.GetTimestamp();
        EnvironmentVariables = new ReadOnlyDictionary<string, string?>(startInfo.Environment);
    }

    /// <inheritdoc />
    public string FileName => _startInfo.FileName;

    /// <inheritdoc />
    public IReadOnlyList<string> Arguments => _startInfo.ArgumentList;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

    /// <inheritdoc />
    public int ProcessId
    {
        get
        {
            // Captured at spawn because Process.Id throws once the handle is disposed.
            _ = Process;
            return _processId;
        }
    }

    /// <inheritdoc />
    public bool HasExited => Process.HasExited;

    /// <inheritdoc />
    public int ExitCode => Process.ExitCode;

    /// <inheritdoc />
    public DateTimeOffset? StartTime
    {
        get
        {
            _ = Process;
            return _startTime;
        }
    }

    protected Process Process =>
        Volatile.Read(ref _process)
        ?? throw new InvalidOperationException($"{nameof(ChildProcess)} has not been started. Call {nameof(StartAsync)} first.");

    /// <inheritdoc />
    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null)
            {
                throw new InvalidOperationException($"{nameof(ChildProcess)} has already been started.");
            }

            // Children never consume input from their owner. A null stdin makes tools such as
            // package-manager lifecycle scripts observe EOF instead of inheriting the TTY and blocking
            // indefinitely (https://github.com/microsoft/aspire/issues/16791). A detached child
            // outlives the CLI, so nothing would be left to drain redirected output either.
#if NET11_0_OR_GREATER
            using var nullHandle = File.OpenNullHandle();
            _startInfo.StandardInputHandle = nullHandle;
            if (_options.Detached)
            {
                _startInfo.StandardOutputHandle = nullHandle;
                _startInfo.StandardErrorHandle = nullHandle;
            }
#else
            // AppHost servers target net10, which does not expose standard-handle assignment.
            // Closing the redirected writer gives package-manager lifecycle scripts the same EOF.
            if (_options.Detached)
            {
                throw new NotSupportedException("Detached process execution requires .NET 11.");
            }
            _startInfo.RedirectStandardInput = true;
#endif

            // Process.Start() only returns null for UseShellExecute, which is never used here.
            process = Process.Start(_startInfo)
                ?? throw new InvalidOperationException($"Failed to start child process: {_startInfo.FileName}");
            _processId = process.Id;
            _startTime = GetStartTime(process);
            Volatile.Write(ref _process, process);
#if !NET11_0_OR_GREATER
            // Publish ownership before closing stdin: a pipe-close failure must still leave
            // disposal able to terminate the child and release its handles.
            process.StandardInput.Close();
#endif

            // Publish the process before reading output so callbacks can read ProcessId.
            if (!_options.Detached && (_startInfo.RedirectStandardOutput || _startInfo.RedirectStandardError))
            {
                _outputDrained = Task.Run(() => ReadOutputAsync(process), CancellationToken.None);
            }
        }

        _logger.LogDebug("{FileName}({ProcessId}) started in {WorkingDirectory}", FileName, _processId, _startInfo.WorkingDirectory);
        return Task.FromResult(true);
    }

    private static DateTimeOffset? GetStartTime(Process process)
    {
        try
        {
            return ProcessStartTimeHelper.TryGetProcessStartTime(process.Id) ?? new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The child already exited and was reaped.
            return null;
        }
    }

    /// <summary>
    /// Forwards each output line to the callbacks until both pipes reach EOF.
    /// </summary>
    /// <remarks>
    /// Reading the pipes directly, unlike <see cref="Process.BeginOutputReadLine"/>, means
    /// <see cref="Process.WaitForExitAsync"/> does not
    /// also wait for EOF, which a grandchild holding the inherited pipe (e.g. a build server) can
    /// delay indefinitely. <see cref="DrainOutputAsync"/> bounds the wait for EOF instead.
    /// </remarks>
    private async Task ReadOutputAsync(Process process)
    {
        Exception? firstCallbackException = null;
        try
        {
#if NET11_0_OR_GREATER
            var lines = process.ReadAllLinesAsync(CancellationToken.None);
#else
            var lines = ReadAllLinesAsync(process);
#endif
            await foreach (var line in lines.ConfigureAwait(false))
            {
                try
                {
                    if (line.StandardError)
                    {
                        OnErrorLine(line.Content);
                    }
                    else
                    {
                        OnOutputLine(line.Content);
                    }
                }
                catch (Exception ex)
                {
                    // Keep draining so a throwing callback cannot back-pressure the child through a
                    // full pipe. The first failure is surfaced after EOF.
                    firstCallbackException ??= ex;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // DisposeAsync released the pipes while a read was pending (no token is passed, so a
            // cancellation can only come from that). Treat as EOF.
            return;
        }

        if (firstCallbackException is not null)
        {
            ExceptionDispatchInfo.Throw(firstCallbackException);
        }
    }

    /// <inheritdoc />
    public async Task<int> WaitForRootExitAsync(CancellationToken cancellationToken)
    {
        var process = Process;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = Process;
        _logger.LogDebug("{FileName}({ProcessId}) waiting for exit", FileName, _processId);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{FileName}({ProcessId}) wait was canceled, stopping it", FileName, _processId);

            await ShutdownOnCancelAsync(process).ConfigureAwait(false);

            // The child has now been signalled/killed by the coordinator. Drain trailing stdout/stderr
            // before propagating the cancellation so callers that observe output — or that swallow the
            // OCE and read ExitCode (e.g. the guest launcher distinguishing user-cancel from internal
            // teardown) — still get the full tail. Use a detached token + reset idle window so the drain
            // gets its whole budget even though the caller's token is already cancelled.
            RecordActivity();
            await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        _logger.LogDebug("{FileName}({ProcessId}) exited with code: {ExitCode}", FileName, _processId, process.ExitCode);

        // Reset the idle window at exit so the drain budget is measured from "process gone", not
        // from the last line read. A consumer can block in a callback right up to exit and still
        // get the full tail — see
        // ChildProcessTests.WaitForExitAsync_DrainsBufferedTailAfterLongIdlePeriod.
        RecordActivity();
        await DrainOutputAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode;
    }

    protected virtual Task ShutdownOnCancelAsync(Process process)
    {
        ForceKillChild(process);

        return Task.CompletedTask;
    }

    private void ForceKillChild(Process process)
    {
        // Mirrors the force path: resolve "already gone?", issue a best-effort courtesy SIGTERM on Unix
        // (so a SIGTERM-aware child can flush), then hard-kill. On Windows there is no graceful signal
        // to send here — Ctrl+C delivery only happens on the signaler-backed graceful ladder — so we
        // skip straight to the kill.
        var entireProcessTree = _options.KillEntireProcessTreeOnCancel;
        try
        {
            if (process.HasExited)
            {
                _logger.LogDebug("{FileName} process {ProcessId} already exited.", FileName, process.Id);
                return;
            }

            if (!_isWindows)
            {
                ProcessSignaler.RequestGracefulShutdown(process.Id, expectedStartTime: null, _logger);

                if (process.HasExited)
                {
                    return;
                }
            }

            _logger.LogDebug(
                "Sending kill to {FileName} process {ProcessId} (entireProcessTree={EntireProcessTree}).",
                FileName,
                process.Id,
                entireProcessTree);
            process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(
                ex,
                "{FileName} process exited before termination could complete (entireProcessTree={EntireProcessTree}).",
                FileName,
                entireProcessTree);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to terminate {FileName} process (entireProcessTree={EntireProcessTree}).",
                FileName,
                entireProcessTree);
        }
    }

    protected static int GetSafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <inheritdoc />
    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process = _process;
        }

        if (process is null)
        {
            return;
        }

        // DotNetCliRunner does not dispose the execution (StartBackchannelAsync runs fire-and-forget
        // and reads HasExited/ExitCode after the await — see DotNetCliRunner.cs), so this path is
        // reached only by explicit `await using` consumers (the session, guest launcher) and tests.
        //
        // Terminate the child if it is still running. On the normal teardown paths the caller drives
        // WaitForExitAsync(token) first, so the shutdown ladder has already exited or killed the
        // process by the time we get here and this is a no-op. It matters for the path where an
        // execution was started but never driven (e.g. a fault between Start and the caller wiring up
        // its wait loop): Process.Dispose releases handles but does NOT terminate the process — so
        // without this kill the child would be orphaned. Owning "kill if still alive on dispose" here
        // keeps that responsibility off every consumer.
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate {FileName}({ProcessId}) during disposal.", FileName, _processId);
        }

        try
        {
            await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnOutputLine(string line)
    {
        // RecordActivity brackets the callback so a slow consumer
        // keeps the drain budget alive both while we hand it the line and while it processes it.
        RecordActivity();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{FileName}({ProcessId}) stdout: {Line}", FileName, _processId, line);
        }
        _options.StandardOutputCallback?.Invoke(line);
        RecordActivity();
    }

    private void OnErrorLine(string line)
    {
        RecordActivity();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{FileName}({ProcessId}) stderr: {Line}", FileName, _processId, line);
        }
        _options.StandardErrorCallback?.Invoke(line);
        RecordActivity();
    }

    private async Task DrainOutputAsync(CancellationToken cancellationToken)
    {
        var drained = _outputDrained;

        while (true)
        {
            if (drained.IsCompleted)
            {
                try
                {
                    await drained.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A throwing callback faults the reader task and surfaces here. The reader still
                    // drained to EOF so output isn't lost; log and move on — the exit code is valid.
                    _logger.LogWarning(ex, "{FileName}({ProcessId}) stdout/stderr callback faulted while draining after exit", FileName, _processId);
                }

                _logger.LogDebug("{FileName}({ProcessId}) output drained", FileName, _processId);
                return;
            }

            // Idle-based budget: a slow-but-progressing consumer keeps resetting the timer via
            // RecordActivity, so only a genuinely stalled reader (no output for the whole window)
            // gives up. The reader keeps running in the background until DisposeAsync releases the
            // pipes — this method never closes streams while callbacks may still be processing data.
            if (_options.TimeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp)) >= s_drainIdleTimeout)
            {
                _logger.LogWarning("{FileName}({ProcessId}) stdout/stderr did not drain within idle timeout after exit", FileName, _processId);
                return;
            }

            try
            {
                // Completion wakes the waiter immediately, including when a test clock is frozen.
                await drained.WaitAsync(s_drainPollInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Recheck the idle budget; a completed reader's failure is observed above.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (drained.IsCompleted)
            {
                // Observe and log the reader failure on the next iteration.
            }
        }
    }

    private void RecordActivity() => Interlocked.Exchange(ref _lastActivityTimestamp, _options.TimeProvider.GetTimestamp());

#if !NET11_0_OR_GREATER
    private static async IAsyncEnumerable<OutputLine> ReadAllLinesAsync(Process process, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // net10 has no Process.ReadAllLinesAsync. Merge the pipes before invoking callbacks,
        // preserving the CLI runner's serialized callback contract and avoiding pipe-buffer deadlock.
        var lines = Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false
        });
        using var readersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readers = CompleteAsync();
        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
            await readers.ConfigureAwait(false);
        }
        finally
        {
            readersCancellation.Cancel();
        }

        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(
                    process.StartInfo.RedirectStandardOutput ? ReadAsync(process.StandardOutput, false) : Task.CompletedTask,
                    process.StartInfo.RedirectStandardError ? ReadAsync(process.StandardError, true) : Task.CompletedTask).ConfigureAwait(false);
                lines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Completion propagates read failures to the single consumer; this task never faults
                // unobserved when disposal interrupts an enumerator blocked on inherited pipes.
                lines.Writer.TryComplete(ex);
            }
        }

        async Task ReadAsync(StreamReader reader, bool standardError)
        {
            while (await reader.ReadLineAsync(readersCancellation.Token).ConfigureAwait(false) is { } line)
            {
                await lines.Writer.WriteAsync(new OutputLine(line, standardError), readersCancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private readonly record struct OutputLine(string Content, bool StandardError);
#endif
}
