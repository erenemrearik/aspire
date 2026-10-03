// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.SqlServer;

namespace Aspire.Hosting;

internal static class SqlServerContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>mssql/server</remarks>
    public const string Image = "mssql/server";

    /// <remarks>2022-latest</remarks>
    public const string Tag = "2022-latest";

    // Keep the default REPL client aligned with the tools installed in the default image.
    // https://learn.microsoft.com/sql/linux/quickstart-install-connect-docker
    public const string ReplCommand = SqlServerReplCommand.Version18;
}
