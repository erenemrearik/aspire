// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class AutoSecWorkflowTests(ITestOutputHelper testOutput)
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Now = "2026-10-01T00:00:00Z";

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesDependabotPrThatPassesEveryGate()
    {
        var result = await RunHarnessAsync(CreateApprovalScenario());

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
        Assert.Empty(decision["reasons"]!.AsArray());
        Assert.Equal([7], decision["fixedAlerts"]!.AsArray().Select(n => n!.GetValue<int>()));
        var review = Assert.Single(result["reviews"]!.AsArray());
        Assert.Equal("APPROVE", review!["event"]!.GetValue<string>());
        Assert.Equal(101, review["pull_number"]!.GetValue<int>());
        Assert.Equal(HeadSha, review["commit_id"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task StagedModeEvaluatesGatesWithoutSubmittingReview()
    {
        var scenario = CreateApprovalScenario();
        scenario["staged"] = true;

        var result = await RunHarnessAsync(scenario);

        Assert.Equal("approve", result["value"]![0]!["decision"]!.GetValue<string>());
        Assert.Empty(result["reviews"]!.AsArray());
        Assert.Contains("(staged)", result["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("not-dependabot")]
    [InlineData("head-sha-mismatch")]
    [InlineData("actions-allow-list-required")]
    [InlineData("non-manifest-file-changed")]
    [InlineData("package-source-changed")]
    [InlineData("breaking-change")]
    [InlineData("cooldown-not-satisfied")]
    [InlineData("fixes-no-open-alert")]
    [InlineData("no-checks")]
    [InlineData("checks-not-green")]
    [InlineData("statuses-not-green")]
    [InlineData("already-approved")]
    public async Task SkipsDependabotPrThatFailsGate(string expectedReason)
    {
        var scenario = CreateApprovalScenario();
        var pr = scenario["pr"]!.AsObject();
        switch (expectedReason)
        {
            case "not-dependabot":
                pr["user_login"] = "someone";
                break;
            case "head-sha-mismatch":
                pr["head_sha"] = new string('b', 40);
                scenario["contents"]!["extension/yarn.lock@base"] = "resolved \"https://pkgs.dev.azure.com/x\"";
                break;
            case "actions-allow-list-required":
                pr["head_ref"] = "dependabot/github_actions/actions/checkout-4.2.0";
                break;
            case "non-manifest-file-changed":
                scenario["files"]!.AsArray().Add("NuGet.config");
                break;
            case "package-source-changed":
                scenario["contents"]!["extension/yarn.lock@head"] = "resolved \"https://registry.example.com/lodash\"";
                break;
            case "breaking-change":
                pr["title"] = "Bump lodash from 4.17.20 to 5.0.0 in /extension";
                pr["body"] = "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 5.0.0.";
                scenario["responses"]!["https://registry.npmjs.org/lodash"]!["body"]!["time"]!["5.0.0"] = "2026-09-01T00:00:00Z";
                break;
            case "cooldown-not-satisfied":
                scenario["responses"]!["https://registry.npmjs.org/lodash"]!["body"]!["time"]!["4.17.21"] = "2026-09-28T00:00:00Z";
                break;
            case "fixes-no-open-alert":
                scenario["alerts"]![0]!["first_patched_version"] = "4.17.22";
                break;
            case "no-checks":
                scenario["checkRuns"] = new JsonArray();
                break;
            case "checks-not-green":
                scenario["checkRuns"]!.AsArray().Add(new JsonObject { ["name"] = "tests", ["status"] = "in_progress", ["conclusion"] = null });
                break;
            case "statuses-not-green":
                scenario["statuses"] = new JsonArray(new JsonObject { ["context"] = "license/cla", ["state"] = "pending" });
                break;
            case "already-approved":
                scenario["reviews"] = new JsonArray(new JsonObject { ["user_login"] = "aspire-repo-bot[bot]", ["state"] = "APPROVED", ["commit_id"] = HeadSha });
                break;
        }

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("skip", decision!["decision"]!.GetValue<string>());
        // Gates run in a fixed order and keep evaluating, so a PR that fails an early gate
        // can also fail later ones (an actions PR has no package registry to check cooldown).
        Assert.Equal(expectedReason, decision["reasons"]![0]!.GetValue<string>());
        Assert.Empty(result["reviews"]!.AsArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task IgnoresMalformedAndDuplicateApprovalRequests()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "readApprovalRequests",
            ["args"] = new JsonArray(new JsonObject
            {
                ["items"] = new JsonArray(
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 5, ["head_sha"] = HeadSha },
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 5, ["head_sha"] = HeadSha },
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 6, ["head_sha"] = "main" },
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = -1, ["head_sha"] = HeadSha },
                    new JsonObject { ["type"] = "create_pull_request", ["pr_number"] = 7, ["head_sha"] = HeadSha }),
            }),
        });

        Assert.Equal(
            """[{"prNumber":5,"headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]""",
            result["value"]!.ToJsonString());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("4.17.20", "4.17.21", false)]
    [InlineData("4.17.20", "4.18.0", false)]
    [InlineData("4.17.20", "5.0.0", true)]
    [InlineData("0.3.1", "0.3.2", false)]
    [InlineData("0.3.1", "0.4.0", true)]
    [InlineData("0.0.3", "0.0.4", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "not-a-version", true)]
    [InlineData("v1.2.3", "v1.2.4", false)]
    public async Task ClassifiesBreakingChanges(string from, string to, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "isBreakingChange",
            ["args"] = new JsonArray(from, to),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParsesGroupedDependabotBody()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "parseDependabotUpdates",
            ["args"] = new JsonArray(
                "Bump the npm_and_yarn group across 2 directories with 2 updates",
                "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `@types/node` from 20.1.0 to 20.1.4\nUpdates `lodash` from 4.17.20 to 4.17.21"),
        });

        Assert.Equal(
            """[{"name":"lodash","from":"4.17.20","to":"4.17.21"},{"name":"@types/node","from":"20.1.0","to":"20.1.4"}]""",
            result["value"]!.ToJsonString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupReportsNuGetVersionMirroredOnApprovedFeed()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = "System.Text.Json",
            ["version"] = "9.0.5",
            ["now"] = Now,
            ["responses"] = new JsonObject
            {
                ["https://api.nuget.org/v3/registration5-semver1/system.text.json/9.0.5.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/system.text.json/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("9.0.4", "9.0.5") }),
            },
        });

        Assert.Equal(
            """{"ecosystem":"nuget","name":"System.Text.Json","version":"9.0.5","published_at":"2026-08-01T00:00:00Z","cooldown_satisfied":true,"available_on_approved_feed":true}""",
            result["value"]!.ToJsonString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupReportsUnmirroredNuGetVersionAndRecentNpmVersion()
    {
        var nuget = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = "Contoso.Lib",
            ["version"] = "1.2.3",
            ["now"] = Now,
            ["responses"] = new JsonObject
            {
                ["https://api.nuget.org/v3/registration5-semver1/contoso.lib/1.2.3.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
            },
        });
        var npm = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "npm",
            ["name"] = "@scope/pkg",
            ["version"] = "1.0.1",
            ["now"] = Now,
            ["responses"] = new JsonObject
            {
                ["https://registry.npmjs.org/@scope%2Fpkg"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.0.1"] = "2026-09-29T00:00:00Z" } }),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/@scope%2Fpkg"] = Response(new JsonObject { ["versions"] = new JsonObject { ["1.0.1"] = new JsonObject() } }),
            },
        });

        Assert.False(nuget["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.True(nuget["value"]!["cooldown_satisfied"]!.GetValue<bool>());
        Assert.True(npm["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.False(npm["value"]!["cooldown_satisfied"]!.GetValue<bool>());
    }

    [Fact]
    public void WorkflowRunsEveryTwelveHoursAndKeepsFeedConfigurationProtected()
    {
        var source = ReadWorkflow("auto-sec.md");
        var compiled = ReadWorkflow("auto-sec.lock.yml");

        Assert.Contains("- cron: \"17 */12 * * *\"", source, StringComparison.Ordinal);
        Assert.Contains("- cron: \"17 */12 * * *\"", compiled, StringComparison.Ordinal);
        Assert.Contains("labels: [auto-sec]", source, StringComparison.Ordinal);
        Assert.Contains("required-labels: [auto-sec]", source, StringComparison.Ordinal);
        Assert.Contains("check-branch-protection: false", source, StringComparison.Ordinal);
        Assert.Contains("allowed-branches: [\"auto-sec/security-updates\"]", source, StringComparison.Ordinal);
        Assert.Contains("approve_dependabot_pr:", compiled, StringComparison.Ordinal);
        Assert.Contains("gate.runApprovalJob({ github, approver, context, core })", compiled, StringComparison.Ordinal);

        var protectedFiles = GetSection(source, "^    protected-files: &auto-sec-protected", "^  push-to-pull-request-branch:");
        Assert.Contains("policy: blocked", protectedFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet.config", protectedFiles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("global.json", protectedFiles, StringComparison.Ordinal);
        Assert.DoesNotContain(".npmrc", protectedFiles, StringComparison.Ordinal);
    }

    private static JsonObject Response(JsonNode body) => new() { ["status"] = 200, ["body"] = body };

    private static JsonObject CreateApprovalScenario() => new()
    {
        ["mode"] = "approve",
        ["now"] = Now,
        ["staged"] = false,
        ["agentItems"] = new JsonArray(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 101, ["head_sha"] = HeadSha }),
        ["pr"] = new JsonObject
        {
            ["number"] = 101,
            ["user_login"] = "dependabot[bot]",
            ["head_sha"] = HeadSha,
            ["head_ref"] = "dependabot/npm_and_yarn/extension/lodash-4.17.21",
            ["title"] = "Bump lodash from 4.17.20 to 4.17.21 in /extension",
            ["body"] = "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 4.17.21.",
        },
        ["files"] = new JsonArray("extension/package.json", "extension/yarn.lock"),
        ["contents"] = new JsonObject
        {
            ["extension/yarn.lock@base"] = "resolved \"https://pkgs.dev.azure.com/dnceng/lodash-4.17.20.tgz\"",
            ["extension/yarn.lock@head"] = "resolved \"https://pkgs.dev.azure.com/dnceng/lodash-4.17.21.tgz\"",
        },
        ["alerts"] = new JsonArray(new JsonObject
        {
            ["number"] = 7,
            ["ecosystem"] = "npm",
            ["package"] = "lodash",
            ["manifest_path"] = "extension/yarn.lock",
            ["first_patched_version"] = "4.17.21",
        }),
        ["checkRuns"] = new JsonArray(new JsonObject { ["name"] = "ci", ["status"] = "completed", ["conclusion"] = "success" }),
        ["statuses"] = new JsonArray(),
        ["reviews"] = new JsonArray(),
        ["responses"] = new JsonObject
        {
            ["https://registry.npmjs.org/lodash"] = Response(new JsonObject { ["time"] = new JsonObject { ["4.17.21"] = "2026-09-01T00:00:00Z" } }),
        },
    };

    private async Task<JsonNode> RunHarnessAsync(JsonObject request)
    {
        using var workspace = TemporaryWorkspace.Create(testOutput);
        var requestPath = Path.Combine(workspace.Path, "request.json");
        var resultPath = Path.Combine(workspace.Path, "result.json");
        await File.WriteAllTextAsync(requestPath, request.ToJsonString());

        using var command = new NodeCommand(testOutput, "auto-sec");
        command.WithWorkingDirectory(RepoRoot.Path).WithTimeout(TimeSpan.FromSeconds(30));
        var result = await command.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "auto-sec.harness.js"),
            requestPath,
            resultPath);

        Assert.Equal(0, result.ExitCode);
        var response = JsonNode.Parse(await File.ReadAllTextAsync(resultPath));
        Assert.NotNull(response);
        return response;
    }

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

    private static string GetSection(string text, string startPattern, string endPattern)
    {
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        var start = Regex.Match(text, startPattern, options);
        Assert.True(start.Success, $"Missing section start '{startPattern}'.");
        var end = Regex.Match(text[(start.Index + start.Length)..], endPattern, options);
        Assert.True(end.Success, $"Missing section end '{endPattern}'.");
        return text.Substring(start.Index, start.Length + end.Index);
    }
}
