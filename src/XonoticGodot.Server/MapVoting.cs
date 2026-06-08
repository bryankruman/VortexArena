using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// One candidate in the end-of-match map vote — the C# successor to the parallel QC arrays
/// <c>mapvote_maps[i]</c> / <c>mapvote_selections[i]</c> / <c>mapvote_maps_flags[i]</c>
/// (server/mapvoting.qc). Collapsed into a small object so a candidate carries its own tally.
/// </summary>
public sealed class MapVoteCandidate
{
    /// <summary>The map name on the ballot (QC <c>mapvote_maps[i]</c>).</summary>
    public string MapName { get; }

    /// <summary>Whether this option can still win (QC <c>GTV_AVAILABLE</c> in mapvote_maps_flags).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Running vote count (QC <c>mapvote_selections[i]</c>).</summary>
    public int Votes { get; internal set; }

    public MapVoteCandidate(string mapName) => MapName = mapName;
}

/// <summary>
/// A minimal end-of-match map vote — the structure + core of server/mapvoting.qc
/// (<c>MapVote_Init</c> / <c>MapVote_AddVotable</c> / the per-client tally in <c>MapVote_Tick</c> /
/// <c>MapVote_CheckRules</c> / <c>MapVote_Finished</c>). After intermission the host builds the ballot from
/// the map rotation, players cast one vote each (by candidate index, QC the client's <c>.mapvote</c> set
/// from impulses), and the winner is the most-voted available candidate — ties broken deterministically by
/// ballot order, then resolved by the seeded RNG if still tied (QC RandomSelection among the leaders).
///
/// Now modeled too: abstain handling (<see cref="Abstain"/>, QC the abstain ballot slot), and the
/// "most votes can't be beaten" early finish (<see cref="Tick"/> closes the vote once the leader is
/// mathematically unbeatable by the remaining unvoted ballots).
///
/// Deferred: the network ballot UI (TempEntity MapVote), gametype voting (GameTypeVote), suggestion
/// entries, and the full per-client impulse plumbing — votes are cast here through <see cref="CastVote"/>.
/// </summary>
public sealed class MapVoting
{
    private readonly List<MapVoteCandidate> _candidates = new();
    private readonly Dictionary<object, int> _votes = new(); // voter key -> candidate index
    private readonly HashSet<object> _abstained = new();     // voters who explicitly abstained
    private Random _rng;

    /// <summary>True once <see cref="Start"/> built a ballot and the vote is accepting votes (QC <c>mapvote_run</c>).</summary>
    public bool Running { get; private set; }

    /// <summary>True once <see cref="Finish"/> picked a winner (QC <c>alreadychangedlevel</c>).</summary>
    public bool Finished { get; private set; }

    /// <summary>The chosen map after <see cref="Finish"/> (QC MapVote_Winner), or "" while the vote runs.</summary>
    public string WinningMap { get; private set; } = "";

    /// <summary>Absolute sim time the vote auto-closes (QC <c>mapvote_timeout</c>); 0 = no timeout.</summary>
    public float Timeout { get; private set; }

    /// <summary>The ballot (read-only) — each option with its live tally.</summary>
    public IReadOnlyList<MapVoteCandidate> Candidates => _candidates;

    /// <summary>How many distinct voters have cast a vote so far (QC the sum of mapvote_selections).</summary>
    public int VotesCast => _votes.Count;

    /// <summary>Number of voters expected (QC <c>mapvote_voters</c>), set at <see cref="Start"/> — drives the early finish.</summary>
    public int ExpectedVoters { get; private set; }

    public MapVoting(int seed = 0x5EED) => _rng = new Random(seed);

    /// <summary>Reseed the tiebreak RNG (determinism support for headless runs).</summary>
    public void Reseed(int seed) => _rng = new Random(seed);

    /// <summary>
    /// QC <c>MapVote_Init</c> + <c>MapVote_AddVotable</c>: build the ballot from <paramref name="mapNames"/>
    /// (deduped, ballot order preserved), record how many voters to expect, and (optionally) arm a timeout.
    /// A vote with 0 or 1 candidate finishes immediately to the only/empty option.
    /// </summary>
    public void Start(IEnumerable<string> mapNames, int expectedVoters, float voteDurationSeconds = 0f)
    {
        _candidates.Clear();
        _votes.Clear();
        _abstained.Clear();
        Finished = false;
        WinningMap = "";

        foreach (string name in mapNames)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            bool dup = false;
            for (int i = 0; i < _candidates.Count; i++)
                if (string.Equals(_candidates[i].MapName, name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup)
                _candidates.Add(new MapVoteCandidate(name));
        }

        ExpectedVoters = System.Math.Max(expectedVoters, 0);
        Running = true;
        Timeout = voteDurationSeconds > 0f ? Now + voteDurationSeconds : 0f;

        // Degenerate ballots finish straight away (QC: a single votable map is chosen without a vote).
        if (_candidates.Count <= 1)
            Finish();
    }

    /// <summary>
    /// Cast (or change) <paramref name="voter"/>'s vote to candidate <paramref name="candidateIndex"/>
    /// (QC client <c>.mapvote</c> set from an impulse). Out-of-range or unavailable candidates are ignored
    /// (QC clears invalid votes). The <paramref name="voter"/> key is any stable per-voter object (the
    /// <see cref="Common.Gameplay.Player"/>). Returns true if the vote was recorded.
    /// </summary>
    public bool CastVote(object voter, int candidateIndex)
    {
        if (!Running || Finished)
            return false;
        if (candidateIndex < 0 || candidateIndex >= _candidates.Count)
            return false;
        if (!_candidates[candidateIndex].Available)
            return false;

        // move the voter's tally from any previous choice to the new one (clearing any prior abstain).
        _abstained.Remove(voter);
        if (_votes.TryGetValue(voter, out int prev) && prev != candidateIndex)
            _candidates[prev].Votes--;
        _votes[voter] = candidateIndex;
        // recount from the map (cheap, and avoids double counting on a repeat of the same choice).
        RecountFromBallots();
        return true;
    }

    /// <summary>
    /// QC the abstain ballot (g_maplist_votable_abstain): <paramref name="voter"/> declines to pick a map.
    /// An abstaining voter still counts toward "everyone voted" (so the vote can finish) but adds no tally to
    /// any candidate. Clears any prior map choice by this voter. Returns true while the vote is open.
    /// </summary>
    public bool Abstain(object voter)
    {
        if (!Running || Finished)
            return false;
        if (_votes.Remove(voter))
            RecountFromBallots();
        _abstained.Add(voter);
        return true;
    }

    /// <summary>Withdraw a voter (e.g. on disconnect) so their vote no longer counts.</summary>
    public void RemoveVoter(object voter)
    {
        bool changed = _votes.Remove(voter);
        changed |= _abstained.Remove(voter);
        if (changed)
            RecountFromBallots();
    }

    /// <summary>
    /// Drive the vote one frame (QC <c>MapVote_Tick</c> → <c>MapVote_CheckRules_decide</c>). Finishes the
    /// vote when every expected voter has voted, or the timeout elapses. No-op once finished/not running.
    /// </summary>
    public void Tick()
    {
        if (!Running || Finished)
            return;

        // everyone voted (including abstainers) → decide now (QC: if mapvote_voters and all counted, finish).
        int ballots = _votes.Count + _abstained.Count;
        if (ExpectedVoters > 0 && ballots >= ExpectedVoters)
        {
            Finish();
            return;
        }

        // QC the "most votes can't be beaten" early finish: if the leader's lead exceeds the number of
        // ballots still outstanding, no remaining vote can change the winner, so close the vote now.
        if (ExpectedVoters > 0 && LeaderIsUnbeatable(ExpectedVoters - ballots))
        {
            Finish();
            return;
        }

        if (Timeout > 0f && Now >= Timeout)
            Finish();
    }

    /// <summary>
    /// True when the leading available candidate's margin over the runner-up exceeds <paramref name="remainingBallots"/>,
    /// so no outstanding vote can overturn it (QC the early-finish check in MapVote_CheckRules_two).
    /// </summary>
    private bool LeaderIsUnbeatable(int remainingBallots)
    {
        if (remainingBallots <= 0)
            return true;
        int best = -1, second = -1;
        for (int i = 0; i < _candidates.Count; i++)
        {
            if (!_candidates[i].Available) continue;
            int v = _candidates[i].Votes;
            if (v > best) { second = best; best = v; }
            else if (v > second) { second = v; }
        }
        if (best < 0) return false;
        if (second < 0) second = 0;
        return (best - second) > remainingBallots;
    }

    /// <summary>
    /// Close the vote and pick the winner (QC <c>MapVote_Finished</c> → <c>MapVote_Winner</c>): the available
    /// candidate with the most votes, ties broken by ballot order then by the seeded RNG among the leaders
    /// (QC RandomSelection). Sets <see cref="WinningMap"/> and <see cref="Finished"/>. Safe to call directly.
    /// </summary>
    public void Finish()
    {
        if (Finished)
            return;
        Finished = true;
        Running = false;

        MapVoteCandidate? winner = null;
        int bestVotes = -1;
        int leadersSeen = 0;

        for (int i = 0; i < _candidates.Count; i++)
        {
            MapVoteCandidate c = _candidates[i];
            if (!c.Available)
                continue;

            if (c.Votes > bestVotes)
            {
                bestVotes = c.Votes;
                winner = c;
                leadersSeen = 1;
            }
            else if (c.Votes == bestVotes)
            {
                // RandomSelection reservoir: keep this tied leader with probability 1/leadersSeen.
                leadersSeen++;
                if (_rng.Next(leadersSeen) == 0)
                    winner = c;
            }
        }

        WinningMap = winner?.MapName ?? "";
    }

    /// <summary>Mark a candidate unavailable (QC clearing GTV_AVAILABLE), e.g. the map that just played.</summary>
    public void Disqualify(string mapName)
    {
        for (int i = 0; i < _candidates.Count; i++)
            if (string.Equals(_candidates[i].MapName, mapName, StringComparison.OrdinalIgnoreCase))
                _candidates[i].Available = false;
    }

    private void RecountFromBallots()
    {
        for (int i = 0; i < _candidates.Count; i++)
            _candidates[i].Votes = 0;
        foreach (int idx in _votes.Values)
            if (idx >= 0 && idx < _candidates.Count)
                _candidates[idx].Votes++;
    }

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
