using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Mod-icons panel — port of Base/.../qcsrc/client/hud/panel/modicons.qc (HUD panel #10) and the per-gametype
/// draw functions it dispatches to (<c>HUD_Mod_CTF</c>, <c>HUD_Mod_KH</c>, <c>HUD_Mod_Dom</c>, ... in
/// common/gametypes/gametype/*/cl_*.qc). The QC version drew the team-objective status for the active
/// gametype: CTF flag states (taken / lost / carrying / shielded, per team) decoded from
/// <c>STAT(OBJECTIVE_STATUS)</c>; Keyhunt key slots; Domination point-per-second bars; etc. Which renderer
/// runs is chosen at gametype-init (<c>HUD_ModIcons_SetFunc</c> -> <c>gametype.m_modicons</c>).
///
/// The objective status is a server/net concern, so the net layer drives it here through
/// <see cref="ObjectiveStatus"/> + the team membership/count, and selects the renderer via
/// <see cref="Mode"/> (the analogue of the QC per-gametype <c>m_modicons</c> binding). This port covers the
/// three most common: CTF flags, Keyhunt keys, and Domination points — each reading the same bitpacked
/// status the QC code does. The skinned flag/key icons are replaced by colored swatches + a one/two-letter
/// state tag, but the decode (bitfields, per-team layout, carrying-blink) is faithful.
/// </summary>
public partial class ModIconsPanel : HudPanel
{
    /// <summary>Which gametype's objective renderer to draw (QC <c>gametype.m_modicons</c> selection).</summary>
    public enum ModIconsMode { None, Ctf, Keyhunt, Domination }

    /// <summary>The active renderer; <see cref="ModIconsMode.None"/> draws nothing (QC: no m_modicons set).</summary>
    public ModIconsMode Mode { get; set; } = ModIconsMode.None;

    /// <summary>QC <c>STAT(OBJECTIVE_STATUS)</c>: the bitpacked team-objective status (flags / keys / ...).</summary>
    public int ObjectiveStatus { get; set; }

    /// <summary>The local player's team number (QC <c>myteam</c>: 1=red, 2=blue, 3=yellow, 4=pink).</summary>
    public int MyTeam { get; set; } = 1;

    /// <summary>Number of teams in play (QC <c>team_count</c> / <c>_teams_available</c> bits).</summary>
    public int TeamCount { get; set; } = 2;

    // Per-team point-per-second values for Domination (QC STAT(DOM_PPS_*) / DOM_TOTAL_PPS), index 0..3.
    private readonly float[] _domPps = new float[4];
    private float _domTotalPps;

    // ---- CTF flag-status bit layout (QC common/gametypes/gametype/ctf/ctf.qh CTF_*_FLAG_TAKEN) ----
    // Each flag's 2-bit status is packed at a multiplier base; status = (stat / base) & 3:
    //   1 = taken (away from base), 2 = lost (dropped), 3 = carrying (you hold it).
    private const int CtfRedBase = 1;
    private const int CtfBlueBase = 4;
    private const int CtfYellowBase = 16;
    private const int CtfPinkBase = 64;
    private const int CtfNeutralBase = 256;
    private const int CtfShielded = 4096;   // CTF_SHIELDED (ctf.qh:57)
    private const int CtfFlagNeutral = 2048; // one-flag mode marker

    /// <summary>This panel changes only when objective status is pushed, but carrying-blink animates.</summary>
    public override bool IsDynamic => true;

    private double _localClock;
    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private double CurrentTime()
    {
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    /// <summary>Set the Domination point-per-second readout (QC STAT(DOM_PPS_*) + DOM_TOTAL_PPS).</summary>
    public void SetDominationPps(float red, float blue, float yellow, float pink, float total)
    {
        _domPps[0] = red; _domPps[1] = blue; _domPps[2] = yellow; _domPps[3] = pink;
        _domTotalPps = total;
        QueueRedraw();
    }

    protected override void DrawPanel()
    {
        switch (Mode)
        {
            case ModIconsMode.Ctf:        DrawCtf(); break;
            case ModIconsMode.Keyhunt:    DrawKeyhunt(); break;
            case ModIconsMode.Domination: DrawDomination(); break;
            default: return; // None: draw nothing
        }
    }

    // ----------------------------------------------------------------------------------------------- CTF
    // Port of HUD_Mod_CTF (cl_ctf.qc): decode each flag's 2-bit status and draw a per-team swatch + tag.
    private void DrawCtf()
    {
        int s = ObjectiveStatus;
        int red = (s / CtfRedBase) & 3;
        int blue = (s / CtfBlueBase) & 3;
        int yellow = (s / CtfYellowBase) & 3;
        int pink = (s / CtfPinkBase) & 3;
        int neutral = (s / CtfNeutralBase) & 3;
        bool oneFlag = (s & CtfFlagNeutral) != 0;

        bool active = red != 0 || blue != 0 || yellow != 0 || pink != 0 || neutral != 0 || (s & CtfShielded) != 0;
        if (!active) return;

        DrawBackground();

        // Build the list of flags to show: in one-flag mode only the neutral flag, else the in-play teams.
        var flags = new List<(int status, Color color, string label)>();
        if (oneFlag)
        {
            flags.Add((neutral, new Color(0.85f, 0.85f, 0.85f, 1f), "N"));
        }
        else
        {
            flags.Add((red, new Color(1f, 0.2f, 0.2f, 1f), "R"));
            flags.Add((blue, new Color(0.2f, 0.45f, 1f, 1f), "B"));
            if (TeamCount >= 3) flags.Add((yellow, new Color(1f, 1f, 0.2f, 1f), "Y"));
            if (TeamCount >= 4) flags.Add((pink, new Color(1f, 0.3f, 0.8f, 1f), "P"));
        }

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float innerH = Size2.Y - pad * 2f;
        float cellW = innerW / flags.Count;
        double now = CurrentTime();
        float blink = 0.85f - 0.7f * Mathf.Abs(Mathf.Sin((float)now * Mathf.Pi * 5f)); // QC blink(0.85,0.15,5)

        for (int i = 0; i < flags.Count; i++)
        {
            var (status, color, label) = flags[i];
            float x = pad + i * cellW;
            string tag = status switch { 1 => "took", 2 => "lost", 3 => "CARRY", _ => "" };
            if (status == 0 && (s & CtfShielded) != 0) tag = "shld";
            if (string.IsNullOrEmpty(tag) && status == 0) continue;

            float a = status == 3 ? blink : 1f;
            DrawRect(new Rect2(x + 2f, pad, cellW * 0.4f, innerH),
                new Color(color.R, color.G, color.B, color.A * a));
            int sz = (int)Mathf.Clamp(innerH * 0.35f, 9f, 18f);
            DrawText(new Vector2(x + cellW * 0.45f, pad + innerH * 0.1f), label,
                new Color(1f, 1f, 1f, 0.9f), sz);
            DrawText(new Vector2(x + cellW * 0.45f, pad + innerH * 0.5f), tag,
                new Color(1f, 1f, 1f, 0.7f * a), Mathf.Max(8, sz - 3));
        }
    }

    // ------------------------------------------------------------------------------------------- Keyhunt
    // Port of HUD_Mod_KH (cl_keyhunt.qc): the status packs four 5-bit key fields; decode each key's owner.
    private void DrawKeyhunt()
    {
        int state = ObjectiveStatus;
        if (state == 0) return;

        // Decode per-key owner team (QC: (bitshift(state, i*-5) & 31) - 1; 30 = carried-by-me, 29 = dropped).
        int[] teamKeys = new int[5]; // index 0..3 teams, 4 = dropped
        int carrying = 0, allKeys = 0;
        for (int i = 0; i < 4; i++)
        {
            int keyState = ((state >> (i * 5)) & 31) - 1;
            if (keyState == -1) continue;
            if (keyState == 30) { carrying++; keyState = MyTeam; }
            switch (keyState)
            {
                case 1: teamKeys[0]++; break;
                case 2: teamKeys[1]++; break;
                case 3: teamKeys[2]++; break;
                case 4: teamKeys[3]++; break;
                case 29: teamKeys[4]++; break;
            }
            allKeys++;
        }
        if (allKeys == 0) return;

        DrawBackground();

        var colors = new[]
        {
            new Color(1f, 0.2f, 0.2f, 1f),  // red
            new Color(0.2f, 0.45f, 1f, 1f), // blue
            new Color(1f, 1f, 0.2f, 1f),    // yellow
            new Color(1f, 0.3f, 0.8f, 1f),  // pink
            new Color(0.6f, 0.6f, 0.6f, 1f),// dropped
        };

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float innerH = Size2.Y - pad * 2f;
        float slotW = innerW / allKeys;
        double now = CurrentTime();
        float blink = 0.6f + Mathf.Sin((float)now * 2f * Mathf.Pi) * 0.4f; // QC RUN HERE oscillation

        int slot = 0;
        for (int t = 0; t < 5; t++)
        {
            int n = teamKeys[t];
            while (n-- > 0)
            {
                float x = pad + slot * slotW;
                // Blink the local team's keys when it holds them all (QC carrying-all blink).
                float a = (carrying > 0 && t + 1 == MyTeam && teamKeys[MyTeam - 1] == allKeys) ? blink : 1f;
                DrawRect(new Rect2(x + slotW * 0.15f, pad, slotW * 0.7f, innerH),
                    new Color(colors[t].R, colors[t].G, colors[t].B, colors[t].A * a));
                slot++;
            }
        }
    }

    // ---------------------------------------------------------------------------------------- Domination
    // Port of HUD_Mod_Dom (cl_domination.qc): one bar per team filled to its share of total points-per-second.
    private void DrawDomination()
    {
        DrawBackground();

        int n = Mathf.Clamp(TeamCount, 2, 4);
        var colors = new[]
        {
            new Color(1f, 0.2f, 0.2f, 1f),
            new Color(0.2f, 0.45f, 1f, 1f),
            new Color(1f, 1f, 0.2f, 1f),
            new Color(1f, 0.3f, 0.8f, 1f),
        };

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float innerH = Size2.Y - pad * 2f;
        float cellW = innerW / n;

        for (int i = 0; i < n; i++)
        {
            float x = pad + i * cellW;
            float ratio = _domTotalPps > 0f ? _domPps[i] / _domTotalPps : 0f;
            // QC: vertical highlight fill grows from the bottom by the pps ratio.
            var cell = new Rect2(x + cellW * 0.1f, pad, cellW * 0.8f, innerH);
            DrawRect(cell, new Color(0f, 0f, 0f, 0.35f));
            float fillH = innerH * Mathf.Clamp(ratio, 0f, 1f);
            DrawRect(new Rect2(cell.Position.X, cell.Position.Y + (innerH - fillH), cell.Size.X, fillH),
                colors[i]);
            int sz = (int)Mathf.Clamp(innerH * 0.25f, 9f, 16f);
            DrawTextCentered(new Vector2(x, pad + innerH * 0.5f - sz * 0.5f), cellW,
                Mathf.RoundToInt(ratio * 100f) + "%", new Color(1f, 1f, 1f, 0.9f), sz);
        }
    }
}
