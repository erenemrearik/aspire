// Deterministic helpers for the auto-sec agentic workflow (.github/workflows/auto-sec.md).
//
// Two consumers:
//   1. The `approve-dependabot-pr` safe-output job calls `runApprovalJob` through
//      actions/github-script. The agent only *requests* an approval; every gate is
//      re-verified here against live GitHub and registry data before the Aspire bot
//      App submits an APPROVE review, so a confused or prompt-injected agent cannot
//      approve anything the gates reject.
//   2. The agent runs `node auto-sec.js lookup <ecosystem> <name> <version>` to get the
//      publish date (7-day cooldown) and approved-feed availability of a candidate
//      version before proposing it in an auto-sec pull request.

'use strict';

const COOLDOWN_DAYS = 7;
const DEPENDABOT_LOGIN = 'dependabot[bot]';
const DEFAULT_BOT_LOGIN = 'aspire-repo-bot[bot]';
const MAX_APPROVAL_REQUESTS = 10;

// Package hosts that are always acceptable in a lockfile or manifest. Any other host
// must already be present in the file on the PR base, so a bump can never move a
// dependency to a new registry or source feed.
const APPROVED_HOSTS = ['pkgs.dev.azure.com', 'dnceng.pkgs.visualstudio.com'];

// NuGet feeds from NuGet.config. A NuGet bump is only proposed when the version
// already restores from one of these; otherwise it is reported as blocked on mirroring.
const APPROVED_NUGET_FEEDS = [
    'dotnet-public',
    'dotnet-eng',
    'dotnet-tools',
    'dotnet9',
    'dotnet10',
    'dotnet11',
    'dotnet-libraries',
];

const APPROVED_NPM_REGISTRY = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/';

// Dependency manifests a Dependabot PR may touch and still be auto-approved. Anything
// else (feed configuration, SDK pins, workflow files, source code) disqualifies the PR.
const ALLOWED_MANIFEST_BASENAMES = new Set([
    'package.json',
    'package-lock.json',
    'npm-shrinkwrap.json',
    'yarn.lock',
    'pnpm-lock.yaml',
    'uv.lock',
    'pyproject.toml',
    'directory.packages.props',
]);

const SUCCESSFUL_CHECK_CONCLUSIONS = new Set(['success', 'skipped', 'neutral']);

// Dependabot branch names look like `dependabot/<package-manager>/<dir>/<pkg>-<version>`.
// Map the package-manager segment to the Dependabot alert ecosystem name.
const BRANCH_ECOSYSTEMS = {
    npm_and_yarn: 'npm',
    pip: 'pip',
    uv: 'pip',
    nuget: 'nuget',
    github_actions: 'actions',
};

function parseVersion(value) {
    if (typeof value !== 'string') {
        return null;
    }

    // Accept `1.2.3`, `v1.2.3`, `1.2`, `1.2.3.4`, `1.2.3-beta.1`, `1.2.3+build`.
    const match = /^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(value.trim());
    if (!match) {
        return null;
    }

    return {
        parts: [match[1], match[2], match[3], match[4]].map(part => (part === undefined ? 0 : Number(part))),
        prerelease: match[5] ?? '',
    };
}

function compareVersions(left, right) {
    const a = parseVersion(left);
    const b = parseVersion(right);
    if (!a || !b) {
        return null;
    }

    for (let i = 0; i < a.parts.length; i++) {
        if (a.parts[i] !== b.parts[i]) {
            return a.parts[i] < b.parts[i] ? -1 : 1;
        }
    }

    // A prerelease sorts before its release (1.0.0-rc.1 < 1.0.0).
    if (a.prerelease === b.prerelease) {
        return 0;
    }
    if (a.prerelease === '') {
        return 1;
    }
    if (b.prerelease === '') {
        return -1;
    }
    return a.prerelease < b.prerelease ? -1 : 1;
}

// Semver treats any change to the left-most non-zero component as breaking:
// 1.x -> 2.x, 0.3.x -> 0.4.x and 0.0.3 -> 0.0.4 are all major changes. Unparseable
// versions and downgrades fail closed and are treated as breaking.
function isBreakingChange(from, to) {
    const a = parseVersion(from);
    const b = parseVersion(to);
    if (!a || !b) {
        return true;
    }

    const [aMajor, aMinor, aPatch] = a.parts;
    const [bMajor, bMinor, bPatch] = b.parts;
    if (aMajor !== bMajor) {
        return true;
    }
    if (aMajor === 0 && aMinor !== bMinor) {
        return true;
    }
    if (aMajor === 0 && aMinor === 0 && aPatch !== bPatch) {
        return true;
    }
    return compareVersions(from, to) !== -1;
}

function normalizePackageName(ecosystem, name) {
    const lowered = String(name ?? '').trim().toLowerCase();
    // PEP 503: runs of `-`, `_` and `.` are equivalent in Python package names.
    return ecosystem === 'pip' ? lowered.replace(/[-_.]+/g, '-') : lowered;
}

function ecosystemFromBranch(headRef) {
    const match = /^dependabot\/([^/]+)\//.exec(headRef ?? '');
    return match ? BRANCH_ECOSYSTEMS[match[1]] ?? null : null;
}

function stripTrailingPunctuation(value) {
    return value.replace(/[.,;:)]+$/, '');
}

// Extract `{ name, from, to }` updates from a Dependabot PR. Dependabot writes:
//   title: "Bump lodash from 4.17.20 to 4.17.21 in /playground/app"
//   body (single):  "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 4.17.21."
//   body (grouped): "Updates `lodash` from 4.17.20 to 4.17.21"
// Grouped titles ("Bump the npm_and_yarn group across 2 directories with 3 updates")
// carry no versions, so the body is authoritative and the title is a fallback.
function parseDependabotUpdates(title, body) {
    const updates = new Map();
    const add = (name, from, to) => {
        const cleanTo = stripTrailingPunctuation(to);
        const key = `${name.toLowerCase()}@${cleanTo}`;
        if (!updates.has(key)) {
            updates.set(key, { name, from: stripTrailingPunctuation(from), to: cleanTo });
        }
    };

    for (const match of String(body ?? '').matchAll(/Updates `([^`]+)` from (\S+) to (\S+)/g)) {
        add(match[1], match[2], match[3]);
    }
    for (const match of String(body ?? '').matchAll(/Bumps \[([^\]]+)\]\([^)]*\) from (\S+) to (\S+)/g)) {
        add(match[1], match[2], match[3]);
    }
    if (updates.size === 0) {
        const titleMatch = /^Bump (\S+) from (\S+) to (\S+)/.exec(String(title ?? ''));
        if (titleMatch) {
            add(titleMatch[1], titleMatch[2], titleMatch[3]);
        }
    }

    return [...updates.values()];
}

function directoryOf(path) {
    const index = String(path).lastIndexOf('/');
    return index < 0 ? '' : path.slice(0, index);
}

function basenameOf(path) {
    const index = String(path).lastIndexOf('/');
    return index < 0 ? path : path.slice(index + 1);
}

function isAllowedManifest(path) {
    if (String(path).startsWith('.github/')) {
        return false;
    }
    return ALLOWED_MANIFEST_BASENAMES.has(basenameOf(path).toLowerCase());
}

function extractHosts(text) {
    const hosts = new Set();
    for (const match of String(text ?? '').matchAll(/\b(?:https?|git\+https?|git\+ssh|ssh):\/\/(?:[^@/\s"']+@)?([A-Za-z0-9.-]+)/g)) {
        hosts.add(match[1].toLowerCase());
    }
    return hosts;
}

// Hosts present in the head version of a file that are neither approved nor already
// present on the base version. A non-empty result means the PR introduces a new source.
function findNewHosts(baseText, headText) {
    const baseHosts = extractHosts(baseText);
    return [...extractHosts(headText)]
        .filter(host => !baseHosts.has(host) && !APPROVED_HOSTS.includes(host))
        .sort();
}

function isCooldownSatisfied(publishedAt, now, days = COOLDOWN_DAYS) {
    const published = Date.parse(publishedAt ?? '');
    if (Number.isNaN(published)) {
        return false;
    }
    return now.getTime() - published >= days * 24 * 60 * 60 * 1000;
}

function alertFixedByUpdate(alert, ecosystem, update, changedDirectories) {
    if (alert.ecosystem !== ecosystem) {
        return false;
    }
    if (normalizePackageName(ecosystem, alert.package) !== normalizePackageName(ecosystem, update.name)) {
        return false;
    }
    if (!changedDirectories.has(directoryOf(alert.manifest_path ?? ''))) {
        return false;
    }
    // Malware alerts have no patched version; replacing the flagged version with a
    // clean one is the fix, so require only that the version actually changes.
    if (alert.malware) {
        return update.from !== update.to;
    }
    const comparison = alert.first_patched_version ? compareVersions(update.to, alert.first_patched_version) : null;
    return comparison !== null && comparison >= 0;
}

/**
 * Pure gate evaluation for a single approval request.
 *
 * Returns `{ decision, reasons, fixedAlerts }` where decision is `approve` or `skip`.
 * Every reason is a short machine code so the run summary stays free of advisory detail.
 */
function evaluateApprovalGates(input) {
    const reasons = [];
    const { pr, expectedHeadSha, files, alerts, checkRuns, statuses, hostChanges, packageInfo, reviews, now } = input;
    const botLogin = input.botLogin ?? DEFAULT_BOT_LOGIN;

    if (pr.user_login !== DEPENDABOT_LOGIN) {
        reasons.push('not-dependabot');
    }
    if (pr.state !== 'open' || pr.draft) {
        reasons.push('not-open');
    }
    if (!expectedHeadSha || pr.head_sha !== expectedHeadSha) {
        reasons.push('head-sha-mismatch');
    }

    const ecosystem = ecosystemFromBranch(pr.head_ref);
    if (!ecosystem) {
        reasons.push('unknown-ecosystem');
    } else if (ecosystem === 'actions') {
        // Action pin bumps need a matching update to the repository Actions allow-list,
        // which a bot cannot confirm, so they always stay with a human reviewer.
        reasons.push('actions-allow-list-required');
    }

    if (!files.length) {
        reasons.push('no-files');
    }
    if (files.some(file => !isAllowedManifest(file.filename))) {
        reasons.push('non-manifest-file-changed');
    }
    if (hostChanges.some(change => change.newHosts.length > 0)) {
        reasons.push('package-source-changed');
    }

    const updates = parseDependabotUpdates(pr.title, pr.body);
    if (!updates.length) {
        reasons.push('no-parseable-updates');
    }
    if (updates.some(update => isBreakingChange(update.from, update.to))) {
        reasons.push('breaking-change');
    }
    if (updates.some(update => !isCooldownSatisfied(packageInfo[`${update.name}@${update.to}`]?.published_at, now))) {
        reasons.push('cooldown-not-satisfied');
    }

    const changedDirectories = new Set(files.map(file => directoryOf(file.filename)));
    const fixedAlerts = ecosystem
        ? alerts
            .filter(alert => updates.some(update => alertFixedByUpdate(alert, ecosystem, update, changedDirectories)))
            .map(alert => alert.number)
            .sort((a, b) => a - b)
        : [];
    if (!fixedAlerts.length) {
        reasons.push('fixes-no-open-alert');
    }

    if (!checkRuns.length) {
        reasons.push('no-checks');
    }
    if (checkRuns.some(run => run.status !== 'completed' || !SUCCESSFUL_CHECK_CONCLUSIONS.has(run.conclusion))) {
        reasons.push('checks-not-green');
    }
    if (statuses.some(status => status.state !== 'success')) {
        reasons.push('statuses-not-green');
    }

    if (reviews.some(review => review.user_login === botLogin && review.state === 'APPROVED' && review.commit_id === pr.head_sha)) {
        reasons.push('already-approved');
    }

    return {
        decision: reasons.length === 0 ? 'approve' : 'skip',
        reasons,
        fixedAlerts,
    };
}

function npmPackagePath(name) {
    // Scoped names keep the `@` and encode the slash: @scope/pkg -> @scope%2fpkg.
    return name.startsWith('@') ? `@${encodeURIComponent(name.slice(1))}` : encodeURIComponent(name);
}

async function fetchJson(fetchImpl, url) {
    const response = await fetchImpl(url, { headers: { accept: 'application/json' } });
    if (response.status === 404) {
        return null;
    }
    if (!response.ok) {
        throw new Error(`GET ${url} failed with HTTP ${response.status}`);
    }
    return response.json();
}

/**
 * Look up publish date and approved-feed availability for one package version.
 * Returns `{ ecosystem, name, version, published_at, cooldown_satisfied, available_on_approved_feed }`.
 */
async function lookupPackageVersion(ecosystem, name, version, { fetchImpl = fetch, now = new Date() } = {}) {
    let publishedAt = null;
    let available = false;

    switch (ecosystem) {
        case 'npm': {
            const packument = await fetchJson(fetchImpl, `https://registry.npmjs.org/${npmPackagePath(name)}`);
            publishedAt = packument?.time?.[version] ?? null;
            const mirror = await fetchJson(fetchImpl, `${APPROVED_NPM_REGISTRY}${npmPackagePath(name)}`);
            available = Boolean(mirror?.versions?.[version]);
            break;
        }
        case 'pip': {
            // The repository's uv.lock files resolve from PyPI, so PyPI is the approved source.
            const release = await fetchJson(fetchImpl, `https://pypi.org/pypi/${encodeURIComponent(name)}/${encodeURIComponent(version)}/json`);
            const uploads = (release?.urls ?? []).map(file => file.upload_time_iso_8601).filter(Boolean).sort();
            publishedAt = uploads[0] ?? null;
            available = release !== null;
            break;
        }
        case 'nuget': {
            const id = name.toLowerCase();
            const normalizedVersion = version.toLowerCase();
            const leaf = await fetchJson(fetchImpl, `https://api.nuget.org/v3/registration5-semver1/${id}/${normalizedVersion}.json`);
            publishedAt = leaf?.published ?? null;
            for (const feed of APPROVED_NUGET_FEEDS) {
                const index = await fetchJson(fetchImpl, `https://pkgs.dev.azure.com/dnceng/public/_packaging/${feed}/nuget/v3/flat2/${id}/index.json`);
                if ((index?.versions ?? []).some(entry => entry.toLowerCase() === normalizedVersion)) {
                    available = true;
                    break;
                }
            }
            break;
        }
        default:
            throw new Error(`Unsupported ecosystem '${ecosystem}'. Expected npm, pip, or nuget.`);
    }

    return {
        ecosystem,
        name,
        version,
        published_at: publishedAt,
        cooldown_satisfied: isCooldownSatisfied(publishedAt, now),
        available_on_approved_feed: available,
    };
}

function readApprovalRequests(agentOutput) {
    const items = Array.isArray(agentOutput?.items) ? agentOutput.items : [];
    const seen = new Set();
    const requests = [];
    for (const item of items) {
        if (item?.type !== 'approve_dependabot_pr') {
            continue;
        }
        const prNumber = Number(item.pr_number);
        const headSha = String(item.head_sha ?? '').trim().toLowerCase();
        if (!Number.isInteger(prNumber) || prNumber <= 0 || !/^[0-9a-f]{40}$/.test(headSha) || seen.has(prNumber)) {
            continue;
        }
        seen.add(prNumber);
        requests.push({ prNumber, headSha });
    }
    return requests.slice(0, MAX_APPROVAL_REQUESTS);
}

async function getFileText(github, owner, repo, path, ref) {
    try {
        const response = await github.rest.repos.getContent({ owner, repo, path, ref, mediaType: { format: 'raw' } });
        return typeof response.data === 'string' ? response.data : '';
    } catch (error) {
        if (error?.status === 404) {
            return '';
        }
        throw error;
    }
}

function normalizeAlert(alert) {
    return {
        number: alert.number,
        ecosystem: alert.dependency?.package?.ecosystem ?? '',
        package: alert.dependency?.package?.name ?? '',
        manifest_path: alert.dependency?.manifest_path ?? '',
        first_patched_version: alert.security_vulnerability?.first_patched_version?.identifier ?? null,
        malware: false,
    };
}

async function collectGateInput(github, owner, repo, request, { fetchImpl, now, botLogin }) {
    const { data: pull } = await github.rest.pulls.get({ owner, repo, pull_number: request.prNumber });
    const pr = {
        number: pull.number,
        state: pull.state,
        draft: Boolean(pull.draft),
        user_login: pull.user?.login ?? '',
        head_sha: pull.head?.sha ?? '',
        head_ref: pull.head?.ref ?? '',
        base_sha: pull.base?.sha ?? '',
        title: pull.title ?? '',
        body: pull.body ?? '',
    };

    const files = (await github.paginate(github.rest.pulls.listFiles, { owner, repo, pull_number: pr.number, per_page: 100 }))
        .map(file => ({ filename: file.filename, status: file.status }));

    // Compare full base/head file contents instead of the PR patch: GitHub omits the
    // patch for large lockfiles, and a missing patch must not hide a registry change.
    const hostChanges = [];
    for (const file of files.filter(entry => isAllowedManifest(entry.filename))) {
        const baseText = await getFileText(github, owner, repo, file.filename, pr.base_sha);
        const headText = await getFileText(github, owner, repo, file.filename, pr.head_sha);
        hostChanges.push({ filename: file.filename, newHosts: findNewHosts(baseText, headText) });
    }

    const alerts = (await github.paginate('GET /repos/{owner}/{repo}/dependabot/alerts', { owner, repo, state: 'open', per_page: 100 }))
        .map(normalizeAlert);
    const malware = await github.paginate('GET /repos/{owner}/{repo}/dependabot/alerts', { owner, repo, state: 'open', classification: 'malware', per_page: 100 });
    const malwareNumbers = new Set(malware.map(alert => alert.number));
    for (const alert of alerts) {
        alert.malware = malwareNumbers.has(alert.number);
    }

    const checkRuns = (await github.paginate(github.rest.checks.listForRef, { owner, repo, ref: pr.head_sha, per_page: 100 }))
        .map(run => ({ name: run.name, status: run.status, conclusion: run.conclusion }));
    const { data: combined } = await github.rest.repos.getCombinedStatusForRef({ owner, repo, ref: pr.head_sha });
    const statuses = (combined.statuses ?? []).map(status => ({ context: status.context, state: status.state }));

    const reviews = (await github.paginate(github.rest.pulls.listReviews, { owner, repo, pull_number: pr.number, per_page: 100 }))
        .map(review => ({ user_login: review.user?.login ?? '', state: review.state, commit_id: review.commit_id }));

    const ecosystem = ecosystemFromBranch(pr.head_ref);
    const packageInfo = {};
    if (ecosystem && ecosystem !== 'actions') {
        for (const update of parseDependabotUpdates(pr.title, pr.body)) {
            try {
                packageInfo[`${update.name}@${update.to}`] = await lookupPackageVersion(ecosystem, update.name, update.to, { fetchImpl, now });
            } catch {
                // A failed lookup leaves the entry missing, which fails the cooldown gate closed.
            }
        }
    }

    return { pr, expectedHeadSha: request.headSha, files, alerts, checkRuns, statuses, hostChanges, packageInfo, reviews, now, botLogin };
}

/**
 * Entry point for the `approve-dependabot-pr` safe-output job (actions/github-script).
 * Reads approval requests from the agent output, re-verifies all gates, and approves
 * only the PRs that pass. Staged mode logs decisions without submitting reviews.
 *
 * `github` performs all reads with the job's GITHUB_TOKEN (which carries
 * `security-events: read` for Dependabot alerts); `approver` is an Octokit client for
 * the Aspire bot App and is used only to submit the APPROVE review.
 */
async function runApprovalJob({ github, approver = github, context, core, fs = require('node:fs'), env = process.env, fetchImpl = fetch, now = new Date() }) {
    const outputPath = env.GH_AW_AGENT_OUTPUT;
    if (!outputPath || !fs.existsSync(outputPath)) {
        core.info('No agent output found; nothing to approve.');
        return [];
    }

    const requests = readApprovalRequests(JSON.parse(fs.readFileSync(outputPath, 'utf8')));
    const staged = env.GH_AW_SAFE_OUTPUTS_STAGED === 'true';
    const botLogin = env.AUTO_SEC_BOT_LOGIN || DEFAULT_BOT_LOGIN;
    const { owner, repo } = context.repo;
    const results = [];

    for (const request of requests) {
        let result;
        try {
            const input = await collectGateInput(github, owner, repo, request, { fetchImpl, now, botLogin });
            result = { pr: request.prNumber, ...evaluateApprovalGates(input) };
        } catch (error) {
            result = { pr: request.prNumber, decision: 'skip', reasons: ['gate-evaluation-failed'], fixedAlerts: [] };
            core.warning(`Gate evaluation failed for #${request.prNumber}: ${error.message}`);
        }

        if (result.decision === 'approve' && !staged) {
            await approver.rest.pulls.createReview({
                owner,
                repo,
                pull_number: request.prNumber,
                commit_id: request.headSha,
                event: 'APPROVE',
                body: 'Automated dependency review: this update passed the auto-sec checks (package sources unchanged, checks green, non-breaking, and past the 7-day cooldown).',
            });
        }

        results.push(result);
        core.info(`#${result.pr}: ${result.decision}${staged ? ' (staged)' : ''} ${result.reasons.join(',')}`);
    }

    const approved = results.filter(result => result.decision === 'approve').length;
    await core.summary
        .addHeading('auto-sec Dependabot approvals', 3)
        .addRaw(`Requests: ${results.length}. Approved: ${approved}${staged ? ' (staged)' : ''}. Skipped: ${results.length - approved}.\n\n`)
        .addRaw(results.map(result => `- #${result.pr}: ${result.decision}${result.reasons.length ? ` (${result.reasons.join(', ')})` : ''}`).join('\n'))
        .write();

    return results;
}

async function main(argv) {
    const [command, ecosystem, name, version] = argv;
    if (command !== 'lookup' || !ecosystem || !name || !version) {
        console.error('Usage: node auto-sec.js lookup <npm|pip|nuget> <package> <version>');
        process.exitCode = 2;
        return;
    }
    console.log(JSON.stringify(await lookupPackageVersion(ecosystem, name, version)));
}

if (require.main === module) {
    main(process.argv.slice(2)).catch(error => {
        console.error(error.message);
        process.exitCode = 1;
    });
}

module.exports = {
    APPROVED_HOSTS,
    COOLDOWN_DAYS,
    alertFixedByUpdate,
    compareVersions,
    ecosystemFromBranch,
    evaluateApprovalGates,
    extractHosts,
    findNewHosts,
    isAllowedManifest,
    isBreakingChange,
    isCooldownSatisfied,
    lookupPackageVersion,
    normalizePackageName,
    parseDependabotUpdates,
    readApprovalRequests,
    runApprovalJob,
};
