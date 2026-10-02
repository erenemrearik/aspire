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
// At most this many reviews are submitted per run. Requests beyond the limit are still
// evaluated (bounded by MAX_EVALUATED_REQUESTS) so ineligible or already-approved PRs
// at the front of the agent's list cannot starve eligible ones on every run.
const MAX_APPROVALS = 10;
const MAX_EVALUATED_REQUESTS = 100;
// Upper bound on distinct package versions one PR may introduce. Each needs a registry
// lookup for the cooldown gate, so a larger diff is left to a human reviewer.
const MAX_VERSION_CHANGES = 50;

// Package sources that are always acceptable in a lockfile or manifest: the
// repository's dnceng public Azure Artifacts feeds only. Any other source must
// already be present in the file on the PR base, so a bump can never move a
// dependency to a new registry or feed. Sources are compared as keys built by
// `sourceKey`, never as bare hosts, because pkgs.dev.azure.com hosts every
// Azure DevOps organization's feeds.
const APPROVED_SOURCE_PREFIXES = [
    'https://pkgs.dev.azure.com/dnceng/public/_packaging/',
    'https://dnceng.pkgs.visualstudio.com/public/_packaging/',
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

const DEFAULT_PORTS = {
    'http': 80,
    'https': 443,
    'git+http': 80,
    'git+https': 443,
    'ssh': 22,
    'git+ssh': 22,
    'git': 9418,
};

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
    return comparePrerelease(a.prerelease, b.prerelease);
}

// SemVer 2.0.0 section 11 (https://semver.org/#spec-item-11): compare dot-separated
// identifiers left to right. Numeric identifiers compare numerically and sort before
// alphanumeric ones, alphanumeric identifiers compare in ASCII order, and a shorter
// identifier list sorts first when all preceding identifiers are equal
// (alpha < alpha.1 < alpha.beta < beta < beta.2 < beta.11 < rc.1).
function comparePrerelease(left, right) {
    const a = left.split('.');
    const b = right.split('.');
    for (let i = 0; i < Math.min(a.length, b.length); i++) {
        const aNumeric = /^\d+$/.test(a[i]);
        const bNumeric = /^\d+$/.test(b[i]);
        if (aNumeric && bNumeric) {
            const difference = Number(a[i]) - Number(b[i]);
            if (difference !== 0) {
                return difference < 0 ? -1 : 1;
            }
        } else if (aNumeric !== bNumeric) {
            return aNumeric ? -1 : 1;
        } else if (a[i] !== b[i]) {
            return a[i] < b[i] ? -1 : 1;
        }
    }
    return a.length === b.length ? 0 : (a.length < b.length ? -1 : 1);
}

// A change to the major version is breaking, and for 0.x a change to the minor version
// is too (0.3.x -> 0.4.x). Patch updates within the same 0.x minor (0.0.3 -> 0.0.4)
// are accepted, matching the policy documented in auto-sec.md and the README.
// Unparseable versions and downgrades fail closed and are treated as breaking.
function isBreakingChange(from, to) {
    const a = parseVersion(from);
    const b = parseVersion(to);
    if (!a || !b) {
        return true;
    }

    const [aMajor, aMinor] = a.parts;
    const [bMajor, bMinor] = b.parts;
    if (aMajor !== bMajor) {
        return true;
    }
    if (aMajor === 0 && aMinor !== bMinor) {
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
// Updates are keyed by the full transition: a grouped PR can move the same package to
// the same version from different starting versions in different directories
// (2.0.0 -> 2.0.1 and 1.9.0 -> 2.0.1), and each transition is checked separately.
function parseDependabotUpdates(title, body) {
    const updates = new Map();
    const add = (name, from, to) => {
        const cleanFrom = stripTrailingPunctuation(from);
        const cleanTo = stripTrailingPunctuation(to);
        const key = `${name.toLowerCase()}@${cleanFrom}->${cleanTo}`;
        if (!updates.has(key)) {
            updates.set(key, { name, from: cleanFrom, to: cleanTo });
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

// Reduce a package URL to the identity of the source that serves it, so two URLs from
// the same feed compare equal while feeds on a shared multi-tenant host do not:
//   https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/lodash/-/lodash-4.17.21.tgz
//     -> https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/
//   https://pkgs.dev.azure.com/contoso/_packaging/feed/npm/registry/lodash
//     -> https://pkgs.dev.azure.com/contoso/_packaging/feed/
//   https://pkgs.dev.azure.com/contoso/project/_apis/...   -> https://pkgs.dev.azure.com/contoso/project/
//   https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz -> https://registry.npmjs.org/
// Azure Artifacts feeds are keyed through `/_packaging/<feed>/`; any other URL on an Azure
// DevOps host is keyed by organization and project; everything else is keyed by host.
// A non-default port is part of the origin (`https://registry.npmjs.org:8443/` is a
// different server than `https://registry.npmjs.org/`); the scheme's default port is
// dropped so `:443` on an https URL compares equal to no port.
function sourceKey(scheme, host, port, path) {
    const loweredScheme = scheme.toLowerCase();
    const portSuffix = port && DEFAULT_PORTS[loweredScheme] !== Number(port) ? `:${Number(port)}` : '';
    const origin = `${loweredScheme}://${host.toLowerCase()}${portSuffix}`;
    const feed = /^(\/(?:[^/?#]+\/){0,2}_packaging\/[^/?#]+\/)/i.exec(path);
    if (feed) {
        return `${origin}${feed[1].toLowerCase()}`;
    }
    if (host.toLowerCase() === 'pkgs.dev.azure.com' || /\.pkgs\.visualstudio\.com$/i.test(host)) {
        const scope = /^(\/(?:[^/?#]+\/){0,2})/.exec(`${path}/`);
        return `${origin}${scope[1].toLowerCase()}`;
    }
    return `${origin}/`;
}

// Every URL scheme npm, yarn, pnpm, uv and NuGet accept for a package location:
//   https://registry.npmjs.org/a/-/a-1.0.0.tgz   git+https://github.com/o/r.git#v1
//   git+ssh://git@github.com/o/r.git             git://github.com/o/r.git
//   ssh://git@github.com/o/r.git
// URI schemes and hosts are case-insensitive (RFC 3986 sections 3.1 and 3.2.2), so
// `HTTPS://Registry.Example.com/` is matched and keyed like its lowercase form.
function extractSources(text) {
    const sources = new Set();
    for (const match of String(text ?? '').matchAll(/\b(https?|git\+https?|git\+ssh|git|ssh):\/\/(?:[^@/\s"']+@)?([A-Za-z0-9.-]+)(?::(\d+))?(\/[^\s"'<>]*)?/gi)) {
        sources.add(sourceKey(match[1], match[2], match[3], match[4] ?? '/'));
    }
    return sources;
}

function isApprovedSource(source) {
    return APPROVED_SOURCE_PREFIXES.some(prefix => source.startsWith(prefix));
}

// Sources present in the head version of a file that are neither approved nor already
// present on the base version. A non-empty result means the PR introduces a new source.
function findNewSources(baseText, headText) {
    const baseSources = extractSources(baseText);
    return [...extractSources(headText)]
        .filter(source => !baseSources.has(source) && !isApprovedSource(source))
        .sort();
}

function isCooldownSatisfied(publishedAt, now, days = COOLDOWN_DAYS) {
    const published = Date.parse(publishedAt ?? '');
    if (Number.isNaN(published)) {
        return false;
    }
    return now.getTime() - published >= days * 24 * 60 * 60 * 1000;
}

// Split an npm package spec into name and version range at the `@` that follows the name.
// The leading `@` of a scoped name is part of the name:
//   lodash@^4.17.20          -> lodash, ^4.17.20
//   @babel/parser@7.29.3     -> @babel/parser, 7.29.3
//   lodash@npm:^4.17.20      -> lodash, npm:^4.17.20 (yarn berry)
function splitNpmSpec(spec) {
    const index = spec.indexOf('@', 1);
    return index < 0 ? { name: spec, range: '' } : { name: spec.slice(0, index), range: spec.slice(index + 1) };
}

// Reduce a range to its version: `^4.17.21`, `~4.17.21`, `>=4.17.21`, `=4.17.21` and
// `v4.17.21` all become `4.17.21`. Compound ranges (`>=1 <2`) keep only the first bound.
function stripRangeOperators(range) {
    return String(range ?? '').trim().replace(/^npm:/, '').replace(/^[\^~=<>v\s]+/, '').split(/[\s,|]/)[0];
}

// Name targeted by an `overrides` or `resolutions` key. Keys can be a bare name, a spec
// with a range, or a glob path ending in the package:
//   lodash, lodash@^4, **/lodash, parent/lodash, **/@scope/pkg, @scope/pkg@1
function overrideKeyName(key) {
    const segments = key.split('/');
    const last = segments.length >= 2 && segments[segments.length - 2].startsWith('@')
        ? segments.slice(-2).join('/')
        : segments[segments.length - 1];
    return splitNpmSpec(last).name;
}

function readJson(text) {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

// Every manifest reader below returns the `{ name, version }` entries the file declares
// or resolves. A package can appear more than once (a lockfile with a top-level and a
// nested copy, or a dependency plus an override), and every occurrence is returned.

// package.json pins a range per dependency map; `overrides` nest by package name and
// `resolutions` keys can be glob paths (`**/lodash`), so both are searched recursively.
// A nested override object pins its own package through the `.` key:
//   "overrides": { "parent": { ".": "1.0.0", "lodash": "4.17.21" } }
function packageJsonEntries(text) {
    const json = readJson(text);
    if (!json) {
        return [];
    }
    const entries = [];
    for (const map of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
        for (const [key, value] of Object.entries(json[map] ?? {})) {
            if (typeof value === 'string') {
                entries.push({ name: key, version: stripRangeOperators(value) });
            }
        }
    }
    const visit = node => {
        for (const [key, value] of Object.entries(node ?? {})) {
            if (typeof value === 'string' && key !== '.') {
                entries.push({ name: overrideKeyName(key), version: stripRangeOperators(value) });
            } else if (value && typeof value === 'object') {
                if (typeof value['.'] === 'string') {
                    entries.push({ name: overrideKeyName(key), version: stripRangeOperators(value['.']) });
                }
                visit(value);
            }
        }
    };
    visit(json.overrides);
    visit(json.resolutions);
    return entries;
}

// package-lock.json / npm-shrinkwrap.json:
//   v2/v3: { "packages": { "node_modules/a/node_modules/lodash": { "version": "4.17.21" } } }
//   v1:    { "dependencies": { "lodash": { "version": "4.17.21", "dependencies": { ... } } } }
function packageLockEntries(text) {
    const json = readJson(text);
    if (!json) {
        return [];
    }
    const entries = [];
    for (const [path, entry] of Object.entries(json.packages ?? {})) {
        const index = path.lastIndexOf('node_modules/');
        if (index >= 0 && typeof entry?.version === 'string') {
            entries.push({ name: path.slice(index + 'node_modules/'.length), version: entry.version });
        }
    }
    const visit = dependencies => {
        for (const [key, entry] of Object.entries(dependencies ?? {})) {
            if (typeof entry?.version === 'string') {
                entries.push({ name: key, version: entry.version });
            }
            visit(entry?.dependencies);
        }
    };
    visit(json.dependencies);
    return entries;
}

// yarn.lock blocks start with an unindented header listing every spec the entry
// resolves, followed by two-space indented fields:
//   classic: "lodash@^4.17.20", lodash@^4.17.21:\n  version "4.17.21"
//   berry:   "lodash@npm:^4.17.20":\n  version: 4.17.21
// Nested `dependencies:` entries are indented further and never bind a version here.
// A header can alias several names to one block; the version is attributed to each.
function yarnLockEntries(text) {
    const entries = [];
    let names = [];
    for (const line of String(text ?? '').split(/\r?\n/)) {
        if (/^\S/.test(line)) {
            names = !line.startsWith('#') && line.trimEnd().endsWith(':')
                ? [...new Set(line.trimEnd().slice(0, -1).split(',')
                    .map(spec => splitNpmSpec(spec.trim().replace(/^"|"$/g, '')).name)
                    .filter(Boolean))]
                : [];
            continue;
        }
        const version = names.length ? /^ {2}version:?\s+"?([^"\s]+)"?\s*$/.exec(line) : null;
        if (version) {
            entries.push(...names.map(name => ({ name, version: version[1] })));
        }
    }
    return entries;
}

// pnpm-lock.yaml lists each resolved package as a two-space indented key, with optional
// quotes, an optional leading `/`, and an optional peer suffix:
//   v9:  '@babel/parser@7.29.3':     lodash@4.17.21(react@18.0.0):
//   v6:  /lodash@4.17.21:
//   v5:  /lodash/4.17.21:            /@babel/parser/7.29.3:
// Keys without a version (`importers:` children such as `  .:`) are skipped.
function pnpmLockEntries(text) {
    const entries = [];
    for (const [, rawKey] of String(text ?? '').matchAll(/^ {2}(\S.*?):\s*$/gm)) {
        const key = rawKey.replace(/^'|'$/g, '').replace(/\(.*$/, '');
        const trimmed = key.replace(/^\//, '');
        let { name, range } = splitNpmSpec(trimmed);
        if (!range && key.startsWith('/')) {
            const slash = trimmed.lastIndexOf('/');
            name = trimmed.slice(0, slash);
            range = trimmed.slice(slash + 1);
        }
        if (range && name) {
            entries.push({ name, version: range });
        }
    }
    return entries;
}

// uv.lock: [[package]]\nname = "jinja2"\nversion = "3.1.6"
function uvLockEntries(text) {
    const entries = [];
    for (const block of String(text ?? '').split(/^\[\[package\]\]\s*$/m).slice(1)) {
        const body = block.split(/^\[/m)[0];
        const name = /^name\s*=\s*"([^"]+)"/m.exec(body);
        const version = /^version\s*=\s*"([^"]+)"/m.exec(body);
        if (name && version) {
            entries.push({ name: name[1], version: version[1] });
        }
    }
    return entries;
}

// pyproject.toml PEP 508 requirement strings (https://peps.python.org/pep-0508/):
//   "jinja2>=3.1.6", "jinja2[i18n]==3.1.6; python_version >= '3.9'", "jinja2 ~= 3.1.6, < 4"
// Only the lower or exact bound proves the version, so `<`, `<=` and `!=` are ignored.
function pyprojectEntries(text) {
    const entries = [];
    for (const [, requirement] of String(text ?? '').matchAll(/["']([A-Za-z0-9][A-Za-z0-9._-]*\s*(?:\[[^\]]*\])?\s*(?:===|==|~=|>=|>)[^"']*)["']/g)) {
        const name = /^[A-Za-z0-9][A-Za-z0-9._-]*/.exec(requirement)[0];
        for (const [, version] of requirement.split(';')[0].matchAll(/(?:===|==|~=|>=|>)\s*([^\s,;]+)/g)) {
            entries.push({ name, version });
        }
    }
    return entries;
}

// Directory.Packages.props: <PackageVersion Include="System.Text.Json" Version="9.0.5" />
// The attributes may appear in either order.
function packagesPropsEntries(text) {
    const entries = [];
    for (const [element] of String(text ?? '').replace(/<!--[\s\S]*?-->/g, '').matchAll(/<PackageVersion\b[^>]*>/gi)) {
        const include = xmlAttribute(element, 'Include');
        const version = xmlAttribute(element, 'Version');
        if (include && version) {
            entries.push({ name: include, version: version.replace(/^\[|\]$/g, '') });
        }
    }
    return entries;
}

const MANIFEST_READERS = {
    'package.json': packageJsonEntries,
    'package-lock.json': packageLockEntries,
    'npm-shrinkwrap.json': packageLockEntries,
    'yarn.lock': yarnLockEntries,
    'pnpm-lock.yaml': pnpmLockEntries,
    'uv.lock': uvLockEntries,
    'pyproject.toml': pyprojectEntries,
    'directory.packages.props': packagesPropsEntries,
};

// Lockfiles record every resolved copy of a package. The other manifests record only a
// declared range, whose lower bound (`^4.0.0`) says nothing about what is installed.
const LOCKFILE_BASENAMES = new Set(['package-lock.json', 'npm-shrinkwrap.json', 'yarn.lock', 'pnpm-lock.yaml', 'uv.lock']);

function manifestReader(path) {
    return MANIFEST_READERS[basenameOf(String(path ?? '')).toLowerCase()] ?? null;
}

function sameVersion(left, right) {
    return left === right || compareVersions(left, right) === 0;
}

/**
 * Every version the manifest at `path` binds to `name`, one per occurrence. Package
 * names compare case-insensitively, with pip names normalized per PEP 503. An
 * unrecognized manifest returns an empty list so callers fail closed.
 */
function manifestPackageVersions(path, text, ecosystem, name) {
    const reader = manifestReader(path);
    if (!reader) {
        return [];
    }
    const wanted = normalizePackageName(ecosystem, name);
    return reader(text)
        .filter(entry => normalizePackageName(ecosystem, entry.name) === wanted)
        .map(entry => entry.version);
}

/**
 * True when the manifest at `path` binds `name` to `version` in one of its own package
 * entries. Each manifest format is parsed so a version is only attributed to the entry
 * that declares it, never to a neighbouring package.
 */
function manifestMentionsVersion(path, text, ecosystem, name, version) {
    return manifestPackageVersions(path, text, ecosystem, name).some(found => sameVersion(found, version));
}

/**
 * Package versions a PR introduces in one manifest: every `{ name, from, to }` where
 * `to` is bound on head but not on base. `from` lists the base versions the new one
 * replaces: those removed by the PR, or, when the PR adds a copy and keeps the old ones,
 * every base version of the package. It is empty for a package that is new to the file.
 */
function manifestVersionChanges(path, baseText, headText, ecosystem) {
    const reader = manifestReader(path);
    if (!reader) {
        return [];
    }
    const group = text => {
        const packages = new Map();
        for (const { name, version } of reader(text)) {
            const key = normalizePackageName(ecosystem, name);
            if (!packages.has(key)) {
                packages.set(key, { name, versions: new Set() });
            }
            packages.get(key).versions.add(version);
        }
        return packages;
    };
    const base = group(baseText);
    const changes = [];
    for (const [key, { name, versions }] of group(headText)) {
        const baseVersions = [...(base.get(key)?.versions ?? [])];
        const removed = baseVersions.filter(version => !versions.has(version));
        for (const version of versions) {
            if (!baseVersions.includes(version)) {
                changes.push({ name, from: removed.length ? removed : baseVersions, to: version });
            }
        }
    }
    return changes;
}

// A new version is breaking when any base version it replaces crosses a breaking
// boundary: a lockfile that consolidates `foo@1.x` and `foo@2.0` into `foo@2.1` moves
// the `1.x` consumers across a major version. A package new to the manifest has no
// predecessor and is left to the cooldown and source gates.
function isBreakingVersionChange(change) {
    return change.from.some(from => isBreakingChange(from, change.to));
}

/**
 * True when every occurrence of `update.name` in the changed manifests of the alert's
 * directory is `acceptable`, and at least one occurrence is the update's new version.
 * A lockfile can keep an old nested copy (`lodash@4.17.20` under a parent) next to the
 * bumped top-level one, so a single matching occurrence does not prove the fix. When the
 * directory has a changed lockfile, only lockfiles are consulted, because a declared
 * range in package.json does not say which version is installed.
 */
function alertDirectoryCarries(alert, ecosystem, update, headContents, acceptable) {
    const alertDirectory = directoryOf(alert.manifest_path ?? '');
    const manifests = Object.entries(headContents ?? {}).filter(([path]) => directoryOf(path) === alertDirectory);
    const lockfiles = manifests.filter(([path]) => LOCKFILE_BASENAMES.has(basenameOf(path).toLowerCase()));
    const versions = (lockfiles.length ? lockfiles : manifests)
        .flatMap(([path, text]) => manifestPackageVersions(path, text, ecosystem, update.name));
    return versions.some(version => sameVersion(version, update.to)) && versions.every(acceptable);
}

function updateMatchesAlert(alert, ecosystem, update) {
    return alert.ecosystem === ecosystem
        && normalizePackageName(ecosystem, alert.package) === normalizePackageName(ecosystem, update.name);
}

/**
 * True when `update` provably fixes `alert`. Grouped Dependabot PRs can update the same
 * package in several directories, so a match on package name alone is not enough: the
 * changed manifests in the alert's own directory must carry the new version on the PR
 * head, and every remaining occurrence of the package there must be at or above the
 * first patched version. `headContents` maps each changed manifest path to its head
 * text. Malware alerts are never counted as fixed (see `evaluateApprovalGates`).
 */
function alertFixedByUpdate(alert, ecosystem, update, headContents) {
    if (alert.malware || !updateMatchesAlert(alert, ecosystem, update)) {
        return false;
    }
    const patched = alert.first_patched_version;
    const comparison = patched ? compareVersions(update.to, patched) : null;
    if (comparison === null || comparison < 0) {
        return false;
    }
    // `first_patched_version` only bounds the range the installed version fell in; an
    // advisory can list disjoint vulnerable ranges, so every occurrence must also sit
    // outside all of them.
    return alertDirectoryCarries(alert, ecosystem, update, headContents,
        version => (compareVersions(version, patched) ?? -1) >= 0 && !inVulnerableRanges(version, alertVulnerableRanges(alert)));
}

// The approval job's alerts carry every advisory range (`vulnerable_ranges`); the agent's
// pre-collected alerts.json carries only the alert's own `vulnerable_version_range`.
function alertVulnerableRanges(alert) {
    return alert.vulnerable_ranges ?? (alert.vulnerable_version_range ? [alert.vulnerable_version_range] : []);
}

/**
 * True when `version` falls in any GitHub advisory range, or a range cannot be evaluated
 * (fail closed). Ranges use the advisory database syntax: comma-separated comparator
 * terms that must all hold, for example `< 1.2.5`, `>= 1.3.0, < 1.3.4`, `= 2.0.0`.
 * See https://docs.github.com/rest/dependabot/alerts.
 */
function inVulnerableRanges(version, ranges) {
    return ranges.some(range => {
        const terms = String(range).split(',').map(term => term.trim()).filter(Boolean);
        if (!terms.length) {
            return true;
        }
        return terms.every(term => {
            const match = /^(<=|>=|<|>|=)\s*(\S+)$/.exec(term);
            const comparison = match ? compareVersions(version, match[2]) : null;
            if (comparison === null) {
                return true;
            }
            switch (match[1]) {
                case '<': return comparison < 0;
                case '<=': return comparison <= 0;
                case '>': return comparison > 0;
                case '>=': return comparison >= 0;
                default: return comparison === 0;
            }
        });
    });
}

/**
 * Alert numbers a Dependabot PR covers, so the agent does not duplicate the fix in the
 * auto-sec PR. Non-malware alerts use the same proof as the approval gate
 * (`alertFixedByUpdate`). Malware alerts have no patched version; they are covered only
 * when every occurrence of the flagged package in the alert's directory is the PR's new
 * version, and the approval gate still leaves those PRs to a human reviewer.
 */
function coveredAlerts(alerts, ecosystem, updates, headContents) {
    if (!ecosystem) {
        return [];
    }
    return (alerts ?? [])
        .filter(alert => updates.some(update => alert.malware
            ? updateMatchesAlert(alert, ecosystem, update)
                && alertDirectoryCarries(alert, ecosystem, update, headContents, version => sameVersion(version, update.to))
            : alertFixedByUpdate(alert, ecosystem, update, headContents)))
        .map(alert => alert.number)
        .sort((a, b) => a - b);
}

/**
 * Pure gate evaluation for a single approval request.
 *
 * Returns `{ decision, reasons, fixedAlerts }` where decision is `approve` or `skip`.
 * Every reason is a short machine code so the run summary stays free of advisory detail.
 */
function evaluateApprovalGates(input) {
    const reasons = [];
    const { pr, expectedHeadSha, files, alerts, checkRuns, statuses, sourceChanges, headContents, packageInfo, reviews, now } = input;
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
    if (sourceChanges.some(change => change.newSources.length > 0)) {
        reasons.push('package-source-changed');
    }
    // `versionChanges` is every package version the diff introduces, including ones the
    // PR body does not list, so the gates below cannot be bypassed by an unlisted bump.
    const versionChanges = input.versionChanges ?? [];
    const tooManyVersionChanges = versionChanges.length > MAX_VERSION_CHANGES;
    if (tooManyVersionChanges) {
        reasons.push('too-many-version-changes');
    }

    const updates = parseDependabotUpdates(pr.title, pr.body);
    if (!updates.length) {
        reasons.push('no-parseable-updates');
    }
    if (updates.some(update => isBreakingChange(update.from, update.to))
        || versionChanges.some(isBreakingVersionChange)) {
        reasons.push('breaking-change');
    }
    const published = (name, version) => packageInfo[`${name}@${version}`]?.published_at;
    if (updates.some(update => !isCooldownSatisfied(published(update.name, update.to), now))
        || (!tooManyVersionChanges && versionChanges.some(change => !isCooldownSatisfied(published(change.name, change.to), now)))) {
        reasons.push('cooldown-not-satisfied');
    }

    // Malware alerts have no patched version, so no version check can prove the update
    // removes the flagged release. Fail closed and leave any PR touching one to a human,
    // including packages changed only by lockfile regeneration.
    const touched = [...updates, ...versionChanges];
    if (ecosystem && alerts.some(alert => alert.malware && touched.some(update => updateMatchesAlert(alert, ecosystem, update)))) {
        reasons.push('malware-requires-review');
    }

    const fixedAlerts = ecosystem
        ? alerts
            .filter(alert => updates.some(update => alertFixedByUpdate(alert, ecosystem, update, headContents)))
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

function xmlAttribute(element, name) {
    const match = new RegExp(`\\b${name}\\s*=\\s*"([^"]*)"`, 'i').exec(element);
    return match ? match[1] : null;
}

function xmlSection(xml, tag) {
    const match = new RegExp(`<${tag}\\b[^>]*>([\\s\\S]*?)</${tag}>`, 'i').exec(xml);
    return match ? match[1] : '';
}

/**
 * Parse the parts of a NuGet.config that decide where a package restores from:
 *   <packageSources><add key="dotnet-public" value="https://.../index.json" /></packageSources>
 *   <packageSourceMapping><packageSource key="dotnet-public"><package pattern="*" /></packageSource></packageSourceMapping>
 *   <disabledPackageSources><add key="dotnet-public" value="true" /></disabledPackageSources>
 * Returns `{ sources: [{ key, url }], mapping: Map<key, pattern[]> }`.
 */
function parseNuGetConfig(xml) {
    const text = String(xml ?? '').replace(/<!--[\s\S]*?-->/g, '');
    const disabled = new Set();
    for (const [element] of xmlSection(text, 'disabledPackageSources').matchAll(/<add\b[^>]*>/gi)) {
        if ((xmlAttribute(element, 'value') ?? '').toLowerCase() === 'true') {
            disabled.add((xmlAttribute(element, 'key') ?? '').toLowerCase());
        }
    }

    const sources = [];
    for (const [element] of xmlSection(text, 'packageSources').matchAll(/<add\b[^>]*>/gi)) {
        const key = xmlAttribute(element, 'key');
        const url = xmlAttribute(element, 'value');
        if (key && url && !disabled.has(key.toLowerCase())) {
            sources.push({ key, url });
        }
    }

    const mapping = new Map();
    for (const match of xmlSection(text, 'packageSourceMapping').matchAll(/<packageSource\b([^>]*)>([\s\S]*?)<\/packageSource>/gi)) {
        const key = xmlAttribute(match[1], 'key');
        if (key) {
            const patterns = [...match[2].matchAll(/<package\b[^>]*>/gi)].map(([element]) => xmlAttribute(element, 'pattern')).filter(Boolean);
            mapping.set(key.toLowerCase(), patterns);
        }
    }

    return { sources, mapping };
}

// Package source mapping precedence
// (https://learn.microsoft.com/nuget/consume-packages/package-source-mapping#package-pattern-precedence):
// an exact package ID beats every prefix pattern, a longer `prefix*` beats a shorter one,
// and every source that declares the winning pattern is eligible. A trailing `*` is the
// only wildcard, so any other `*` is a literal character. Returns -1 when nothing matches.
function nugetPatternSpecificity(pattern, packageId) {
    const id = packageId.toLowerCase();
    const value = pattern.toLowerCase();
    if (value.endsWith('*')) {
        const prefix = value.slice(0, -1);
        return id.startsWith(prefix) ? prefix.length : -1;
    }
    return value === id ? Number.MAX_SAFE_INTEGER : -1;
}

// The sources NuGet would consult for `packageId`. Without package source mapping every
// enabled source is eligible.
function selectNuGetSources(config, packageId) {
    if (config.mapping.size === 0) {
        return config.sources;
    }

    let best = -1;
    let eligible = new Set();
    for (const [key, patterns] of config.mapping) {
        const specificity = Math.max(-1, ...patterns.map(pattern => nugetPatternSpecificity(pattern, packageId)));
        if (specificity > best) {
            best = specificity;
            eligible = new Set([key]);
        } else if (specificity === best && specificity >= 0) {
            eligible.add(key);
        }
    }
    return best < 0 ? [] : config.sources.filter(source => eligible.has(source.key.toLowerCase()));
}

// Resolve the flat-container base URL from a NuGet v3 service index, e.g.
//   { "resources": [{ "@id": "https://.../nuget/v3/flat2/", "@type": "PackageBaseAddress/3.0.0" }] }
async function getPackageBaseAddress(fetchImpl, serviceIndexUrl) {
    const index = await fetchJson(fetchImpl, serviceIndexUrl);
    const resource = (index?.resources ?? []).find(entry => String(entry?.['@type'] ?? '').startsWith('PackageBaseAddress/'));
    const address = resource?.['@id'];
    return typeof address === 'string' ? (address.endsWith('/') ? address : `${address}/`) : null;
}

async function isNuGetVersionAvailable(fetchImpl, nugetConfigText, id, normalizedVersion) {
    // Only the repository's own dnceng feeds count; a mapped source outside them can never
    // make a version "available" for an auto-sec bump.
    const sources = selectNuGetSources(parseNuGetConfig(nugetConfigText), id)
        .filter(source => APPROVED_SOURCE_PREFIXES.some(prefix => source.url.toLowerCase().startsWith(prefix)));
    for (const source of sources) {
        const baseAddress = await getPackageBaseAddress(fetchImpl, source.url);
        if (!baseAddress) {
            continue;
        }
        const versions = await fetchJson(fetchImpl, `${baseAddress}${id}/index.json`);
        if ((versions?.versions ?? []).some(entry => entry.toLowerCase() === normalizedVersion)) {
            return true;
        }
    }
    return false;
}

/**
 * Look up publish date and approved-feed availability for one package version.
 * Returns `{ ecosystem, name, version, published_at, cooldown_satisfied, available_on_approved_feed }`.
 *
 * NuGet availability follows the repository NuGet.config (`nugetConfigText`): only the
 * sources its package source mapping assigns to the package are probed. When no config
 * text is supplied, `available_on_approved_feed` is `null` (unknown). The approval gate
 * passes `checkAvailability: false`, since it needs only the publish date; availability
 * is then `null`.
 */
async function lookupPackageVersion(ecosystem, name, version, { fetchImpl = fetch, now = new Date(), nugetConfigText = null, checkAvailability = true } = {}) {
    let publishedAt = null;
    let available = null;

    switch (ecosystem) {
        case 'npm': {
            const packument = await fetchJson(fetchImpl, `https://registry.npmjs.org/${npmPackagePath(name)}`);
            publishedAt = packument?.time?.[version] ?? null;
            if (checkAvailability) {
                const mirror = await fetchJson(fetchImpl, `${APPROVED_NPM_REGISTRY}${npmPackagePath(name)}`);
                available = Boolean(mirror?.versions?.[version]);
            }
            break;
        }
        case 'pip': {
            // The repository's uv.lock files resolve from PyPI, so PyPI is the approved source.
            const release = await fetchJson(fetchImpl, `https://pypi.org/pypi/${encodeURIComponent(name)}/${encodeURIComponent(version)}/json`);
            const uploads = (release?.urls ?? []).map(file => file.upload_time_iso_8601).filter(Boolean).sort();
            publishedAt = uploads[0] ?? null;
            available = checkAvailability ? release !== null : null;
            break;
        }
        case 'nuget': {
            const id = name.toLowerCase();
            const normalizedVersion = version.toLowerCase();
            const leaf = await fetchJson(fetchImpl, `https://api.nuget.org/v3/registration5-gz-semver2/${id}/${normalizedVersion}.json`);
            publishedAt = leaf?.published ?? null;
            available = checkAvailability && nugetConfigText ? await isNuGetVersionAvailable(fetchImpl, nugetConfigText, id, normalizedVersion) : null;
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
    return requests.slice(0, MAX_EVALUATED_REQUESTS);
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
    const ecosystem = alert.dependency?.package?.ecosystem ?? '';
    const name = alert.dependency?.package?.name ?? '';
    // Keep every range the advisory lists for this package, not just the one matching
    // the installed version, so a target in a later vulnerable interval is rejected.
    const ranges = [
        alert.security_vulnerability?.vulnerable_version_range,
        ...(alert.security_advisory?.vulnerabilities ?? [])
            .filter(vulnerability => vulnerability.package?.ecosystem === ecosystem
                && normalizePackageName(ecosystem, vulnerability.package?.name ?? '') === normalizePackageName(ecosystem, name))
            .map(vulnerability => vulnerability.vulnerable_version_range),
    ].filter(range => typeof range === 'string' && range.trim());
    return {
        number: alert.number,
        ecosystem,
        package: name,
        manifest_path: alert.dependency?.manifest_path ?? '',
        first_patched_version: alert.security_vulnerability?.first_patched_version?.identifier ?? null,
        vulnerable_ranges: [...new Set(ranges)],
        malware: false,
    };
}

async function collectGateInput(github, owner, repo, request, { fetchImpl, now, botLogin, lookupCache = new Map() }) {
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
    // The head text also proves which manifests an update actually touched.
    const sourceChanges = [];
    const headContents = {};
    const versionChangesByKey = new Map();
    const ecosystem = ecosystemFromBranch(pr.head_ref);
    for (const file of files.filter(entry => isAllowedManifest(entry.filename))) {
        const baseText = await getFileText(github, owner, repo, file.filename, pr.base_sha);
        const headText = await getFileText(github, owner, repo, file.filename, pr.head_sha);
        headContents[file.filename] = headText;
        sourceChanges.push({ filename: file.filename, newSources: findNewSources(baseText, headText) });
        if (ecosystem && ecosystem !== 'actions') {
            for (const change of manifestVersionChanges(file.filename, baseText, headText, ecosystem)) {
                const key = `${normalizePackageName(ecosystem, change.name)}@${change.to}`;
                const existing = versionChangesByKey.get(key);
                if (existing) {
                    existing.from = [...new Set([...existing.from, ...change.from])];
                } else {
                    versionChangesByKey.set(key, { ...change, from: [...change.from] });
                }
            }
        }
    }
    const versionChanges = [...versionChangesByKey.values()];

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

    const packageInfo = {};
    if (ecosystem && ecosystem !== 'actions') {
        const targets = parseDependabotUpdates(pr.title, pr.body).map(update => ({ name: update.name, to: update.to }));
        // Past the cap the gate already fails, so skip one registry request per change.
        if (versionChanges.length <= MAX_VERSION_CHANGES) {
            targets.push(...versionChanges);
        }
        for (const { name, to } of targets) {
            const key = `${name}@${to}`;
            if (key in packageInfo) {
                continue;
            }
            const cacheKey = `${ecosystem}:${normalizePackageName(ecosystem, name)}@${to}`;
            try {
                if (!lookupCache.has(cacheKey)) {
                    lookupCache.set(cacheKey, await lookupPackageVersion(ecosystem, name, to, { fetchImpl, now, checkAvailability: false }));
                }
                packageInfo[key] = lookupCache.get(cacheKey);
            } catch {
                // A failed lookup leaves the entry missing, which fails the cooldown gate closed.
            }
        }
    }

    return { pr, expectedHeadSha: request.headSha, files, alerts, checkRuns, statuses, sourceChanges, headContents, versionChanges, packageInfo, reviews, now, botLogin };
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
    const lookupCache = new Map();
    let approvals = 0;

    for (const request of requests) {
        let result;
        if (approvals >= MAX_APPROVALS) {
            results.push({ pr: request.prNumber, decision: 'skip', reasons: ['approval-limit-reached'], fixedAlerts: [] });
            core.info(`#${request.prNumber}: skip approval-limit-reached`);
            continue;
        }
        try {
            const input = await collectGateInput(github, owner, repo, request, { fetchImpl, now, botLogin, lookupCache });
            result = { pr: request.prNumber, ...evaluateApprovalGates(input) };
        } catch (error) {
            result = { pr: request.prNumber, decision: 'skip', reasons: ['gate-evaluation-failed'], fixedAlerts: [] };
            core.warning(`Gate evaluation failed for #${request.prNumber}: ${error.message}`);
        }

        if (result.decision === 'approve' && !staged) {
            // The gates take many requests; Dependabot can rebase meanwhile, and an
            // approval on the older commit could still count for the new head.
            const { data: live } = await github.rest.pulls.get({ owner, repo, pull_number: request.prNumber });
            if (live.head?.sha !== request.headSha) {
                result = { ...result, decision: 'skip', reasons: ['head-sha-mismatch'] };
            }
        }
        if (result.decision === 'approve') {
            approvals++;
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
    // The agent runs this CLI from its checkout, so the repository NuGet.config sits three
    // directories above this file and decides which feeds a NuGet package restores from.
    const nugetConfigPath = require('node:path').resolve(__dirname, '..', '..', '..', 'NuGet.config');
    const nugetConfigText = ecosystem === 'nuget' ? require('node:fs').readFileSync(nugetConfigPath, 'utf8') : null;
    console.log(JSON.stringify(await lookupPackageVersion(ecosystem, name, version, { nugetConfigText })));
}

if (require.main === module) {
    main(process.argv.slice(2)).catch(error => {
        console.error(error.message);
        process.exitCode = 1;
    });
}

module.exports = {
    APPROVED_SOURCE_PREFIXES,
    COOLDOWN_DAYS,
    MAX_APPROVALS,
    MAX_VERSION_CHANGES,
    compareVersions,
    coveredAlerts,
    ecosystemFromBranch,
    evaluateApprovalGates,
    extractSources,
    findNewSources,
    inVulnerableRanges,
    isAllowedManifest,
    isBreakingChange,
    isCooldownSatisfied,
    lookupPackageVersion,
    manifestMentionsVersion,
    manifestPackageVersions,
    manifestVersionChanges,
    normalizePackageName,
    parseDependabotUpdates,
    parseNuGetConfig,
    readApprovalRequests,
    runApprovalJob,
    selectNuGetSources,
};
