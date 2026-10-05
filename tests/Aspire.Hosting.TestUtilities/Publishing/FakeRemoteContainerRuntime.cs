// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using System.Collections.Concurrent;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Tests.Publishing;

public sealed class FakeRemoteContainerRuntime : FakeContainerRuntime, IContainerRuntime
{
    public ConcurrentBag<string> RemoteResolveCalls { get; } = [];
    public ConcurrentBag<(string Source, string Destination)> RemoteCopyCalls { get; } = [];
    public required Func<string, CancellationToken, Task<string>> ResolveRemoteImageAsyncCallback { get; set; }
    public required Func<string, string, CancellationToken, Task<string>> CopyRemoteImageAsyncCallback { get; set; }

    public Task<string> ResolveRemoteImageAsync(string imageName, CancellationToken cancellationToken)
    {
        RemoteResolveCalls.Add(imageName);

        return ResolveRemoteImageAsyncCallback(imageName, cancellationToken);
    }

    public Task<string> CopyRemoteImageAsync(string sourceImageName, string destinationImageName, CancellationToken cancellationToken)
    {
        RemoteCopyCalls.Add((sourceImageName, destinationImageName));

        return CopyRemoteImageAsyncCallback(sourceImageName, destinationImageName, cancellationToken);
    }
}
