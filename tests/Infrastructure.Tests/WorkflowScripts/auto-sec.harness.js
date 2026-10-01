// Drives .github/workflows/auto-sec/auto-sec.js for AutoSecWorkflowTests.
//
// argv[2]: request JSON path. argv[3]: result JSON path.
// Request modes:
//   { mode: "call", fn, args }                    -> { value }
//   { mode: "lookup", ecosystem, name, version, now, responses } -> { value, urls }
//   { mode: "approve", now, staged, agentItems, pr, files, contents, alerts,
//     malwareNumbers, checkRuns, statuses, reviews, responses } -> { value, reviews, summary, info, warnings }
// `responses` maps a URL to `{ status, body }`; unknown URLs return 404.
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
    const pages = {
        files: () => request.files.map(filename => ({ filename, status: 'modified' })),
        checks: () => request.checkRuns ?? [],
        reviews: () => (request.reviews ?? []).map(review => ({ user: { login: review.user_login }, state: review.state, commit_id: review.commit_id })),
    };
    const rest = {
        pulls: {
            get: async () => ({
                data: {
                    number: request.pr.number,
                    state: request.pr.state ?? 'open',
                    draft: request.pr.draft ?? false,
                    user: { login: request.pr.user_login ?? 'dependabot[bot]' },
                    head: { sha: request.pr.head_sha, ref: request.pr.head_ref },
                    base: { sha: 'base0000000000000000000000000000000000000' },
                    title: request.pr.title,
                    body: request.pr.body,
                },
            }),
            listFiles: pages.files,
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
                security_vulnerability: { first_patched_version: alert.first_patched_version ? { identifier: alert.first_patched_version } : null },
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
        default:
            throw new Error(`Unknown mode ${request.mode}`);
    }

    fs.writeFileSync(process.argv[3], JSON.stringify(result));
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
