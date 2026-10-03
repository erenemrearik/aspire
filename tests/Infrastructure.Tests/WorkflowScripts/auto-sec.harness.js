// Drives .github/workflows/auto-sec/auto-sec.js for AutoSecWorkflowTests.
//
// argv[2]: request JSON path. argv[3]: result JSON path.
// Request modes:
//   { mode: "call", fn, args }                    -> { value }
//   { mode: "lookup", ecosystem, name, version, now, responses, nugetConfigText } -> { value, urls }
//   { mode: "approve", now, staged, agentItems, pr, prOverrides, files, contents, alerts,
//     malwareNumbers, checkRuns, statuses, reviews, responses, liveHeadSha, liveBaseRef } -> { value, reviews, summary, info, warnings }
//   { mode: "push-gate", agentItems, pr, prOverrides } -> { value, info, failures }
// PRs may carry `head_repo` (defaults to microsoft/aspire) and `base_ref` (defaults to main).
// Alerts may carry `vulnerable_version_range` and `advisory_ranges` (every range the
// advisory lists for the package).
// `responses` maps a URL to `{ status, body }`; unknown URLs return 404.
// `prOverrides` maps a PR number to fields that replace `pr` for that number.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const gate = require(path.join(__dirname, '..', '..', '..', '.github', 'workflows', 'auto-sec', 'auto-sec.js'));

function reviveDates(value) {
    if (value && typeof value === 'object' && typeof value.now === 'string') {
        return { ...value, now: new Date(value.now) };
    }
    return value;
}

function createFetch(responses, urls) {
    return async url => {
        urls.push(url);
        const response = responses?.[url];
        const status = response?.status ?? 404;
        return {
            status,
            ok: status >= 200 && status < 300,
            json: async () => response?.body ?? null,
        };
    };
}

function createGitHub(request, created) {
    const notFound = () => Object.assign(new Error('Not Found'), { status: 404 });
    const getCalls = new Map();
    const pages = {
        files: () => request.files.map(filename => ({ filename, status: 'modified' })),
        commits: () => (request.commits ?? [{ author_login: 'dependabot[bot]', verified: true }])
            .map(commit => ({ author: { login: commit.author_login }, commit: { verification: { verified: commit.verified } } })),
        checks: () => request.checkRuns ?? [],
        reviews: () => (request.reviews ?? []).map(review => ({ user: { login: review.user_login }, state: review.state, commit_id: review.commit_id })),
    };
    const rest = {
        pulls: {
            get: async ({ pull_number: pullNumber }) => {
                const pr = { ...request.pr, number: pullNumber, ...(request.prOverrides?.[pullNumber] ?? {}) };
                // `liveHeadSha` / `liveBaseRef` simulate a push or retarget that lands after the gates were evaluated.
                getCalls.set(pullNumber, (getCalls.get(pullNumber) ?? 0) + 1);
                if (request.liveHeadSha && getCalls.get(pullNumber) > 1) {
                    pr.head_sha = request.liveHeadSha;
                }
                if (request.liveBaseRef && getCalls.get(pullNumber) > 1) {
                    pr.base_ref = request.liveBaseRef;
                }
                return {
                    data: {
                        number: pr.number,
                        state: pr.state ?? 'open',
                        draft: pr.draft ?? false,
                        user: { login: pr.user_login ?? 'dependabot[bot]' },
                        head: { sha: pr.head_sha, ref: pr.head_ref, repo: { full_name: pr.head_repo ?? 'microsoft/aspire' } },
                        base: { sha: 'base0000000000000000000000000000000000000', ref: pr.base_ref ?? 'main' },
                        title: pr.title,
                        body: pr.body,
                    },
                };
            },
            listFiles: pages.files,
            listCommits: pages.commits,
            listReviews: pages.reviews,
            createReview: async args => {
                created.push(args);
                return { data: { id: 1 } };
            },
        },
        checks: { listForRef: pages.checks },
        repos: {
            getContent: async ({ path: filePath, ref }) => {
                const key = `${filePath}@${ref === request.pr.head_sha ? 'head' : 'base'}`;
                if (!(key in (request.contents ?? {}))) {
                    throw notFound();
                }
                return { data: request.contents[key] };
            },
            getCombinedStatusForRef: async () => ({ data: { statuses: request.statuses ?? [] } }),
        },
    };
    const paginate = async (route, params) => {
        if (typeof route === 'function') {
            return route(params);
        }
        if (route === 'GET /repos/{owner}/{repo}/dependabot/alerts') {
            const alerts = request.alerts ?? [];
            if (params.classification === 'malware') {
                const malware = new Set(request.malwareNumbers ?? []);
                return alerts.filter(alert => malware.has(alert.number)).map(alert => ({ number: alert.number }));
            }
            return alerts.map(alert => ({
                number: alert.number,
                dependency: { package: { ecosystem: alert.ecosystem, name: alert.package }, manifest_path: alert.manifest_path },
                security_vulnerability: {
                    vulnerable_version_range: alert.vulnerable_version_range ?? null,
                    first_patched_version: alert.first_patched_version ? { identifier: alert.first_patched_version } : null,
                },
                security_advisory: {
                    vulnerabilities: (alert.advisory_ranges ?? []).map(range => ({
                        package: { ecosystem: alert.ecosystem, name: alert.package },
                        vulnerable_version_range: range,
                    })),
                },
            }));
        }
        throw new Error(`Unexpected paginate route ${route}`);
    };
    return { rest, paginate };
}

async function main() {
    const request = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
    let result;

    switch (request.mode) {
        case 'call': {
            const value = gate[request.fn](...request.args.map(reviveDates));
            result = { value: value instanceof Set ? [...value].sort() : value };
            break;
        }
        case 'lookup': {
            const urls = [];
            const value = await gate.lookupPackageVersion(request.ecosystem, request.name, request.version, {
                fetchImpl: createFetch(request.responses, urls),
                now: new Date(request.now),
                nugetConfigText: request.nugetConfigText ?? null,
            });
            result = { value, urls };
            break;
        }
        case 'approve': {
            const reviews = [];
            const info = [];
            const warnings = [];
            let summary = '';
            const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const outputPath = path.join(outputDir, 'agent_output.json');
            fs.writeFileSync(outputPath, JSON.stringify({ items: request.agentItems ?? [] }));
            const core = {
                info: message => info.push(message),
                warning: message => warnings.push(message),
                summary: {
                    addHeading: text => { summary += `${text}\n`; return core.summary; },
                    addRaw: text => { summary += text; return core.summary; },
                    write: async () => {},
                },
            };
            try {
                const value = await gate.runApprovalJob({
                    github: createGitHub(request, []),
                    approver: createGitHub(request, reviews),
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    core,
                    env: { GH_AW_AGENT_OUTPUT: outputPath, GH_AW_SAFE_OUTPUTS_STAGED: request.staged ? 'true' : 'false' },
                    fetchImpl: createFetch(request.responses, []),
                    now: new Date(request.now),
                });
                result = { value, reviews, summary, info, warnings };
            } finally {
                fs.rmSync(outputDir, { recursive: true, force: true });
            }
            break;
        }
        case 'push-gate': {
            const info = [];
            const failures = [];
            const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const outputPath = path.join(outputDir, 'agent_output.json');
            fs.writeFileSync(outputPath, JSON.stringify({ items: request.agentItems ?? [] }));
            try {
                const value = await gate.runPushTargetGate({
                    github: createGitHub(request, []),
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    core: { info: message => info.push(message), setFailed: message => failures.push(message) },
                    env: { GH_AW_AGENT_OUTPUT: outputPath },
                });
                result = { value, info, failures };
            } finally {
                fs.rmSync(outputDir, { recursive: true, force: true });
            }
            break;
        }
        default:
            throw new Error(`Unknown mode ${request.mode}`);
    }

    fs.writeFileSync(process.argv[3], JSON.stringify(result));
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
