// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

internal sealed class ContainerImageSourceAnnotation(ContainerReference source) : IResourceAnnotation
{
    internal ContainerReference Source { get; } = source;

    internal string Image => $"{Source.Registry}/{Source.Image}" +
        (Source.Tag is not null ? $":{Source.Tag}" : "") +
        (Source.Digest is not null ? $"@{Source.Digest}" : "");
}
