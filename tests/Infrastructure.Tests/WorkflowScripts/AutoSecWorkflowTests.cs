// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
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
    [InlineData("package-source-changed-other-org")]
    [InlineData("breaking-change")]
    [InlineData("breaking-change-grouped-transition")]
    [InlineData("breaking-change-unlisted-package")]
    [InlineData("breaking-change-consolidated-versions")]
    [InlineData("cooldown-not-satisfied")]
    [InlineData("cooldown-not-satisfied-unlisted-package")]
    [InlineData("too-many-version-changes")]
    [InlineData("malware-requires-review")]
    [InlineData("malware-requires-review-unlisted-package")]
    [InlineData("fixes-no-open-alert")]
    [InlineData("fixes-no-open-alert-other-directory")]
    [InlineData("fixes-no-open-alert-vulnerable-copy-remains")]
    [InlineData("no-checks")]
    [InlineData("checks-not-green")]
    [InlineData("statuses-not-green")]
    [InlineData("already-approved")]
    public async Task SkipsDependabotPrThatFailsGate(string scenarioName)
    {
        var expectedReason = scenarioName;
        var scenario = CreateApprovalScenario();
        var pr = scenario["pr"]!.AsObject();
        switch (scenarioName)
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
            case "package-source-changed-other-org":
                // Same host as the approved dnceng feeds, but another Azure DevOps organization.
                expectedReason = "package-source-changed";
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21", "https://pkgs.dev.azure.com/contoso/_packaging/feed/");
                break;
            case "breaking-change-grouped-transition":
                // The same target version reached from two starting versions: only the
                // second transition crosses a major version.
                expectedReason = "breaking-change";
                pr["head_ref"] = "dependabot/npm_and_yarn/npm_and_yarn-1a2b3c4d5e";
                pr["title"] = "Bump the npm_and_yarn group across 2 directories with 2 updates";
                pr["body"] = "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `lodash` from 3.10.1 to 4.17.21";
                break;
            case "breaking-change":
                pr["title"] = "Bump lodash from 4.17.20 to 5.0.0 in /extension";
                pr["body"] = "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 5.0.0.";
                scenario["responses"]!["https://registry.npmjs.org/lodash"]!["body"]!["time"]!["5.0.0"] = "2026-09-01T00:00:00Z";
                break;
            case "cooldown-not-satisfied":
                scenario["responses"]!["https://registry.npmjs.org/lodash"]!["body"]!["time"]!["4.17.21"] = "2026-09-28T00:00:00Z";
                break;
            case "breaking-change-unlisted-package":
                // The lockfile also moves react across a major version that the PR body omits.
                expectedReason = "breaking-change";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("react", "17.0.2");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("react", "18.2.0");
                scenario["responses"]!["https://registry.npmjs.org/react"] = Response(new JsonObject { ["time"] = new JsonObject { ["18.2.0"] = "2026-09-01T00:00:00Z" } });
                break;
            case "breaking-change-consolidated-versions":
                // The lockfile collapses foo@1.0.0 and foo@2.0.0 into foo@2.1.0, moving the
                // 1.x consumers across a major version.
                expectedReason = "breaking-change";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("foo", "1.0.0") + YarnLockEntry("foo", "2.0.0");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("foo", "2.1.0");
                scenario["responses"]!["https://registry.npmjs.org/foo"] = Response(new JsonObject { ["time"] = new JsonObject { ["2.1.0"] = "2026-09-01T00:00:00Z" } });
                break;
            case "malware-requires-review-unlisted-package":
                // The lockfile also changes minimist, which has an open malware alert, without
                // the PR body listing it.
                expectedReason = "malware-requires-review";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8");
                scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
                scenario["alerts"]!.AsArray().Add(new JsonObject
                {
                    ["number"] = 9,
                    ["ecosystem"] = "npm",
                    ["package"] = "minimist",
                    ["manifest_path"] = "extension/yarn.lock",
                    ["first_patched_version"] = null,
                });
                scenario["malwareNumbers"] = new JsonArray(9);
                break;
            case "cooldown-not-satisfied-unlisted-package":
                // The lockfile also bumps minimist to a release published three days ago.
                expectedReason = "cooldown-not-satisfied";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8");
                scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-28T00:00:00Z" } });
                break;
            case "too-many-version-changes":
                var baseLock = new StringBuilder(YarnLockEntry("lodash", "4.17.20"));
                var headLock = new StringBuilder(YarnLockEntry("lodash", "4.17.21"));
                for (var i = 0; i < 50; i++)
                {
                    baseLock.Append(YarnLockEntry($"package-{i}", "1.0.0"));
                    headLock.Append(YarnLockEntry($"package-{i}", "1.0.1"));
                }
                scenario["contents"]!["extension/yarn.lock@base"] = baseLock.ToString();
                scenario["contents"]!["extension/yarn.lock@head"] = headLock.ToString();
                break;
            case "fixes-no-open-alert-vulnerable-copy-remains":
                // The top-level lodash is bumped but a nested copy stays on the vulnerable version.
                expectedReason = "fixes-no-open-alert";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("lodash", "4.17.20");
                break;
            case "fixes-no-open-alert":
                scenario["alerts"]![0]!["first_patched_version"] = "4.17.22";
                break;
            case "malware-requires-review":
                scenario["malwareNumbers"] = new JsonArray(7);
                break;
            case "fixes-no-open-alert-other-directory":
                // A grouped PR bumps lodash in extension/ and only touches another package in
                // playground/app/, so the playground lodash alert is not fixed.
                expectedReason = "fixes-no-open-alert";
                pr["head_ref"] = "dependabot/npm_and_yarn/npm_and_yarn-1a2b3c4d5e";
                pr["title"] = "Bump the npm_and_yarn group across 2 directories with 2 updates";
                pr["body"] = "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `minimist` from 1.2.5 to 1.2.8";
                scenario["files"]!.AsArray().Add("playground/app/yarn.lock");
                scenario["contents"]!["playground/app/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
                scenario["contents"]!["playground/app/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.8");
                scenario["alerts"]![0]!["manifest_path"] = "playground/app/yarn.lock";
                scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
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
    [InlineData("0.0.3", "0.0.4", false)]
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

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1", 0)]
    public async Task ComparesPrereleaseVersionsBySemVerPrecedence(string left, string right, int expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "compareVersions",
            ["args"] = new JsonArray(left, right),
        });

        Assert.Equal(expected, result["value"]!.GetValue<int>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json", "")]
    [InlineData("https://registry.npmjs.org/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/feed/npm/registry/a/-/a-1.0.0.tgz", "https://pkgs.dev.azure.com/contoso/_packaging/feed/")]
    [InlineData("https://pkgs.dev.azure.com/dnceng/internal/_packaging/feed/npm/registry/a", "https://pkgs.dev.azure.com/dnceng/internal/_packaging/feed/")]
    [InlineData("https://registry.example.com/a/-/a-1.0.0.tgz", "https://registry.example.com/")]
    [InlineData("https://registry.npmjs.org:443/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://registry.npmjs.org:8443/a/-/a-1.0.0.tgz", "https://registry.npmjs.org:8443/")]
    [InlineData("git://github.com/a/b.git", "git://github.com/")]
    [InlineData("git://github.com:9418/a/b.git", "git://github.com/")]
    [InlineData("HTTPS://Registry.Example.com/a.tgz", "https://registry.example.com/")]
    [InlineData("HTTPS://REGISTRY.NPMJS.ORG/a/-/a-1.0.0.tgz", "")]
    public async Task FindsPackageSourcesAddedOnHead(string headUrl, string expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "findNewSources",
            ["args"] = new JsonArray("resolved \"https://registry.npmjs.org/b/-/b-1.0.0.tgz\"", $"resolved \"{headUrl}\""),
        });

        Assert.Equal(expected, string.Join(",", result["value"]!.AsArray().Select(source => source!.GetValue<string>())));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesPrWhoseUnlistedVersionChangesPassEveryGate()
    {
        var scenario = CreateApprovalScenario();
        scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
        scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8") + YarnLockEntry("left-pad", "1.3.0");
        scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
        scenario["responses"]!["https://registry.npmjs.org/left-pad"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.3.0"] = "2026-09-01T00:00:00Z" } });

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesGroupedPrWhenAlertDirectoryCarriesFixedVersion()
    {
        var scenario = CreateApprovalScenario();
        var pr = scenario["pr"]!.AsObject();
        pr["head_ref"] = "dependabot/npm_and_yarn/npm_and_yarn-1a2b3c4d5e";
        pr["title"] = "Bump the npm_and_yarn group across 2 directories with 1 update";
        pr["body"] = "Updates `lodash` from 4.17.20 to 4.17.21";
        scenario["files"]!.AsArray().Add("playground/app/yarn.lock");
        scenario["contents"]!["playground/app/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20");
        scenario["contents"]!["playground/app/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21");
        scenario["alerts"]!.AsArray().Add(new JsonObject
        {
            ["number"] = 8,
            ["ecosystem"] = "npm",
            ["package"] = "lodash",
            ["manifest_path"] = "playground/app/yarn.lock",
            ["first_patched_version"] = "4.17.21",
        });

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
        Assert.Equal([7, 8], decision["fixedAlerts"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CapsApprovalsAfterSkippingIneligiblePullRequests()
    {
        var scenario = CreateApprovalScenario();
        var items = new JsonArray();
        var overrides = new JsonObject();
        for (var number = 1; number <= 14; number++)
        {
            items.Add(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = number, ["head_sha"] = HeadSha });
            if (number <= 3)
            {
                overrides[number.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["user_login"] = "someone" };
            }
        }
        scenario["agentItems"] = items;
        scenario["prOverrides"] = overrides;

        var result = await RunHarnessAsync(scenario);

        Assert.Equal(
            ["not-dependabot", "not-dependabot", "not-dependabot", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approval-limit-reached"],
            result["value"]!.AsArray().Select(decision => decision!["decision"]!.GetValue<string>() == "approve"
                ? "approve"
                : decision["reasons"]![0]!.GetValue<string>()));
        Assert.Equal(Enumerable.Range(4, 10), result["reviews"]!.AsArray().Select(review => review!["pull_number"]!.GetValue<int>()));
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
                "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `@types/node` from 20.1.0 to 20.1.4\nUpdates `lodash` from 4.17.20 to 4.17.21\nUpdates `lodash` from 4.17.19 to 4.17.21"),
        });

        Assert.Equal(
            """[{"name":"lodash","from":"4.17.20","to":"4.17.21"},{"name":"@types/node","from":"20.1.0","to":"20.1.4"},{"name":"lodash","from":"4.17.19","to":"4.17.21"}]""",
            result["value"]!.ToJsonString());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("app/yarn.lock", "lodash@^4.17.20:\n  version \"4.17.20\"\nminimist@^4.17.21:\n  version \"4.17.21\"\n", "lodash", "4.17.21", false)]
    [InlineData("app/yarn.lock", "\"lodash@^4.17.20\", lodash@^4.17.21:\n  version \"4.17.21\"\n", "lodash", "4.17.21", true)]
    [InlineData("app/yarn.lock", "\"lodash@npm:^4.17.20\":\n  version: 4.17.21\n", "lodash", "4.17.21", true)]
    [InlineData("app/yarn.lock", "lodash-es@^4.17.21:\n  version \"4.17.21\"\n", "lodash", "4.17.21", false)]
    [InlineData("app/yarn.lock", "parent@^1.0.0:\n  version \"1.0.0\"\n  dependencies:\n    lodash \"4.17.21\"\n", "lodash", "4.17.21", false)]
    [InlineData("app/package-lock.json", """{"packages":{"node_modules/lodash":{"version":"4.17.20"},"node_modules/minimist":{"version":"4.17.21"}}}""", "lodash", "4.17.21", false)]
    [InlineData("app/package-lock.json", """{"packages":{"node_modules/a/node_modules/@scope/lodash":{"version":"4.17.21"}}}""", "@scope/lodash", "4.17.21", true)]
    [InlineData("app/npm-shrinkwrap.json", """{"dependencies":{"a":{"version":"1.0.0","dependencies":{"lodash":{"version":"4.17.21"}}}}}""", "lodash", "4.17.21", true)]
    [InlineData("app/package.json", """{"dependencies":{"lodash":"^4.17.20","minimist":"^4.17.21"}}""", "lodash", "4.17.21", false)]
    [InlineData("app/package.json", """{"devDependencies":{"lodash":"~4.17.21"}}""", "lodash", "4.17.21", true)]
    [InlineData("app/package.json", """{"resolutions":{"**/lodash":"4.17.21"}}""", "lodash", "4.17.21", true)]
    [InlineData("app/pnpm-lock.yaml", "packages:\n\n  lodash@4.17.20:\n    resolution: {}\n\n  minimist@4.17.21:\n    resolution: {}\n", "lodash", "4.17.21", false)]
    [InlineData("app/pnpm-lock.yaml", "packages:\n\n  '@babel/parser@7.29.3':\n    resolution: {}\n", "@babel/parser", "7.29.3", true)]
    [InlineData("app/pnpm-lock.yaml", "packages:\n  /lodash/4.17.21:\n    resolution: {}\n", "lodash", "4.17.21", true)]
    [InlineData("app/uv.lock", "[[package]]\nname = \"jinja2\"\nversion = \"3.1.5\"\n\n[[package]]\nname = \"markupsafe\"\nversion = \"3.1.6\"\n", "jinja2", "3.1.6", false)]
    [InlineData("app/uv.lock", "[[package]]\nname = \"Jinja2\"\nversion = \"3.1.6\"\n", "jinja2", "3.1.6", true)]
    [InlineData("app/pyproject.toml", "dependencies = [\"jinja2>=3.1.5\", \"markupsafe==3.1.6\"]\n", "jinja2", "3.1.6", false)]
    [InlineData("app/pyproject.toml", "dependencies = [\"typing_extensions[x] >= 4.12.2, < 5; python_version < '3.11'\"]\n", "typing-extensions", "4.12.2", true)]
    [InlineData("Directory.Packages.props", "<PackageVersion Include=\"A\" Version=\"9.0.4\" />\n<PackageVersion Include=\"B\" Version=\"9.0.5\" />", "A", "9.0.5", false)]
    [InlineData("Directory.Packages.props", "<PackageVersion Version=\"9.0.5\" Include=\"System.Text.Json\" />", "system.text.json", "9.0.5", true)]
    [InlineData("app/requirements.txt", "jinja2==3.1.6\n", "jinja2", "3.1.6", false)]
    public async Task BindsVersionToItsOwnManifestEntry(string path, string text, string name, string version, bool expected)
    {
        var ecosystem = Path.GetFileName(path) switch
        {
            "Directory.Packages.props" => "nuget",
            "uv.lock" or "pyproject.toml" or "requirements.txt" => "pip",
            _ => "npm",
        };
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "manifestMentionsVersion",
            ["args"] = new JsonArray(path, text, ecosystem, name, version),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportsAlertsCoveredByDependabotPr()
    {
        var alerts = new JsonArray(
            new JsonObject { ["number"] = 1, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 2, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "other/yarn.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 3, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = "4.17.22", ["malware"] = false },
            new JsonObject { ["number"] = 4, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = null, ["malware"] = true },
            new JsonObject { ["number"] = 5, ["ecosystem"] = "pip", ["package"] = "lodash", ["manifest_path"] = "app/uv.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 6, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "nested/yarn.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 8, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "nested/yarn.lock", ["first_patched_version"] = null, ["malware"] = true });
        var updates = new JsonArray(new JsonObject { ["name"] = "lodash", ["from"] = "4.17.20", ["to"] = "4.17.21" });
        var headContents = new JsonObject
        {
            ["app/yarn.lock"] = YarnLockEntry("lodash", "4.17.21"),
            ["other/yarn.lock"] = YarnLockEntry("lodash", "4.17.20"),
            // A nested copy keeps the vulnerable version, so neither alert in nested/ is covered.
            ["nested/yarn.lock"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("lodash", "4.17.20"),
        };

        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "coveredAlerts",
            ["args"] = new JsonArray(alerts, "npm", updates, headContents),
        });

        Assert.Equal([1, 4], result["value"]!.AsArray().Select(n => n!.GetValue<int>()));
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
            ["nugetConfigText"] = File.ReadAllText(Path.Combine(RepoRoot.Path, "NuGet.config")),
            ["responses"] = new JsonObject
            {
                ["https://api.nuget.org/v3/registration5-gz-semver2/system.text.json/9.0.5.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json"] = ServiceIndex("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/"),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/system.text.json/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("9.0.4", "9.0.5") }),
            },
        });

        Assert.Equal(
            """{"ecosystem":"nuget","name":"System.Text.Json","version":"9.0.5","published_at":"2026-08-01T00:00:00Z","cooldown_satisfied":true,"available_on_approved_feed":true}""",
            result["value"]!.ToJsonString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupProbesOnlyNuGetSourcesMappedToThePackage()
    {
        const string config = """
            <configuration>
              <packageSources>
                <clear />
                <add key="public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />
                <add key="transport" value="https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="public"><package pattern="*" /></packageSource>
                <packageSource key="transport"><package pattern="Contoso.Build*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="Contoso.Build.Tasks" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        const string transportFlat = "https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/flat2/";
        const string publicIndex = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json";
        var responses = new JsonObject
        {
            ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.build.engine/1.0.0.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
            ["https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json"] = ServiceIndex(transportFlat),
            [$"{transportFlat}contoso.build.engine/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("1.0.0") }),
            [publicIndex] = ServiceIndex("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/"),
        };

        // The longest prefix pattern wins, so only the transport feed is consulted.
        var prefixMapped = await RunHarnessAsync(NuGetLookup("Contoso.Build.Engine", config, responses));
        // An exact ID beats every prefix and maps only to a non-dnceng source, so nothing is probed.
        var exactMapped = await RunHarnessAsync(NuGetLookup("Contoso.Build.Tasks", config, responses.DeepClone().AsObject()));

        Assert.True(prefixMapped["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.Equal(
            [
                "https://api.nuget.org/v3/registration5-gz-semver2/contoso.build.engine/1.0.0.json",
                "https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json",
                $"{transportFlat}contoso.build.engine/index.json",
            ],
            prefixMapped["urls"]!.AsArray().Select(url => url!.GetValue<string>()));
        Assert.False(exactMapped["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.Equal(
            ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.build.tasks/1.0.0.json"],
            exactMapped["urls"]!.AsArray().Select(url => url!.GetValue<string>()));

        static JsonObject NuGetLookup(string name, string config, JsonObject responses) => new()
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = name,
            ["version"] = "1.0.0",
            ["now"] = Now,
            ["nugetConfigText"] = config,
            ["responses"] = responses,
        };
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
            ["nugetConfigText"] = File.ReadAllText(Path.Combine(RepoRoot.Path, "NuGet.config")),
            ["responses"] = new JsonObject
            {
                ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.lib/1.2.3.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
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

        // Approval requests carry only identifiers; free text would be persisted in the
        // agent output artifact.
        var approvalInputs = GetSection(source, "^      inputs:", "^      steps:");
        Assert.Equal(
            ["head_sha", "pr_number"],
            Regex.Matches(approvalInputs, "^        ([a-z_]+):\r?$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        Assert.Contains("covered_alerts: m.coveredAlerts(alerts, ecosystem, updates, headContents)", source, StringComparison.Ordinal);
    }

    private static JsonObject Response(JsonNode body) => new() { ["status"] = 200, ["body"] = body };

    private static string YarnLockEntry(string name, string version, string feedPrefix = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/")
        => $"{name}@^{version}:\n  version \"{version}\"\n  resolved \"{feedPrefix}npm/registry/{name}/-/{name}-{version}.tgz\"\n";

    private static JsonObject ServiceIndex(string flatContainer) => Response(new JsonObject
    {
        ["resources"] = new JsonArray(new JsonObject { ["@id"] = flatContainer, ["@type"] = "PackageBaseAddress/3.0.0" }),
    });

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
            ["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20"),
            ["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21"),
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
