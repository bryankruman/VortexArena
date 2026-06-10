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
/// <see cref="Mode"/> (the analogue of the QC per-gametype <c>m_modicons</c> binding). This port covers:
/// CTF flags, Keyhunt keys, Domination points, the Clan Arena / Freeze Tag alive-count grid
/// (<c>HUD_Mod_CA</c>/<c>HUD_Mod_FreezeTag</c> reading STAT(REDALIVE..PINKALIVE)) and the Survival own-role
/// tag (<c>HUD_Mod_Survival</c>) — each reading the same bitpacked/stat data the QC code does. The skinned
/// flag/key/player icons are replaced by colored swatches + text, but the decode (bitfields, per-team
/// layout, HUD_GetRowCount grid, carrying-blink) is faithful.
/// </summary>
public partial class ModIconsPanel : HudPanel
{
    /// <summary>Which gametype's objective renderer to draw (QC <c>gametype.m_modicons</c> selection).</summary>
    public enum ModIconsMode { None, Ctf, Keyhunt, Domination, ClanArena, FreezeTag, Survival }

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

    // Per-team alive counts for ClanArena/FreezeTag (QC STAT(REDALIVE..PINKALIVE)), index 0..3 = red..pink.
    private readonly int[] _alive = new int[4];

    /// <summary>Set the CA/FT alive counts (QC STAT(REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE)).</summary>
    public void SetAliveCounts(int red, int blue, int yellow, int pink)
    {
        _alive[0] = red; _alive[1] = blue; _alive[2] = yellow; _alive[3] = pink;
        QueueRedraw();
    }

    /// <summary>The local player's Survival role (QC <c>playerslots[player_localnum].survival_status</c>):
    /// 0 = none (draws nothing — the server sends 0 pre-round/spectating, its side of QC's
    /// GAMESTARTTIME/ROUNDSTARTTIME hide), 1 = prey ("Survivor"), 2 = hunter ("Hunter").</summary>
    public int SurvivalStatus { get; set; }

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
            case ModIconsMode.ClanArena:  DrawCaStyle(LayoutCvar("hud_panel_modicons_ca_layout")); break;
            case ModIconsMode.FreezeTag:  DrawCaStyle(LayoutCvar("hud_panel_modicons_freezetag_layout")); break;
            case ModIconsMode.Survival:   DrawSurvival(); break;
            default: return; // None: draw nothing
        }
    }

    /// <summary>QC <c>autocvar_hud_panel_modicons_ca_layout</c> / <c>_freezetag_layout</c>: 0 = number only,
    /// 1 = icon + number (_hud_descriptions.cfg:213/215; the shipped skin cfgs set 1). Read live so the
    /// console can flip it; defaults to 1 when unregistered.</summary>
    private static int LayoutCvar(string name)
    {
        if (Api.Services is null) return 1;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? 1 : (int)Api.Cvars.GetFloat(name);
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

    // ------------------------------------------------------------------------- ClanArena / FreezeTag
    // Port of HUD_Mod_CA / HUD_Mod_CA_Draw / DrawCAItem (cl_clanarena.qc:19-92); FreezeTag reuses the same
    // drawer with its own layout cvar (HUD_Mod_FreezeTag, cl_freezetag.qc:10-18). A grid of one cell per
    // active team showing the alive count from STAT(REDALIVE..PINKALIVE); layout 1 adds the per-team
    // "player_<color>" skin icon (here a colored swatch, like the CTF renderer's stand-in for skinned
    // icons) on the cell's left half.

    /// <summary>QC the DrawCAItem count colors '1 0 0' / '0 0 1' / '1 1 0' / '1 0 1' (red/blue/yellow/pink).</summary>
    private static readonly Color[] CaTeamColors =
    {
        new(1f, 0f, 0f, 1f),
        new(0f, 0f, 1f, 1f),
        new(1f, 1f, 0f, 1f),
        new(1f, 0f, 1f, 1f),
    };

    private void DrawCaStyle(int layout)
    {
        DrawBackground();

        int n = Mathf.Clamp(TeamCount, 2, 4);
        float aspectRatio = layout != 0 ? 2f : 1f; // QC aspect_ratio = (layout) ? 2 : 1
        float pad = Padding;
        var inner = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);

        int rows = HudGetRowCount(n, inner.Size, aspectRatio);
        int columns = Mathf.CeilToInt(n / (float)rows);
        var itemSize = new Vector2(inner.Size.X / columns, inner.Size.Y / rows);

        int row = 0, column = 0;
        for (int i = 0; i < n; i++)
        {
            var pos = new Vector2(inner.Position.X + column * itemSize.X, inner.Position.Y + row * itemSize.Y);
            DrawCaItem(new Rect2(pos, itemSize), aspectRatio, layout, i);
            ++row;
            if (row >= rows) { row = 0; ++column; } // QC: cells fill row-first down each column
        }
    }

    /// <summary>QC <c>HUD_GetRowCount</c> (client/hud/hud.qc:165-170), formula carried verbatim:
    /// <c>bound(1, floor((sqrt(4*item_aspect*aspect*N + aspect^2) + aspect + 0.5) * 0.5), N)</c>
    /// with aspect = panel height/width.</summary>
    private static int HudGetRowCount(int itemCount, Vector2 size, float itemAspect)
    {
        float aspect = size.Y / Mathf.Max(1f, size.X);
        return (int)Mathf.Clamp(
            Mathf.Floor((Mathf.Sqrt(4f * itemAspect * aspect * itemCount + aspect * aspect) + aspect + 0.5f) * 0.5f),
            1f, itemCount);
    }

    /// <summary>QC <c>DrawCAItem</c>: aspect-fit-center the cell content, then layout 1 = team icon (swatch)
    /// on the left half + the alive count in the team color on the right half; layout 0 = count only.</summary>
    private void DrawCaItem(Rect2 cell, float aspectRatio, int layout, int i)
    {
        Color color = CaTeamColors[i];
        int stat = _alive[i];

        // QC: aspect-fit the content box inside the cell (mySize.x/mySize.y vs aspect_ratio reflow).
        Vector2 pos = cell.Position, size = cell.Size;
        if (size.X / Mathf.Max(1f, size.Y) > aspectRatio)
        {
            float w = aspectRatio * size.Y;
            pos.X += (size.X - w) * 0.5f;
            size.X = w;
        }
        else
        {
            float h = size.X / aspectRatio;
            pos.Y += (size.Y - h) * 0.5f;
            size.Y = h;
        }

        int fontSize = (int)Mathf.Clamp(size.Y * 0.8f, 9f, 40f);
        if (layout != 0)
        {
            // Left half: the "player_red|blue|yellow|pink" icon stand-in; right half: the count, team-colored.
            DrawRect(new Rect2(pos.X + size.X * 0.06f, pos.Y + size.Y * 0.12f, size.X * 0.38f, size.Y * 0.76f),
                new Color(color.R, color.G, color.B, 0.85f));
            DrawTextCentered(new Vector2(pos.X + size.X * 0.5f, pos.Y + (size.Y - fontSize) * 0.5f),
                size.X * 0.5f, stat.ToString(), color, fontSize);
        }
        else
        {
            DrawTextCentered(new Vector2(pos.X, pos.Y + (size.Y - fontSize) * 0.5f),
                size.X, stat.ToString(), color, fontSize);
        }
    }

    // ---------------------------------------------------------------------------------------- Survival
    // Port of HUD_Mod_Survival (cl_survival.qc:47-75): one centered own-role tag — "Hunter" in '1 0 0' /
    // "Survivor" in '0 1 0'. The QC pre-round/spectating hide (STAT(GAMESTARTTIME)/STAT(ROUNDSTARTTIME) >
    // time || spectating) is the SERVER's job in the port: it sends status 0 until the roles are live,
    // and 0 draws nothing.
    private void DrawSurvival()
    {
        if (SurvivalStatus == 0) return;
        DrawBackground();
        bool hunter = SurvivalStatus == 2; // SURV_STATUS_HUNTER (survival.qh:37)
        string text = hunter ? "Hunter" : "Survivor";
        Color color = hunter ? new Color(1f, 0f, 0f, 1f) : new Color(0f, 1f, 0f, 1f);
        float pad = Padding;
        int fontSize = (int)Mathf.Clamp((Size2.Y - pad * 2f) * 0.6f, 10f, 32f);
        DrawTextCentered(new Vector2(pad, (Size2.Y - fontSize) * 0.5f), Size2.X - pad * 2f, text, color, fontSize);
    }
}
