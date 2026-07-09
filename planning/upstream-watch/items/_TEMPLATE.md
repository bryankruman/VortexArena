<!-- Copy to items/UW-####-<slug>.md for any contribution that needs more than a ledger line.
     Delete these comments. Keep it tight — this is a decision aid, not an essay. -->

# UW-#### — <short feature name>

- **Source:** `<repo>@<sha>` or `<repo>:<branch>@<tip>`  ·  <gitlab url>
- **Author / date:** <author> · <ISO date landed or last-updated>
- **Kind:** qc-gameplay | data-cfg | asset | dp-engine | build/i18n/ci
- **Upstream state:** merged to master | open MR (!####) | unmerged branch | draft/WIP
- **Base symbols touched:** `<path>:<symbol>`, …
- **Port-worthiness:** high | medium | low | none
- **Decision:** pending | port | adapt | ported | defer | reject | n/a

## What it does / how it works
<One paragraph from reading the diff. The actual mechanism, not the commit title.>

## Portability
<Which layer; how it maps onto src/** or data; what our loaders/netcode need to support it.>

## Completeness (upstream)
<Finished vs half-built. Tests? Follow-up commits? Review state if an MR.>

## Quality
<Clean vs hacky; matches surrounding Base style; obvious edge cases handled.>

## Roadmap / design alignment
<Serves Vortex Arena goals? Conflicts with an intended_divergence? Cross-link planning/ docs.>

## Effort & risk
<S/M/L. What it interacts with (netcode, determinism, hot paths). Any regression risk vs a
closed bug — cite the memory/postmortem if so.>

## Recommendation
<One or two sentences: the auditor's proposed decision and why. Bryan makes the final call.>
