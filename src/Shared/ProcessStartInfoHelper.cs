// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
        startInfo.Arguments = "";
#if !NETFRAMEWORK
        startInfo.ArgumentList.Clear();
#endif
        if (isWindows && (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            SetEnvironmentVariable(startInfo, CommandEnvironmentVariable, command);
            var commandLine = new StringBuilder($"\"%{CommandEnvironmentVariable}%\"");
            var index = 0;
            foreach (var arg in args)
            {
                var variable = ArgumentEnvironmentVariablePrefix + index.ToString(CultureInfo.InvariantCulture);
                SetEnvironmentVariable(startInfo, variable, arg);
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
#if NETFRAMEWORK
            startInfo.Arguments = string.Join(" ", args.Select(QuoteWindowsArgument));
#else
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
#endif
        }
    }

    private static void SetEnvironmentVariable(ProcessStartInfo startInfo, string name, string value)
    {
#if NETFRAMEWORK
        startInfo.EnvironmentVariables[name] = value;
#else
        startInfo.Environment[name] = value;
#endif
    }

#if NETFRAMEWORK
    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(static character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', (backslashCount * 2) + 1);
                result.Append('"');
                backslashCount = 0;
                continue;
            }

            result.Append('\\', backslashCount);
            backslashCount = 0;
            result.Append(character);
        }
        result.Append('\\', backslashCount * 2);
        result.Append('"');

        return result.ToString();
    }
#endif
}
