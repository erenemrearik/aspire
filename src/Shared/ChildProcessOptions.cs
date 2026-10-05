// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Output and cancellation behavior shared by redirected process executions.
/// </summary>
internal sealed class ChildProcessOptions
{
    public Action<string>? StandardOutputCallback { get; init; }
    public Action<string>? StandardErrorCallback { get; init; }
    public bool Detached { get; init; }
    public bool KillEntireProcessTreeOnCancel { get; init; } = true;
}
