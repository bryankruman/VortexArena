# Perf baselines

Known-good `tools/perf-report.py --json` summaries, one per (map, build-flavor), used by
`tools/perf-run.ps1 -Baseline …` and `tools/perf-smoke.ps1 -Live` to answer "is this run worse
than known-good?".

Regenerate on a known-good **release export**:

```powershell
tools\perf-run.ps1 -Label baseline -Map catharsis
Copy-Item _scratch\perf_baseline.json tools\perf-baselines\catharsis-release.json
```

Naming: `<map>-release.json`. Commit updates deliberately (a baseline change is a claim that the
new numbers are the accepted normal), and note the reason in the commit message.
