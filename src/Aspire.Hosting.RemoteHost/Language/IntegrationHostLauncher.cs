// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Owns integration-host startup, recovery, diagnostics, and shutdown for the AppHost server.
/// </summary>
internal sealed class IntegrationHostLauncher : IHostedService, IAsyncDisposable
{
    internal const string RegistrationIdVariable = "ASPIRE_INTEGRATION_HOST_REGISTRATION_ID";
    private static readonly TimeSpan s_perHostRegistrationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_perHostDiscoveryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_stableHostPeriod = TimeSpan.FromMinutes(1);
    private const int MaxRestartAttempts = 3;
    private readonly LanguageSupportResolver _languageResolver;
    private readonly ExternalCapabilityRegistry _externalCapabilityRegistry;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IntegrationHostLauncher> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _supervisors = [];
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _startupFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _startTask;
    private Task? _stopTask;
    private Exception? _failure;

    public IntegrationHostLauncher(
        LanguageSupportResolver languageResolver,
        ExternalCapabilityRegistry externalCapabilityRegistry,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<IntegrationHostLauncher> logger)
    {
        _languageResolver = languageResolver;
        _externalCapabilityRegistry = externalCapabilityRegistry;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task ReadyAsync(CancellationToken cancellationToken = default)
        => _readyTcs.Task.WaitAsync(cancellationToken);

    internal void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new InvalidOperationException("Integration host recovery failed; the AppHost session was stopped.", failure);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var initializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            initializationCancellation.Token.ThrowIfCancellationRequested();
            if (_configuration.GetSection("IntegrationHosts").GetChildren().Any()
                && !_configuration.GetValue<bool>(KnownConfigNames.IntegrationHostsEnabled))
            {
                throw new InvalidOperationException(
                    $"Integration hosts require {KnownConfigNames.IntegrationHostsEnabled}=true.");
            }

            if (_configuration.GetValue<bool>(KnownConfigNames.IntegrationHostBootstrap))
            {
                _logger.LogDebug("Skipping integration hosts while bootstrapping the managed SDK.");
                _readyTcs.TrySetResult(true);
                return;
            }

            var descriptors = ReadDescriptorsFromConfiguration();
            foreach (var descriptor in descriptors)
            {
                _supervisors.Add(SuperviseHostAsync(descriptor, _shutdown.Token));
            }

            var initialization = InitializeHostsAsync(
                descriptors.Count, s_perHostRegistrationTimeout, s_perHostDiscoveryTimeout, initializationCancellation.Token);
            if (await Task.WhenAny(initialization, _startupFailure.Task).ConfigureAwait(false) == _startupFailure.Task)
            {
                initializationCancellation.Cancel();
                await initialization.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw await _startupFailure.Task.ConfigureAwait(false);
            }

            await initialization.ConfigureAwait(false);
            if (_startupFailure.Task.IsCompleted)
            {
                throw await _startupFailure.Task.ConfigureAwait(false);
            }

            _readyTcs.TrySetResult(true);
        }
        catch (OperationCanceledException) when (initializationCancellation.IsCancellationRequested)
        {
            _readyTcs.TrySetCanceled(initializationCancellation.Token);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize integration hosts at server startup.");
            _readyTcs.TrySetException(ex);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task InitializeHostsAsync(int expectedCount, TimeSpan registrationTimeout, TimeSpan discoveryTimeout, CancellationToken cancellationToken)
    {
        var registered = await _externalCapabilityRegistry.WaitForHostsAsync(
            expectedCount, registrationTimeout, cancellationToken).ConfigureAwait(false);
        if (registered != expectedCount)
        {
            throw new TimeoutException(
                $"Only {registered} of {expectedCount} integration hosts registered within the per-host timeout ({registrationTimeout}). " +
                "SDK generation was stopped because required integrations are unavailable. Check the integration host startup logs.");
        }

        await _externalCapabilityRegistry.InitializeAllHostsAsync(expectedCount, discoveryTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SuperviseHostAsync(IntegrationHostDescriptor descriptor, CancellationToken cancellationToken)
    {
        JsonRpc? previousConnection = null;
        var restartAttempts = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var registrationId = Guid.NewGuid().ToString("N");
                var registration = _externalCapabilityRegistry.ExpectHostRegistration(registrationId);
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                JsonRpc? connection = null;
                ProcessScope? process = null;
                Exception? failure = null;
                long? healthySince = null;
                var wasStable = false;
                var hasCallbacks = false;
                try
                {
                    process = await LaunchAsync(descriptor, registrationId, attemptCancellation.Token).ConfigureAwait(false);
                    var registered = registration.WaitAsync(s_perHostRegistrationTimeout, attemptCancellation.Token);
                    if (await Task.WhenAny(registered, process.Exit).ConfigureAwait(false) == process.Exit)
                    {
                        throw CreateExitException(descriptor, process, await process.Exit.ConfigureAwait(false));
                    }
                    connection = await registered.ConfigureAwait(false);

                    if (previousConnection is null)
                    {
                        // Remember the connection as soon as it registers. Readiness and
                        // disconnect callbacks can race; recovery still needs its published
                        // registrations even if this supervisor resumes after the guest.
                        previousConnection = connection;
                        var ready = _readyTcs.Task.WaitAsync(cancellationToken);
                        if (await Task.WhenAny(ready, process.Exit, connection.Completion).ConfigureAwait(false) != ready &&
                            !_readyTcs.Task.IsCompletedSuccessfully)
                        {
                            throw new InvalidOperationException(
                                $"Integration host '{descriptor.PackageName}' disconnected during startup. Check its stdout/stderr.");
                        }
                        await ready.ConfigureAwait(false);
                    }
                    else
                    {
                        await _externalCapabilityRegistry.ReplaceHostAsync(
                            previousConnection, connection, s_perHostDiscoveryTimeout, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation(
                            "Integration host '{Name}' recovered after restart attempt {Attempt}; capabilities rediscovered.",
                            descriptor.PackageName, restartAttempts);
                    }
                    previousConnection = connection;
                    healthySince = Stopwatch.GetTimestamp();

                    await Task.WhenAny(process.Exit, connection.Completion).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (process.Exit.IsCompleted)
                    {
                        throw CreateExitException(descriptor, process, await process.Exit.ConfigureAwait(false));
                    }

                    throw new InvalidOperationException(
                        $"Integration host '{descriptor.PackageName}' disconnected from the AppHost server.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    if (!_readyTcs.Task.IsCompletedSuccessfully)
                    {
                        _startupFailure.TrySetResult(ex);
                    }
                    wasStable = healthySince is { } timestamp && Stopwatch.GetElapsedTime(timestamp) >= s_stableHostPeriod;
                }
                finally
                {
                    attemptCancellation.Cancel();
                    _externalCapabilityRegistry.ForgetHostRegistration(registrationId);
                    if (connection is null && registration.IsCompletedSuccessfully)
                    {
                        connection = registration.Result;
                    }
                    if (connection is not null)
                    {
                        hasCallbacks = _externalCapabilityRegistry.MarkHostUnavailable(connection);
                    }
                    if (process is not null)
                    {
                        await process.DisposeAsync(failure).ConfigureAwait(false);
                    }
                }

                if (!_readyTcs.Task.IsCompletedSuccessfully)
                {
                    _startupFailure.TrySetResult(failure!);
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (hasCallbacks)
                {
                    var callbackFailure = new InvalidOperationException(
                        $"Integration host '{descriptor.PackageName}' owns callbacks that cannot be restored by restarting the host. " +
                        "Restart the AppHost session to rebuild the resource model.", failure);
                    _logger.LogError(callbackFailure,
                        "Integration host '{Name}' owns callbacks that cannot be restored by restarting the host. " +
                        "Stopping the AppHost session. Restart it to rebuild the resource model.",
                        descriptor.PackageName);
                    Interlocked.CompareExchange(ref _failure, callbackFailure, null);
                    _lifetime.StopApplication();
                    return;
                }

                // Count fast crash loops against the same budget. A single successful
                // registration is not evidence that the replacement is healthy.
                if (wasStable)
                {
                    restartAttempts = 0;
                }
                if (restartAttempts == MaxRestartAttempts)
                {
                    _logger.LogError(failure,
                        "Integration host '{Name}' failed after {Attempts} restart attempts. Stopping the AppHost session.",
                        descriptor.PackageName, restartAttempts);
                    Interlocked.CompareExchange(ref _failure, failure, null);
                    _lifetime.StopApplication();
                    return;
                }

                restartAttempts++;
                var delay = TimeSpan.FromSeconds(1 << (restartAttempts - 1));
                _logger.LogWarning(failure,
                    "Integration host '{Name}' is unavailable. Restart attempt {Attempt}/{Maximum} in {Delay}.",
                    descriptor.PackageName, restartAttempts, MaxRestartAttempts, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Do not start a replacement if cleanup could not establish that the old
            // process scope stopped. That would leave multiple owners mutating the model.
            _logger.LogError(ex, "Integration host supervision failed for '{Name}'.", descriptor.PackageName);
            if (!_readyTcs.Task.IsCompletedSuccessfully)
            {
                _startupFailure.TrySetResult(ex);
            }
            else
            {
                Interlocked.CompareExchange(ref _failure, ex, null);
                _lifetime.StopApplication();
            }
        }
    }

    private static InvalidOperationException CreateExitException(
        IntegrationHostDescriptor descriptor, ProcessScope process, int exitCode)
        => new(
            $"Integration host supervisor for '{descriptor.PackageName}' (PID {process.ProcessId}, entry '{descriptor.HostEntryPoint}') " +
            $"exited with code {exitCode}. Check IntegrationHost[{descriptor.PackageName}] diagnostics for the runtime's original exit code and stderr.");

    private List<IntegrationHostDescriptor> ReadDescriptorsFromConfiguration()
    {
        var result = new List<IntegrationHostDescriptor>();
        foreach (var entry in _configuration.GetSection("IntegrationHosts").GetChildren())
        {
            var language = entry["Language"];
            var packageName = entry["PackageName"];
            var hostEntryPoint = entry["HostEntryPoint"];
            if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(hostEntryPoint))
            {
                throw new InvalidOperationException(
                    $"Malformed IntegrationHosts entry: Language='{language}', PackageName='{packageName}', HostEntryPoint='{hostEntryPoint}'. " +
                    "All three fields are required.");
            }
            if (!File.Exists(hostEntryPoint))
            {
                throw new FileNotFoundException(
                    $"Integration host entry point for package '{packageName}' [{language}] does not exist. Verify the integration path.",
                    hostEntryPoint);
            }
            result.Add(new IntegrationHostDescriptor
            {
                Language = language,
                PackageName = packageName,
                HostEntryPoint = hostEntryPoint
            });
        }

        return result;
    }

    private async Task<ProcessScope> LaunchAsync(IntegrationHostDescriptor descriptor, string registrationId, CancellationToken cancellationToken)
    {
        var socketPath = _configuration["REMOTE_APP_HOST_SOCKET_PATH"];
        if (string.IsNullOrEmpty(socketPath))
        {
            throw new InvalidOperationException(
                "REMOTE_APP_HOST_SOCKET_PATH is not set on the server; cannot spawn integration hosts.");
        }

        var languageSupport = _languageResolver.GetLanguageSupport(descriptor.Language);
        var hostSpec = languageSupport is null ? null : LanguageService.GetIntegrationHostSpec(languageSupport);
        if (hostSpec is null)
        {
            throw new InvalidOperationException(
                $"Language '{descriptor.Language}' does not provide an integration host for '{descriptor.PackageName}'.");
        }

        // Dependency restore belongs to the CLI's restore phase, not to runtime recovery.
        if (!CommandPathResolver.TryResolveCommand(hostSpec.Execute.Command, out var command, out var error))
        {
            throw new InvalidOperationException($"Cannot launch integration host '{descriptor.PackageName}' [{descriptor.Language}]: {error}");
        }
        var startInfo = CreateProcessStartInfo(command!, hostSpec.Execute.Args, descriptor.HostEntryPoint, OperatingSystem.IsWindows());
        startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath;
        startInfo.Environment[RegistrationIdVariable] = registrationId;
        var token = _configuration[KnownConfigNames.RemoteAppHostToken];
        if (!string.IsNullOrEmpty(token))
        {
            startInfo.Environment[KnownConfigNames.RemoteAppHostToken] = token;
        }

        _logger.LogInformation(
            "Launching integration host '{Name}' [{Language}]: {Command} (entry: {EntryPoint}, cwd: {Directory}).",
            descriptor.PackageName, descriptor.Language, command, descriptor.HostEntryPoint, startInfo.WorkingDirectory);
        var execution = new ChildProcess(
            ProcessSupervisor.CreateStartInfo(startInfo), _logger, new ChildProcessOptions
            {
                StandardOutputCallback = line => _logger.LogInformation("IntegrationHost[{Name}]: {Line}", descriptor.PackageName, line),
                StandardErrorCallback = line => _logger.LogWarning("IntegrationHost[{Name}]: {Line}", descriptor.PackageName, line)
            }, OperatingSystem.IsWindows());
        try
        {
            await execution.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var process = new ProcessScope(execution, _logger, descriptor.PackageName);
        _logger.LogInformation("Started integration host supervisor '{Name}' (PID {Pid}).", descriptor.PackageName, process.ProcessId);

        return process;
    }

    internal static ProcessStartInfo CreateProcessStartInfo(string command, IReadOnlyList<string> argumentTemplates, string entryPoint, bool isWindows)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(entryPoint)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ProcessStartInfoHelper.SetCommand(
            startInfo, command,
            argumentTemplates.Select(argument => argument.Replace("{entryPoint}", entryPoint)),
            isWindows);

        return startInfo;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            return (_stopTask ??= StopCoreAsync()).WaitAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync()
    {
        _shutdown.Cancel();
        _readyTcs.TrySetCanceled(_shutdown.Token);
        _externalCapabilityRegistry.Stop();
        await Task.WhenAll(_supervisors).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

/// <summary>
/// Identifies an integration host configured by the AppHost server.
/// </summary>
internal sealed class IntegrationHostDescriptor
{
    public required string Language { get; init; }
    public required string PackageName { get; init; }
    public required string HostEntryPoint { get; init; }
}
