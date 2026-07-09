export const meta = {
  name: 'upstream-watch',
  description: 'Analyze a harvested upstream worklist (new master commits + branches/MRs from the Xonotic data + DarkPlaces repos) against the upstream-watch rubric, assign UW-#### ids, and append ledger rows + per-item deep dives. Human sets the final port/reject decision.',
  whenToUse: 'Weekly, after running `python tools/upstream-watch.py`. args: { worklist?: "planning/upstream-watch/_inbox/worklist-<date>.json" }',
  phases: [
    { title: 'Load', detail: 'read the worklist + current ledger high-water UW id' },
    { title: 'Analyze', detail: 'one analyst per non-noise candidate: apply the rubric from the real diff' },
    { title: 'Write', detail: 'scribe inserts pre-built ledger rows + writes deep-dive docs' },
  ],
}

const PORT = 'C:/Users/Bryan/Projects/Xonotic/XonoticGodot'
const WATCH = `${PORT}/planning/upstream-watch`
const DATA = 'C:/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir'
const DP = 'C:/Users/Bryan/Projects/Xonotic/Base/darkplaces'
const pad = n => `UW-${String(n).padStart(4, '0')}`

const worklist = (args && args.worklist)
  ? (args.worklist.startsWith('/') || /^[A-Za-z]:/.test(args.worklist) ? args.worklist : `${PORT}/${args.worklist}`)
  : null

log(`upstream-watch analysis: worklist=${worklist || '(latest in _inbox)'}`)

// ---- Load: worklist candidates + ledger high-water mark ---------------------
const LOAD_SCHEMA = {
  type: 'object', required: ['next_uw', 'harvest_date', 'candidates'], additionalProperties: false,
  properties: {
    next_uw: { type: 'integer' },        // highest UW-#### in LEDGER.md + 1 (or 1 if none)
    harvest_date: { type: 'string' },    // the worklist JSON's "generated" field (YYYY-MM-DD)
    candidates: { type: 'array', items: {
      type: 'object', required: ['source', 'repo', 'type', 'title'], additionalProperties: false,
      properties: {
        source: { type: 'string' },                       // dedup key, e.g. data@a1b2c3 or dp:branch@tip
        repo: { type: 'string', enum: ['data', 'dp'] },
        type: { type: 'string', enum: ['commit', 'branch'] },
        ref: { type: 'string' },                          // sha, or branch name to diff over master
        title: { type: 'string' },
        noise: { type: 'boolean' },                       // pre-flagged i18n/ci/chore
        open_mr: { type: 'boolean' },
      } } },
  },
}
const loadPrompt = `Read ${worklist ? `the worklist JSON at ${worklist}` :
  `the most recent planning/upstream-watch/_inbox/worklist-*.json file`} and read
planning/upstream-watch/LEDGER.md. Return: next_uw = (highest UW-#### integer in the ledger table)
+ 1, or 1 if the table has no data rows yet. harvest_date = the worklist JSON's top-level
"generated" field. And candidates = one entry per master commit
(type "commit", ref=sha from the source key after '@') and per branch/MR (type "branch", ref=branch
name), carrying source, repo, title (commit subject or MR title), noise flag, and open_mr. Do not
analyze anything yet — just enumerate faithfully and completely.`

const loaded = await agent(loadPrompt, { label: 'load', phase: 'Load', agentType: 'Explore', schema: LOAD_SCHEMA })
let candidates = (loaded && loaded.candidates) || []
const baseUw = (loaded && loaded.next_uw) || 1
if (!candidates.length) { log('No candidates in worklist — nothing to analyze.'); return { analyzed: 0 } }

// Deterministic, stable id assignment: worklist order. No cross-agent race on the counter.
candidates = candidates.map((c, i) => ({ ...c, uw: pad(baseUw + i) }))
const noise = candidates.filter(c => c.noise && c.type === 'commit' && !c.open_mr)
const real = candidates.filter(c => !(c.noise && c.type === 'commit' && !c.open_mr))
log(`${candidates.length} candidate(s): ${real.length} to analyze, ${noise.length} noise→n/a mechanically. Ids ${pad(baseUw)}..${pad(baseUw + candidates.length - 1)}.`)

// ---- Analyze: one analyst per real candidate (rubric from the actual diff) ---
const ANALYSIS_SCHEMA = {
  type: 'object',
  required: ['uw', 'source', 'slug', 'kind', 'summary', 'portability', 'completeness',
             'quality', 'alignment', 'effort', 'worth', 'proposed_decision', 'recommendation', 'needs_deepdive'],
  additionalProperties: false,
  properties: {
    uw: { type: 'string' }, source: { type: 'string' }, slug: { type: 'string' },
    kind: { type: 'string', enum: ['qc-gameplay', 'data-cfg', 'asset', 'dp-engine', 'build/i18n/ci'] },
    base_symbols: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' }, portability: { type: 'string' }, completeness: { type: 'string' },
    quality: { type: 'string' }, alignment: { type: 'string' },
    effort: { type: 'string', enum: ['S', 'M', 'L'] },
    worth: { type: 'string', enum: ['high', 'medium', 'low', 'none'] },
    proposed_decision: { type: 'string', enum: ['pending', 'port', 'adapt', 'defer', 'reject', 'n/a'] },
    recommendation: { type: 'string' }, needs_deepdive: { type: 'boolean' },
  },
}
function analyzePrompt(c) {
  const repoPath = c.repo === 'dp' ? DP : DATA
  const how = c.type === 'commit'
    ? `Read the diff: \`git -C ${repoPath} show --stat ${c.ref}\` then \`git -C ${repoPath} show ${c.ref}\`.`
    : `Read what the branch adds over master: \`git -C ${repoPath} log --oneline --stat origin/master..origin/${c.ref}\` then \`git -C ${repoPath} diff origin/master...origin/${c.ref}\`.`
  return `Triage upstream Xonotic contribution ${c.uw} for the Vortex Arena port (a C#/Godot
reimplementation of Xonotic). Source ${c.source} (repo=${c.repo}, ${c.type}${c.open_mr ? ', OPEN MR' : ''}).
Title: "${c.title}".

${how} Read the ACTUAL diff, not just the title. Apply the rubric in
planning/upstream-watch/README.md §5 (read it if unsure). Return the structured analysis with
uw="${c.uw}", source="${c.source}", and:
- summary: what it does / how it works, from the diff. Name Base files/symbols touched (base_symbols).
- kind: qc-gameplay (qcsrc/**) · data-cfg (cfg/effectinfo/notifications) · asset · dp-engine
  (DarkPlaces C — usually n/a since Godot replaces DP, EXCEPT protocol/physics/collision/asset-format
  behavior, or a security/parser fix, which we DO mirror) · build/i18n/ci (usually n/a).
- portability, completeness (merged vs WIP/draft; tests?), quality, alignment (serves Vortex Arena or
  upstream-only churn / conflicts with an existing intended_divergence — grep planning/ if unsure).
- effort S/M/L; worth high/medium/low/none; proposed_decision (default "pending" for real gameplay
  impact — Bryan decides; "n/a" for translations/CI/engine-internal; "reject" only with a clear reason;
  "defer" for stale drafts worth revisiting).
- needs_deepdive: true only if non-trivial (real gameplay/feature/subsystem) — those get a full doc;
  small fixes/noise get a one-line ledger row (false). slug: short kebab-case name.
Be concise; these become ledger cells + a short doc.`
}

const analyses = (await parallel(real.map(c => () =>
  agent(analyzePrompt(c), { label: `${c.uw}:${c.repo}`, phase: 'Analyze', agentType: 'Explore', schema: ANALYSIS_SCHEMA })
    .then(a => a ? { ...a, uw: c.uw, source: a.source || c.source, _title: c.title } : null)
))).filter(Boolean)

// Mechanical n/a rows for pre-flagged noise (translations/CI) — no agent spent.
const noiseRows = noise.map(c => ({
  uw: c.uw, source: c.source, kind: 'build/i18n/ci', summary: c.title.slice(0, 100),
  worth: 'none', proposed_decision: 'n/a', effort: 'S', needs_deepdive: false, recommendation: 'noise (i18n/ci/chore)',
}))
const all = [...analyses, ...noiseRows].sort((a, b) => a.uw.localeCompare(b.uw))
log(`Analyzed ${analyses.length}; +${noiseRows.length} mechanical noise rows. Building ${all.length} ledger rows.`)

// ---- Build ledger rows + deep-dive docs deterministically in JS -------------
// Stacked 3-col layout (Contribution / Worth / Decision) so it renders without
// horizontal scroll on GitHub. Contribution cell = bold id+summary / source+kind / recommendation.
const esc = s => String(s || '').replace(/\|/g, '\\|').replace(/<br\s*\/?>/gi, ' ').replace(/\n+/g, ' ').trim()
const clip = (s, n) => { s = esc(s); return s.length <= n ? s : s.slice(0, n).replace(/\s+\S*$/, '').replace(/[ —\-,:;(]+$/, '') + '…' }
const WORTH = { high: '🟢 High', medium: '🟡 Medium', low: '🟠 Low', none: '⚪ None' }
const DEC = { pending: '⏳ Pending', port: '✅ Port', adapt: '🔧 Adapt', ported: '📦 Ported', defer: '⏸️ Defer', reject: '❌ Reject', 'n/a': '➖ N/A' }
const ledgerRows = all.map(a => {
  const eff = a.effort && a.effort !== '?' ? ` _(effort ${a.effort})_` : ''
  let contrib = `**${a.uw} · ${clip(a.summary, 160)}**<br>\`${esc(a.source)}\` · ${a.kind}${eff}<br>${clip(a.recommendation, 160)}`
  if (a.needs_deepdive) contrib += ` · [deep dive](items/${a.uw}-${a.slug}.md)`
  return `| ${contrib} | ${WORTH[a.worth] || a.worth} | ${DEC[a.proposed_decision] || a.proposed_decision} |`
}).join('\n')

const deepDives = analyses.filter(a => a.needs_deepdive).map(a => ({
  path: `${WATCH}/items/${a.uw}-${a.slug}.md`,
  content: `# ${a.uw} — ${esc(a._title)}

- **Source:** \`${a.source}\`
- **Kind:** ${a.kind}
- **Base symbols touched:** ${(a.base_symbols || []).map(s => '`' + s + '`').join(', ') || '—'}
- **Port-worthiness:** ${a.worth}  ·  **Effort:** ${a.effort}
- **Decision:** ${a.proposed_decision}

## What it does / how it works
${a.summary}

## Portability
${a.portability}

## Completeness (upstream)
${a.completeness}

## Quality
${a.quality}

## Roadmap / design alignment
${a.alignment}

## Recommendation
${a.recommendation}
`,
}))

// ---- Write: one scribe does the mechanical inserts (rows pre-built) ----------
const WRITE_SCHEMA = {
  type: 'object', required: ['rows_inserted', 'docs_written'], additionalProperties: false,
  properties: { rows_inserted: { type: 'integer' }, docs_written: { type: 'integer' } },
}
const scribePrompt = `Do two mechanical writes — do NOT rewrite or re-reason the content, insert it verbatim.

1) In ${WATCH}/LEDGER.md, insert the following ${all.length} pre-built table row(s) into the ledger
table (immediately after the header separator row, keeping UW ids ascending), and DELETE the
"_(none yet …)_" placeholder row if present. Do not alter existing rows. The rows:

${ledgerRows}

2) Write these ${deepDives.length} deep-dive file(s) verbatim (create each path with the given content):

${deepDives.map(d => `--- FILE: ${d.path} ---\n${d.content}`).join('\n\n')}

Return rows_inserted and docs_written counts.`

const written = await agent(scribePrompt, { label: 'scribe', phase: 'Write', schema: WRITE_SCHEMA })
log(`Done: ${written ? written.rows_inserted : 0} ledger row(s), ${written ? written.docs_written : 0} deep-dive doc(s). All Decision=pending/n-a until Bryan rules.`)
return { analyzed: analyses.length, noise: noiseRows.length, ...written, id_range: `${pad(baseUw)}..${pad(baseUw + candidates.length - 1)}` }
