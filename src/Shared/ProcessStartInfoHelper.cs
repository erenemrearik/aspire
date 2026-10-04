// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class ProcessStartInfoHelper
{
    private const string CommandEnvironmentVariable = "ASPIRE_COMMAND_SHIM_PATH";
    private const string ArgumentEnvironmentVariablePrefix = "ASPIRE_COMMAND_SHIM_ARGUMENT_";

    /// <summary>
    /// Configures an executable and its arguments, including Windows batch shims.
    /// </summary>
    public static void SetCommand(ProcessStartInfo startInfo, string command, IEnumerable<string> args, bool isWindows)
    {
        if (isWindows && (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Environment[CommandEnvironmentVariable] = command;
            var commandLine = new StringBuilder($"\"%{CommandEnvironmentVariable}%\"");
            var index = 0;
            foreach (var arg in args)
            {
                var variable = ArgumentEnvironmentVariablePrefix + index.ToString(CultureInfo.InvariantCulture);
                startInfo.Environment[variable] = arg;
                commandLine.Append(" \"%").Append(variable).Append("%\"");
                index++;
            }

            // Expand child-only variables once so paths like C:\apps\%TEMP%\host.mts
            // retain literal percent-delimited text. /V:OFF also preserves literal '!'.
            // /S /C strips the outer quotes, leaving: "%ASPIRE_COMMAND_SHIM_PATH%" "%ASPIRE_COMMAND_SHIM_ARGUMENT_0%"
            // https://learn.microsoft.com/windows-server/administration/windows-commands/cmd
            startInfo.Arguments = $"/D /V:OFF /S /C \"{commandLine}\"";
        }
        else
        {
            startInfo.FileName = command;
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }
    }
}
