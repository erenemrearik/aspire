// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class BuildCodeqlStarterTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task CompilesGoAppHostAndApiUsingSameBuildPackages()
    {
        using var environment = new CodeqlStarterTestEnvironment(output);

        var result = await environment.RunAsync("go");

        result.EnsureSuccessful();
        var project = Path.Combine(environment.OutputDirectory, "starter");
        Assert.Collection(environment.ReadCommands(),
            command => AssertFeature(command, "experimentalPolyglotGo"),
            command => AssertScaffold(command, "go", project),
            command => AssertCommand(command, "go", project, ["mod", "tidy"]),
            command => AssertCommand(command, "go", project,
                ["build", "-buildvcs=false", "-o", Path.Combine(environment.OutputDirectory, "apphost.exe"), "."]),
            command => AssertCommand(command, "go", Path.Combine(project, "api"), ["mod", "tidy"]),
            command => AssertCommand(command, "go", Path.Combine(project, "api"),
                ["build", "-buildvcs=false", "-o", Path.Combine(environment.OutputDirectory, "api.exe"), "."]));
        AssertSameBuildIdentity(environment);
        AssertRestoredEnvironment(environment);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task CompilesJavaAppHostSdkAndApiWithoutGradleDaemonOrCache()
    {
        using var environment = new CodeqlStarterTestEnvironment(output);

        var result = await environment.RunAsync("java");

        result.EnsureSuccessful();
        var project = Path.Combine(environment.OutputDirectory, "starter");
        Assert.Collection(environment.ReadCommands(),
            command => AssertFeature(command, "experimentalPolyglotJava"),
            command => AssertScaffold(command, "java", project),
            command => AssertCommand(command, "javac", project,
                ["--release", "25", "-d", Path.Combine(environment.OutputDirectory, "java-classes"),
                    "@.aspire/modules/sources.txt", "AppHost.java"]));
        Assert.Equal(
            $"gradle|{Path.Combine(project, "api")}|--no-daemon --no-build-cache clean classes",
            File.ReadAllText(environment.GradleTracePath).Trim());
        AssertSameBuildIdentity(environment);
        AssertRestoredEnvironment(environment);
    }

    [Theory]
    [InlineData("go", "new")]
    [InlineData("go", "go")]
    [InlineData("java", "javac")]
    [InlineData("java", "gradle")]
    [RequiresTools(["pwsh"])]
    public async Task FailsOnScaffoldOrCompilerErrorAndRestoresEnvironment(string language, string failCommand)
    {
        using var environment = new CodeqlStarterTestEnvironment(output);

        var result = await environment.RunAsync(language, failCommand);

        Assert.NotEqual(0, result.ExitCode);
        Assert.EndsWith("failed with exit code 7.", File.ReadAllText(environment.ErrorPath).Trim());
        AssertRestoredEnvironment(environment);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMultipleSameBuildAppHostVersions()
    {
        using var environment = new CodeqlStarterTestEnvironment(output);
        environment.CreatePackage("13.5.0-preview.1.26310.10");

        var result = await environment.RunAsync("go");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected exactly one same-build Aspire.Hosting.AppHost package, found 2", result.Output);
        Assert.False(Directory.Exists(environment.OutputDirectory));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsExistingOutputWithoutDeletingIt()
    {
        using var environment = new CodeqlStarterTestEnvironment(output);
        Directory.CreateDirectory(environment.OutputDirectory);
        var marker = Path.Combine(environment.OutputDirectory, "marker.txt");
        File.WriteAllText(marker, "preserve");

        var result = await environment.RunAsync("go");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("require fresh sources and outputs", result.Output);
        Assert.Equal("preserve", File.ReadAllText(marker));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMissingGeneratedJavaSourceList()
    {
        using var environment = new CodeqlStarterTestEnvironment(output);

        var result = await environment.RunAsync("java", omitJavaSources: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Generated Java SDK source list not found", result.Output);
        Assert.Equal(["aspire", "aspire"], environment.ReadCommands().Select(command => command.Name));
        AssertRestoredEnvironment(environment);
    }

    private static void AssertFeature(CodeqlStarterCommand command, string feature)
    {
        Assert.Equal("aspire", command.Name);
        Assert.Equal(["--nologo", "config", "set", $"features:{feature}", "true", "--global"], command.Arguments);
    }

    private static void AssertScaffold(CodeqlStarterCommand command, string language, string project)
    {
        Assert.Equal("aspire", command.Name);
        Assert.Equal(
            ["--nologo", "new", $"aspire-{language}-starter", "--name", "CodeqlStarter",
                "--output", project, "--version", CodeqlStarterTestEnvironment.PackageVersion,
                "--channel", "local", "--non-interactive", "--localhost-tld", "false", "--suppress-agent-init"],
            command.Arguments);
    }

    private static void AssertCommand(CodeqlStarterCommand command, string name, string directory, string[] arguments)
    {
        Assert.Equal(name, command.Name);
        Assert.Equal(directory, command.Directory);
        Assert.Equal(arguments, command.Arguments);
    }

    private static void AssertSameBuildIdentity(CodeqlStarterTestEnvironment environment)
    {
        Assert.All(environment.ReadCommands(), command =>
        {
            Assert.Equal(CodeqlStarterTestEnvironment.PackageVersion, command.Version);
            Assert.Equal("local", command.Channel);
            Assert.Equal(environment.PackageDirectory, command.Packages);
            Assert.Equal(Path.Combine(environment.OutputDirectory, "aspire-home"), command.Home);
        });
    }

    private static void AssertRestoredEnvironment(CodeqlStarterTestEnvironment environment)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(environment.EnvironmentPath));
        Assert.Equal("original-home", document.RootElement.GetProperty("Home").GetString());
        Assert.Equal("original-channel", document.RootElement.GetProperty("Channel").GetString());
        Assert.Equal("original-version", document.RootElement.GetProperty("Version").GetString());
        Assert.Equal("original-packages", document.RootElement.GetProperty("Packages").GetString());
    }
}
