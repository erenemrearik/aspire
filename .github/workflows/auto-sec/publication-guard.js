// gh-aw v0.89.17 unconditionally emits always() for post-agent publication steps.
// Apply this pass after compilation until the compiler supports a scrub-success guard:
// https://github.com/github/gh-aw/blob/v0.89.17/pkg/workflow/compiler_yaml_post_agent.go
const fs = require('node:fs');
const path = require('node:path');

const PUBLICATION_GUARD = "steps.auto_sec_scrub.outcome == 'success' && steps.auto_sec_scrub.outputs.publication_ready == 'true'";

function guardPublications(workflow) {
    // Generated jobs have two-space indentation, steps six, and step properties eight:
    //   agent:
    //     steps:
    //       - name: Scrub auto-sec agent transcript and outputs
    //         id: auto_sec_scrub
    //         if: always()
    // Match only these structural lines, never the indented shell/JavaScript bodies.
    const job = /^  agent:\r?\n([\s\S]*?)(?=^  [A-Za-z_][\w-]*:|(?![\s\S]))/m.exec(workflow);
    if (!job) {
        throw new Error('auto-sec publication guard: missing agent job');
    }
    const steps = [...job[0].matchAll(/^      - [^\r\n]+[\s\S]*?(?=^      - |$(?![\s\S]))/gm)];
    const scrubs = steps.filter(step => /^        id: auto_sec_scrub\r?$/m.test(step[0]));
    if (scrubs.length !== 1) {
        throw new Error('auto-sec publication guard: expected one scrub step');
    }
    const scrubIndex = steps.indexOf(scrubs[0]);
    for (const name of ['Append agent step summary', 'Ingest agent output', 'Upload agent output fallback artifact', 'Upload agent artifacts']) {
        const matches = steps.filter(step => step[0].startsWith(`      - name: ${name}\n`) || step[0].startsWith(`      - name: ${name}\r\n`));
        if (matches.length !== 1 || steps.indexOf(matches[0]) <= scrubIndex) {
            throw new Error(`auto-sec publication guard: expected ${name} after scrub`);
        }
    }

    let guardedJob = job[0];
    // Guard every remaining step, not just today's known publishers. A new generated
    // summary or upload must not bypass the boundary on a compiler upgrade.
    for (const step of steps.slice(scrubIndex + 1).reverse()) {
        const condition = /^        if: ([^\r\n]+)\r?$/m.exec(step[0]);
        let guardedStep;
        if (condition) {
            if (condition[1].endsWith(` && ${PUBLICATION_GUARD}`)) {
                continue;
            }
            guardedStep = step[0].replace(condition[0], `        if: (${condition[1]}) && ${PUBLICATION_GUARD}${condition[0].endsWith('\r') ? '\r' : ''}`);
        } else {
            const newline = step[0].includes('\r\n') ? '\r\n' : '\n';
            guardedStep = step[0].replace(newline, `${newline}        if: (success()) && ${PUBLICATION_GUARD}${newline}`);
        }
        guardedJob = guardedJob.slice(0, step.index) + guardedStep + guardedJob.slice(step.index + step[0].length);
    }
    return workflow.slice(0, job.index) + guardedJob + workflow.slice(job.index + job[0].length);
}

if (require.main === module) {
    const filename = path.join(__dirname, '..', 'auto-sec.lock.yml');
    fs.writeFileSync(filename, guardPublications(fs.readFileSync(filename, 'utf8')));
}

module.exports = { guardPublications, PUBLICATION_GUARD };
