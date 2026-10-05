// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting.ApplicationModel;

internal sealed class DestinationImageReferenceAnnotation(DestinationImageResource image) : IResourceAnnotation
{
    internal DestinationImageResource Image { get; } = image;
}
