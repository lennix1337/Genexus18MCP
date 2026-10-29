const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.join(__dirname, '../..');
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

test('engine documentation keeps fixture results distinct from SDK replacement', () => {
    const section = markdownSection(read('README.md'), '## Experimental `gx-engine` (Rust SDK Replacement Project)');

    assert.doesNotMatch(section, /Overall SDK Replacement:[^\n]*100(?:\.0)?%/);
    assert.doesNotMatch(section, /\*\*>\s*380x faster\*\*/);
    assert.match(section, /Full SDK replacement is not verified/);
    assert.match(section, /64\.19×/);
    assert.match(section, /plans\/131-sdk-performance-benchmark\.md/);
    assert.match(section, /plans\/129-implementation-progress\.md#current-acceptance-status--2026-10-18/);
    assert.match(section, /Historical arithmetic fixture build/);
    assert.match(section, /Different pipelines and targets/);
});

test('engine guidance separates package builds, fixture speed and full-KB coverage', () => {
    const coverage = markdownSection(read('README.md'), '### What builds today');
    assert.match(coverage, /Package build/);
    assert.match(coverage, /Selected-object build/);
    assert.match(coverage, /Full native KB build/);
    assert.match(coverage, /346 generated \/ 346 compiled, 0 skipped, 0 refused/);
    assert.match(coverage, /328\/328 C# files byte-identical/);
    assert.match(coverage, /One oracle KB/);
    assert.match(coverage, /packaged binary.*not benchmarked/);

    const guidance = markdownSection(read('AGENTS.md'), '## gx-engine development');
    assert.match(guidance, /full SDK replacement/);
    assert.match(guidance, /refusals/);
    assert.match(guidance, /Engine\.Backend/);
    assert.match(guidance, /disposable/);
    for (const document of ['src/gx-engine/README.md', 'docs/engine_backend.md', 'plans/131-sdk-performance-benchmark.md']) {
        assert.ok(guidance.includes(`](${document})`), `Missing agent pointer: ${document}`);
        assert.ok(fs.existsSync(path.join(repoRoot, document)), `Missing agent document: ${document}`);
    }

    const artifactSkill = '.agents/skills/gxe-artifact-parity/SKILL.md';
    assert.ok(guidance.includes(`](${artifactSkill})`), 'Missing artifact-parity skill pointer');
    assert.match(read(artifactSkill), /^name: gxe-artifact-parity$/m);
    assert.match(read(artifactSkill), /scripts\/artifact_diff\.py/);
    assert.ok(fs.existsSync(path.join(repoRoot, 'src/gx-engine/scripts/artifact_diff.py')));

    const engineReadme = read('src/gx-engine/README.md');
    const engineCoverage = markdownSection(engineReadme, '## Build coverage and routing');
    assert.match(engineCoverage, /full SDK replacement is not established/);
    assert.doesNotMatch(engineReadme, /Full Object Model:\*\* Complete support|compilation in < 20s/);
    assert.match(engineReadme, /packaged binary was smoke-tested, not benchmarked/);
});

test('engine SQL authentication guidance distinguishes safe preflight fallback from replay', () => {
    const guide = read('docs/engine_backend.md');
    const auth = markdownSection(guide, '## SQL authentication (issue #635)');
    assert.match(auth, /SQLCMDUSER/);
    assert.match(auth, /SQLCMDPASSWORD/);
    assert.match(auth, /private credential store/);
    assert.match(auth, /exit \*\*5\*\*/);
    assert.match(auth, /sql_credentials_unavailable/);
    assert.match(auth, /EngineSqlCredentialsUnavailable/);
    assert.match(auth, /must not\s+trigger automatic SDK build replay/);
    const auto = markdownSection(guide, '### Exactly what `auto` routes');
    assert.match(auto, /compile_check callers=false includeCallees=transitive/);
    assert.match(auto, /Whole-KB/);
    assert.match(auto, /estimated_seconds/);
    assert.match(auto, /Read, query, edit/);
});

test('engine capability guidance does not duplicate synthetic parity claims', () => {
    const section = markdownSection(read('README.md'), '#### Gateway Capability Registry Gates');
    assert.match(section, /\[current capability table\]\(docs\/engine_backend\.md\)/);
    assert.doesNotMatch(section, /\|\s*`[^`]+`[^\n]*\*\*Parity\*\*/);
    assert.match(section, /deferral guard is not native implementation coverage/);
    assert.match(section, /historical, bounded evidence/);
    assert.match(section, /plans\/132-gxengine-full-parity\.md/);
});

test('engine default policy is gxengine and preserves auto/SDK opt-out and reconciliation', () => {
    const section = markdownSection(read('README.md'), '### Activation Flag & Safety Seam');
    assert.match(section, /defaults to `"gxengine"`/);
    assert.match(section, /async\/other directed builds/);
    assert.match(section, /mode=compile_check/);
    assert.match(section, /callers=false/);
    const backend = read('docs/engine_backend.md');
    assert.match(backend, /engine\.scope=target-closure/);
    assert.match(backend, /successful target build does not imply whole-KB success/);
    assert.match(backend, /default `callers=true`/);
    assert.match(section, /`"auto"` routes only builds/);
    assert.match(section, /`"sdk"` keeps every operation on the SDK/);
    assert.match(section, /never trigger automatic SDK replay/);
    assert.match(section, /EngineCapabilityNotParity.*evidence-gate rejection/);
    assert.match(section, /EngineOperationOutsideAdmittedSpace.*read\/edit deferrals/);
    for (const document of ['src/gx-engine/README.md', 'docs/engine_backend.md']) {
        const text = read(document);
        assert.match(text, /planning skips.*Partial|Partial when skipped during planning/);
        assert.match(text, /checked-emission errors.*Failed/);
    }
    assert.match(section, /admitted type-only `genexus_query` calls/);
    assert.match(section, /all reads/);
    assert.match(backend, /No read shape is currently admitted/);
    assert.match(backend, /all object-name\/text searches \(including\s+`exactMatch:true`\)/);
    assert.match(read('docs/engine_backend.md'), /`Engine\.Backend=gxengine` \(also when the section is absent\)/);
    assert.match(read('src/gx-engine/README.md'), /defaults to `Engine\.Backend=gxengine`/);
    assert.match(read('AGENTS.md'), /`Engine\.Backend` defaults to `gxengine`/);
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

test('explain documentation advertises compatibility-only contract', () => {
    for (const docPath of ['README.md', 'GEMINI.md', '.gemini/skills/genexus-mastery/SKILL.md']) {
        const text = read(docPath);
        assert.match(text, /compatibility-only/i);
        assert.match(text, /NotImplemented/);
        assert.match(text, /genexus_read/i);
    }
});

test('issue 146 documentation publishes context lease and HTTP token contract', () => {
    for (const docPath of ['docs/kb-isolation-contract.md', 'docs/llm_cli_mcp_playbook.md', 'docs/mcp_capabilities_inventory.md']) {
        const text = read(docPath);
        assert.match(text, /local-friendly/i);
        assert.match(text, /hardened/i);
        assert.match(text, /ResolutionPolicy/);
        assert.match(text, /KB_NOT_OWNED/);
        assert.match(text, /KB_LOCKED/);
        assert.match(text, /KB_LEASE_INVALID/);
        assert.match(text, /KB_LEASE_EXPIRED/);
        assert.match(text, /GXMCP_HTTP_TOKEN/);
        assert.match(text, /select/i);
        assert.match(text, /close/i);
    }
});

test('agent guide links canonical lanes and runs build script last in coding standards', () => {
    const guide = read('AGENTS.md');
    assert.match(guide, /\(CODING_STANDARDS\.md#validation-lanes\)/);
    assert.match(guide, /Run `\.\\build\.ps1` \*\*last\*\*/);

    const standards = read('CODING_STANDARDS.md');
    assert.match(standards, /generate-operation-contract-inventory\.py/);
    assert.match(standards, /nexus-ide/i);

    // Verify build.ps1 is in CODING_STANDARDS.md
    assert.match(standards, /`?\.\\build\.ps1`?/);
});
