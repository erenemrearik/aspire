// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests;

internal sealed class CodeqlStarterTestEnvironment : IDisposable
{
    public const string PackageVersion = "13.5.0-preview.1.26310.9";

    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;
    private readonly string _runnerPath;

    public string PackageDirectory { get; }
    public string OutputDirectory { get; }
    public string TracePath { get; }
    public string GradleTracePath { get; }
    public string EnvironmentPath { get; }
    public string ErrorPath { get; }
    public string CliPath { get; }

    public CodeqlStarterTestEnvironment(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        PackageDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "packages")).FullName;
        OutputDirectory = Path.Combine(_workspace.Path, "scan output");
        TracePath = Path.Combine(_workspace.Path, "commands.jsonl");
        GradleTracePath = Path.Combine(_workspace.Path, "gradle.txt");
        EnvironmentPath = Path.Combine(_workspace.Path, "environment.json");
        ErrorPath = Path.Combine(_workspace.Path, "error.txt");
        CliPath = Path.Combine(_workspace.Path, "aspire.ps1");
        _runnerPath = Path.Combine(_workspace.Path, "run.ps1");
        CreatePackage(PackageVersion);
        File.WriteAllText(CliPath, """
            param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
            Write-CommandTrace 'aspire' $Arguments
            $global:LASTEXITCODE = 0
            if ($Arguments -contains 'new') {
                if ($env:CODEQL_FAIL_COMMAND -eq 'new') {
                    $global:LASTEXITCODE = 7
                    return
                }
                $project = $Arguments[$Arguments.IndexOf('--output') + 1]
                $api = New-Item -ItemType Directory -Path (Join-Path $project 'api')
                $modules = New-Item -ItemType Directory -Path (Join-Path $project '.aspire' 'modules')
                if (-not $env:CODEQL_OMIT_JAVA_SOURCES) {
                    Set-Content (Join-Path $modules 'sources.txt') '.aspire/modules/aspire/Generated.java'
                }
                if ($IsWindows) {
                    Set-Content (Join-Path $api 'gradlew.bat') @'
            @echo off
            echo gradle^|%CD%^|%*>>"%CODEQL_GRADLE_TRACE%"
            if "%CODEQL_FAIL_COMMAND%"=="gradle" exit /b 7
            exit /b 0
            '@
                } else {
                    $gradle = Join-Path $api 'gradlew'
                    Set-Content $gradle @'
            #!/bin/sh
            printf 'gradle|%s|%s\n' "$PWD" "$*" >> "$CODEQL_GRADLE_TRACE"
            if [ "$CODEQL_FAIL_COMMAND" = "gradle" ]; then exit 7; fi
            exit 0
            '@
                    & chmod +x $gradle
                }
            }
            """);
        File.WriteAllText(_runnerPath, """
            function global:Write-CommandTrace {
                param([string]$Name, [string[]]$Arguments)
                [ordered]@{
                    Name = $Name
                    Arguments = @($Arguments)
                    Directory = $PWD.Path
                    Version = $env:ASPIRE_CLI_VERSION
                    Channel = $env:ASPIRE_CLI_CHANNEL
                    Packages = $env:ASPIRE_CLI_PACKAGES
                    Home = $env:ASPIRE_HOME
                } | ConvertTo-Json -Compress | Add-Content $env:CODEQL_TRACE
            }
            function global:go {
                Write-CommandTrace 'go' $args
                $global:LASTEXITCODE = if ($env:CODEQL_FAIL_COMMAND -eq 'go') { 7 } else { 0 }
            }
            function global:javac {
                Write-CommandTrace 'javac' $args
                $global:LASTEXITCODE = if ($env:CODEQL_FAIL_COMMAND -eq 'javac') { 7 } else { 0 }
            }
            $ErrorActionPreference = 'Stop'
            $env:ASPIRE_HOME = 'original-home'
            $env:ASPIRE_CLI_CHANNEL = 'original-channel'
            $env:ASPIRE_CLI_VERSION = 'original-version'
            $env:ASPIRE_CLI_PACKAGES = 'original-packages'
            try {
                & $env:CODEQL_SCRIPT -Language $env:CODEQL_LANGUAGE -CliPath $env:CODEQL_CLI `
                    -PackageDirectory $env:CODEQL_PACKAGES -OutputDirectory $env:CODEQL_OUTPUT
            } catch {
                Set-Content $env:CODEQL_ERROR $_.Exception.Message
                throw
            } finally {
                [ordered]@{
                    Home = $env:ASPIRE_HOME
                    Channel = $env:ASPIRE_CLI_CHANNEL
                    Version = $env:ASPIRE_CLI_VERSION
                    Packages = $env:ASPIRE_CLI_PACKAGES
                } | ConvertTo-Json -Compress | Set-Content $env:CODEQL_ENVIRONMENT
            }
            """);
    }

    public void CreatePackage(string version)
    {
        using var archive = ZipFile.Open(
            Path.Combine(PackageDirectory, $"Aspire.Hosting.AppHost.{version}.nupkg"), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("Aspire.Hosting.AppHost.nuspec").Open());
        writer.Write($"""
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>Aspire.Hosting.AppHost</id><version>{version}</version></metadata>
            </package>
            """);
    }

    public async Task<CommandResult> RunAsync(string language, string failCommand = "", bool omitJavaSources = false)
    {
        using var command = new PowerShellCommand(_runnerPath, _output)
            .WithTimeout(TimeSpan.FromSeconds(60))
            .WithEnvironmentVariable("CODEQL_SCRIPT", Path.Combine(RepoRoot.Path, "eng", "scripts", "build-codeql-starter.ps1"))
            .WithEnvironmentVariable("CODEQL_LANGUAGE", language)
            .WithEnvironmentVariable("CODEQL_CLI", CliPath)
            .WithEnvironmentVariable("CODEQL_PACKAGES", PackageDirectory)
            .WithEnvironmentVariable("CODEQL_OUTPUT", OutputDirectory)
            .WithEnvironmentVariable("CODEQL_TRACE", TracePath)
            .WithEnvironmentVariable("CODEQL_GRADLE_TRACE", GradleTracePath)
            .WithEnvironmentVariable("CODEQL_ENVIRONMENT", EnvironmentPath)
            .WithEnvironmentVariable("CODEQL_ERROR", ErrorPath)
            .WithEnvironmentVariable("CODEQL_FAIL_COMMAND", failCommand)
            .WithEnvironmentVariable("CODEQL_OMIT_JAVA_SOURCES", omitJavaSources ? "true" : "");

        return await command.ExecuteAsync();
    }

    public CodeqlStarterCommand[] ReadCommands()
    {
        return File.ReadAllLines(TracePath)
            .Select(line => JsonSerializer.Deserialize<CodeqlStarterCommand>(line)!)
            .ToArray();
    }

    public void Dispose() => _workspace.Dispose();
}

internal sealed record CodeqlStarterCommand(
    string Name, string[] Arguments, string Directory, string Version, string Channel, string Packages, string Home);
