const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.join(__dirname, '..');
const read = (file) => fs.readFileSync(path.join(repoRoot, file), 'utf8');

function requiredNodeMajor() {
    const range = JSON.parse(read('package.json')).engines.node;
    const match = range.match(/>=([0-9]+)/);
    assert.ok(match, `Unable to determine Node.js minimum from engines.node: ${range}`);
    return match[1];
}

// Returns the body of one ATX section, addressed by its exact heading line so
// that editing prose above it cannot silently change what is asserted. The
// heading must appear exactly once, and the section ends at the next heading of
// the same or higher rank. Lines inside fenced code blocks are never treated as
// headings or as section boundaries.
function markdownSection(markdown, headingLine) {
    const lines = markdown.split(/\r?\n/);
    const fenced = [];
    let fence = null;
    for (const line of lines) {
        const opening = /^ {0,3}(`{3,}|~{3,})/.exec(line);
        if (opening) fence = fence === null ? opening[1][0] : null;
        fenced.push(fence !== null);
    }

    const starts = [];
    lines.forEach((line, index) => {
        if (!fenced[index] && line.trim() === headingLine) starts.push(index);
    });
    assert.equal(starts.length, 1, `Expected exactly one "${headingLine}" heading, found ${starts.length}`);

    const level = /^#+/.exec(headingLine)[0].length;
    let end = lines.length;
    for (let index = starts[0] + 1; index < lines.length; index++) {
        if (fenced[index]) continue;
        const heading = /^(#{1,6})\s+\S/.exec(lines[index].trim());
        if (heading && heading[1].length <= level) {
            end = index;
            break;
        }
    }
    return lines.slice(starts[0] + 1, end).join('\n');
}

test('onboarding docs stay aligned with package prerequisites', () => {
    const nodeMajor = requiredNodeMajor();
    const readme = read('README.md');
    const spanish = read('docs/GETTING_STARTED.es.md');
    const portuguese = read('docs/GETTING_STARTED.pt-br.md');

    assert.match(readme, new RegExp(`Node\\.js ${nodeMajor}\\+`));
    assert.match(spanish, new RegExp(`Node\\.js ${nodeMajor} o superior`));
    assert.match(portuguese, new RegExp(`Node\\.js ${nodeMajor} ou superior`));
    assert.doesNotMatch(readme, /setup\.bat/);
    assert.match(readme, /`\.\\build\.ps1`/);
    assert.match(readme, /\*\*Windows\*\* \(GeneXus is Windows-only\)/);
    assert.match(readme, /GeneXus 18.*installed locally/);
});

test('the README release section describes the release flow that actually ships', () => {
    const section = markdownSection(read('README.md'), '### Automated release');

    // The workflow triggers on the published release event and only verifies the
    // release `release.ps1` already created; it never runs on a push, and it never
    // creates the tag or the release.
    assert.doesNotMatch(section, /NPM_TOKEN/);
    assert.match(section, /`release: \[published\]`/);

    // npm authentication is OIDC Trusted Publishing, so there is no token secret.
    assert.match(section, /OIDC Trusted Publishing/);
});

test('the docs index lists every top-level document and every link resolves', () => {
    const docsDir = path.join(repoRoot, 'docs');
    const index = read('docs/README.md');
    const targets = new Set(
        [...index.matchAll(/\]\(([^)#\s]+)(?:#[^)]*)?\)/g)]
            .map((match) => match[1])
            .filter((target) => !/^[a-z]+:/i.test(target))
    );

    const unlisted = fs.readdirSync(docsDir)
        .filter((name) => name.endsWith('.md') && name !== 'README.md')
        .filter((name) => !targets.has(name));
    assert.deepEqual(unlisted, [], 'Add these documents to docs/README.md');

    const broken = [...targets].filter((target) => !fs.existsSync(path.join(docsDir, target)));
    assert.deepEqual(broken, [], 'docs/README.md links to missing files');

    assert.match(read('README.md'), /\]\(docs\/README\.md\)/);
    assert.match(read('AGENTS.md'), /\]\(docs\/README\.md\)/);
});
