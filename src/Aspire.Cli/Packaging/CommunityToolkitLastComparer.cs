// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Packaging;

/// <summary>
/// Orders integration names case-insensitively within groups, placing CommunityToolkit names last in ascending order.
/// Descending order reverses both the group priority and the alphabetical order.
/// </summary>
internal sealed class CommunityToolkitLastComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var prefix = "communitytoolkit-";
        var xStarts = x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var yStarts = y.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        return (xStarts, yStarts) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => string.Compare(x, y, StringComparison.OrdinalIgnoreCase)
        };
    }
}
