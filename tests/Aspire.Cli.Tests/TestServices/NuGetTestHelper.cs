// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

internal static class NuGetTestHelper
{
    public static BundleNuGetService CreateService()
        => new(NullLogger<BundleNuGetService>.Instance, CreateClient());

    public static NuGetClient CreateClient()
        => new(new TestFeatures(), new TestEnvironment(), NullLogger<NuGetClient>.Instance);
}
