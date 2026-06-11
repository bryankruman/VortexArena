using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Mod-icons panel — port of Base/.../qcsrc/client/hud/panel/modicons.qc (HUD panel #10) and the per-gametype
/// draw functions it dispatches to (<c>HUD_Mod_CTF</c>, <c>HUD_Mod_KH</c>, <c>HUD_Mod_Dom</c>,
/// <c>HUD_Mod_CA</c>/<c>HUD_Mod_FreezeTag</c>, <c>HUD_Mod_Survival</c>, … in
/// common/gametypes/gametype/*/cl_*.qc). The QC version drew the team-objective status for the active
/// gametype: CTF flag states (taken / lost / carrying / shielded, per team) decoded from
/// <c>STAT(OBJECTIVE_STATUS)</c>; Keyhunt key slots; Domination point-per-second bars; the Clan
/// Arena / Freeze Tag alive-count grid; etc. Which renderer runs is chosen at gametype-init
/// (<c>HUD_ModIcons_SetFunc</c> -> <c>gametype.m_modicons</c>).
///
/// The objective status is a server/net concern, so the net layer drives it here through
/// <see cref="ObjectiveStatus"/> + the team membership/count, and selects the renderer via
/// <see cref="Mode"/> (the analogue of the QC per-gametype <c>m_modicons</c> binding). This port covers:
/// CTF flags, Keyhunt keys, Domination points, the Clan Arena / Freeze Tag alive-count grid
/// (<c>HUD_Mod_CA</c>/<c>HUD_Mod_FreezeTag</c> reading STAT(REDALIVE..PINKALIVE)), the Survival own-role
/// tag (<c>HUD_Mod_Survival</c>) and an Assault objective-role tag (port extension, using the shipped
/// <c>as_defend</c>/<c>as_destroy</c> skin art) — each reading the same bitpacked/stat data the QC code does.
///
/// Icons render via the real Xonotic skin art (<c>flag_&lt;color&gt;_&lt;state&gt;</c>, <c>kh_*</c>,
/// <c>player_&lt;color&gt;</c>, <c>dom_icon_*</c>, <c>as_*</c>) through <see cref="HudPanel.DrawSkinPic"/>, with a
/// drawn-primitive (colored swatch + text) fallback so nothing is invisible when a pic is missing. The
/// decode (bitfields, per-team layout, HUD_GetRowCount grid, carrying-blink, status-change expand,
/// stalemate overlay) is faithful to the .qc.
/// </summary>
public partial class ModIconsPanel : HudPanel
{
    /// <summary>Which gametype's objective renderer to draw (QC <c>gametype.m_modicons</c> selection).</summary>
    public enum ModIconsMode { None, Ctf, Keyhunt, Domination, ClanArena, FreezeTag, Survival, Assault }

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

    /// <summary>Port extension: the local player's Assault objective role. 0 = none (draws nothing),
    /// 1 = defend (attackers destroyed / you guard), 2 = destroy (you attack the objective). Drawn with the
    /// shipped <c>as_defend</c>/<c>as_destroy</c> skin art. Stock Xonotic has no Assault modicons renderer,
    /// so this is fed by the port's net layer when it knows the role; 0 keeps parity (panel blank).</summary>
    public int AssaultStatus { get; set; }

    // ---- CTF flag-status bit layout (QC common/gametypes/gametype/ctf/ctf.qh CTF_*_FLAG_TAKEN) ----
    // Each flag's 2-bit status is packed at a multiplier base; status = (stat / base) & 3:
    //   1 = taken (away from base), 2 = lost (dropped), 3 = carrying (you hold it).
    private const int CtfRedBase = 1;       // CTF_RED_FLAG_TAKEN     (ctf.qh:41)
    private const int CtfBlueBase = 4;      // CTF_BLUE_FLAG_TAKEN    (ctf.qh:44)
    private const int CtfYellowBase = 16;   // CTF_YELLOW_FLAG_TAKEN  (ctf.qh:47)
    private const int CtfPinkBase = 64;     // CTF_PINK_FLAG_TAKEN    (ctf.qh:50)
    private const int CtfNeutralBase = 256; // CTF_NEUTRAL_FLAG_TAKEN (ctf.qh:53)
    private const int CtfFlagNeutral = 2048; // CTF_FLAG_NEUTRAL (one-flag mode marker, ctf.qh:56)
    private const int CtfShielded = 4096;   // CTF_SHIELDED (ctf.qh:57)
    private const int CtfStalemate = 8192;  // CTF_STALEMATE (ctf.qh:58)

    // QC NUM_TEAM_* team-membership bits of _teams_available: BIT(0)=red BIT(1)=blue BIT(2)=yellow BIT(3)=pink.
    private const int Num1 = 1, Num2 = 2, Num3 = 3, Num4 = 4;

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
        // Prefer the sim clock when wired, but never let a half-initialised services container (or a NaN/Inf
        // clock value) leak into the per-frame draw math — fall back to the local accumulator.
        if (Api.Services is not null)
        {
            try
            {
                float t = Api.Clock.Time;
                if (float.IsFinite(t)) return t;
            }
            catch { /* services container present but clock not yet ready */ }
        }
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
            case ModIconsMode.Domination: DrawDomination(LayoutCvar("dom_layout")); break;
            case ModIconsMode.ClanArena:  DrawCaStyle(LayoutCvar("ca_layout")); break;
            case ModIconsMode.FreezeTag:  DrawCaStyle(LayoutCvar("freezetag_layout")); break;
            case ModIconsMode.Survival:   DrawSurvival(); break;
            case ModIconsMode.Assault:    DrawAssault(); break;
            default: return; // None: draw nothing
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Behavior cvars (defaults registered into the shared store; HudConfig invokes this by reflection).
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC the modicons layout cvars exported by each gametype's m_modicons_export
    /// (cl_clanarena.qc/cl_freezetag.qc/cl_domination.qc HUD_Write_Cvar) + the panel's dynamichud flag.
    /// Registered into the SHARED store so console/menu edits flip the layout live.</summary>
    public static void RegisterDefaults(XonoticGodot.Engine.Simulation.CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // _hud_descriptions.cfg ships ca/freezetag layout = 1 (icon + number); dom layout 1 = icon + percent.
        c.Register("hud_panel_modicons_ca_layout", "1", save);
        c.Register("hud_panel_modicons_freezetag_layout", "1", save);
        c.Register("hud_panel_modicons_dom_layout", "1", save);
        c.Register("hud_panel_modicons_dynamichud", "1", save);
    }

    /// <summary>Read a <c>hud_panel_modicons_&lt;suffix&gt;</c> layout cvar from the shared store (live, so the
    /// console can flip it). Defaults to 1 when unregistered (the shipped skin cfgs set 1).</summary>
    private int LayoutCvar(string suffix)
    {
        string s = CvarStr(suffix);
        if (string.IsNullOrWhiteSpace(s)) return 1;
        float v = CvarF(suffix, 1f);
        if (!float.IsFinite(v)) return 1;
        // Layouts are 0 (icon-only) / 1 (icon+pct) / 2 (icon+avg). Clamp so a bogus cvar can't yield int
        // overflow ((int)Infinity) or a wild value that confuses the layout==2 branch.
        return (int)Mathf.Clamp(v, 0f, 2f);
    }

    // QC NUM_TEAM_* swatch fallback colors: red '1 0 0', blue '0 0 1', yellow '1 1 0', pink '1 0 1'.
    private static readonly Color[] TeamColors =
    {
        new(1f, 0.2f, 0.2f, 1f),  // red
        new(0.2f, 0.45f, 1f, 1f), // blue
        new(1f, 1f, 0.2f, 1f),    // yellow
        new(1f, 0.3f, 0.8f, 1f),  // pink
    };

    // Pure-component team colors used by the count text (QC DrawCAItem/DrawDomItem '1 0 0' style).
    private static readonly Color[] PureTeamColors =
    {
        new(1f, 0f, 0f, 1f),
        new(0f, 0f, 1f, 1f),
        new(1f, 1f, 0f, 1f),
        new(1f, 0f, 1f, 1f),
    };

    private static readonly string[] TeamSkin = { "red", "blue", "yellow", "pink" };

    /// <summary>Aspect-fit-center a content box inside <paramref name="cell"/> at <paramref name="aspect"/>
    /// (width/height), exactly as QC DrawCAItem/DrawDomItem reflow the cell before drawing.</summary>
    private static Rect2 AspectFit(Rect2 cell, float aspect)
    {
        Vector2 pos = cell.Position, size = cell.Size;
        if (size.X / Mathf.Max(1f, size.Y) > aspect)
        {
            float w = aspect * size.Y;
            pos.X += (size.X - w) * 0.5f;
            size.X = w;
        }
        else
        {
            float h = size.X / Mathf.Max(0.0001f, aspect);
            pos.Y += (size.Y - h) * 0.5f;
            size.Y = h;
        }
        return new Rect2(pos, size);
    }

    /// <summary>Draw a skin icon aspect-fitted (QC <c>drawpic_aspect_skin</c>): the source pic keeps a square
    /// aspect, fitted-centered into <paramref name="box"/>, modulated by white at <paramref name="alpha"/>.
    /// Returns false if the pic was missing (so the caller can draw a stand-in).</summary>
    private bool DrawIconAspect(Rect2 box, string pic, float alpha)
    {
        Rect2 fit = AspectFit(box, 1f);
        return DrawSkinPic(pic, fit, new Color(1f, 1f, 1f, alpha));
    }

    /// <summary>Resolve a skin pic (skin -> default fall-through) the same way <see cref="HudPanel.DrawSkinPic"/>
    /// does, returning the raw texture (or null on miss) — needed for region draws.</summary>
    private static Texture2D? ResolveSkinPic(string bareName)
        => TextureCache.GetFirst($"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");

    /// <summary>Draw the bottom <paramref name="ratio"/> band of a skin pic into <paramref name="dst"/> (the QC
    /// drawsetcliparea stand-in for the Domination highlight fill). Returns false on a missing pic.</summary>
    private bool DrawIconRegionBottom(Rect2 dst, string pic, float ratio, float alpha)
    {
        Texture2D? tex = ResolveSkinPic(pic);
        if (tex is null) return false;
        Vector2 ts = tex.GetSize();
        float h = ts.Y * Mathf.Clamp(ratio, 0f, 1f);
        var src = new Rect2(0f, ts.Y - h, ts.X, h);
        DrawTextureRectRegion(tex, dst, src, new Color(1f, 1f, 1f, alpha));
        return true;
    }

    // ----------------------------------------------------------------------------------------------- CTF
    // Port of HUD_Mod_CTF (cl_ctf.qc): decode each flag's 2-bit status and draw the per-team flag icon at the
    // QC fs/fs2/fs3 layout positions (rotated so the local team's flag is centered), with the carrying-blink,
    // the status-change expand transition, and the stalemate overlay.
    private void DrawCtf()
    {
        int s = ObjectiveStatus;
        int redflag = (s / CtfRedBase) & 3;
        int blueflag = (s / CtfBlueBase) & 3;
        int yellowflag = (s / CtfYellowBase) & 3;
        int pinkflag = (s / CtfPinkBase) & 3;
        int neutralflag = (s / CtfNeutralBase) & 3;
        bool oneFlag = (s & CtfFlagNeutral) != 0;
        bool stalemate = (s & CtfStalemate) != 0;

        // QC mod_active gate: nothing to show => blank panel.
        bool active = redflag != 0 || blueflag != 0 || yellowflag != 0 || pinkflag != 0
                      || neutralflag != 0 || (s & CtfShielded) != 0;
        if (!active) return;

        DrawBackground();

        int nteams = TeamsAvailableBits();
        int tcount = 2;
        if ((nteams & (1 << 2)) != 0) tcount = 3;
        if ((nteams & (1 << 3)) != 0) tcount = 4;

        // QC blink(0.85, 0.15, 5): the carrying flag pulses.
        double now = CurrentTime();
        float carryAlpha = Blink(0.85f, 0.15f, 5f, now);

        // Resolve each flag's icon name + alpha (QC X(team, cond) macro).
        string? redIcon = CtfIcon("red", redflag, s, MyTeam != Num1 && (nteams & (1 << 0)) != 0, carryAlpha, out float ra);
        string? blueIcon = CtfIcon("blue", blueflag, s, MyTeam != Num2 && (nteams & (1 << 1)) != 0, carryAlpha, out float ba);
        string? yellowIcon = CtfIcon("yellow", yellowflag, s, MyTeam != Num3 && (nteams & (1 << 2)) != 0, carryAlpha, out float ya);
        string? pinkIcon = CtfIcon("pink", pinkflag, s, MyTeam != Num4 && (nteams & (1 << 3)) != 0, carryAlpha, out float pa);
        string? neutralIcon = CtfIcon("neutral", neutralflag, s, oneFlag, carryAlpha, out float na);

        // QC layout split factors by team count (cl_ctf.qc:123-132).
        float fs, fs2, fs3;
        if (oneFlag)
        {
            redIcon = blueIcon = yellowIcon = pinkIcon = null; // only the neutral flag in one-flag mode
            fs = fs2 = fs3 = 1f;
        }
        else
        {
            switch (tcount)
            {
                case 3: fs = 1f; fs2 = 0.35f; fs3 = 0.35f; break;
                case 4: fs = 0.75f; fs2 = 0.25f; fs3 = 0.5f; break;
                default: fs = 0.5f; fs2 = 0.5f; fs3 = 0.5f; break;
            }
        }

        // QC: long axis = size1 (split between flags), short axis = size2. Here panels are usually wide.
        Vector2 size = Size2;
        bool wide = size.X > size.Y;
        float size1 = wide ? size.X : size.Y;
        float size2 = wide ? size.Y : size.X;

        // The QC rotates flag positions so the LOCAL team's flag sits at the origin (pos) and the rest fan out.
        // Build per-flag offsets along the long axis (e1) by my team.
        float redOff, blueOff, yellowOff, pinkOff;
        switch (MyTeam)
        {
            case Num2: redOff = fs2; blueOff = 0f; yellowOff = -fs2; pinkOff = fs3; break;
            case Num3: redOff = fs3; blueOff = -fs2; yellowOff = 0f; pinkOff = fs2; break;
            case Num4: redOff = -fs2; blueOff = fs3; yellowOff = fs2; pinkOff = 0f; break;
            default:   redOff = 0f; blueOff = fs2; yellowOff = -fs2; pinkOff = fs3; break;
        }

        // flag_size: e1 * fs * size1 (long axis) + e2 * size2 (short axis).
        Vector2 flagSize = wide ? new Vector2(fs * size1, size2) : new Vector2(size2, fs * size1);

        DrawFlag(redIcon, OffsetPos(redOff, size1, wide), flagSize, ra, stalemate, "R", TeamColors[0]);
        DrawFlag(blueIcon, OffsetPos(blueOff, size1, wide), flagSize, ba, stalemate, "B", TeamColors[1]);
        DrawFlag(yellowIcon, OffsetPos(yellowOff, size1, wide), flagSize, ya, stalemate, "Y", TeamColors[2]);
        DrawFlag(pinkIcon, OffsetPos(pinkOff, size1, wide), flagSize, pa, stalemate, "P", TeamColors[3]);
        DrawFlag(neutralIcon, Vector2.Zero, flagSize, na, stalemate, "N", new Color(0.85f, 0.85f, 0.85f, 1f));
    }

    private Vector2 OffsetPos(float frac, float size1, bool wide)
        => wide ? new Vector2(frac * size1, 0f) : new Vector2(0f, frac * size1);

    /// <summary>QC X(team, cond): pick the flag icon name for a 2-bit status (1=taken,2=lost,3=carrying),
    /// or the shielded icon when the flag is home AND the cond (it's an enemy team / one-flag) holds.</summary>
    private string? CtfIcon(string team, int flag, int statItems, bool cond, float carryAlpha, out float alpha)
    {
        alpha = 1f;
        switch (flag)
        {
            case 1: return $"flag_{team}_taken";
            case 2: return $"flag_{team}_lost";
            case 3: alpha = carryAlpha; return $"flag_{team}_carrying";
            default:
                if ((statItems & CtfShielded) != 0 && cond) return $"flag_{team}_shielded";
                return null;
        }
    }

    /// <summary>QC X(team) draw macro: optional stalemate overlay, then the flag icon (aspect-fit skin pic with
    /// a colored-swatch + tag fallback).</summary>
    private void DrawFlag(string? icon, Vector2 offset, Vector2 flagSize, float alpha, bool stalemate,
        string fallbackLabel, Color fallbackColor)
    {
        if (string.IsNullOrEmpty(icon)) return;
        var box = new Rect2(offset, flagSize);

        if (stalemate)
            DrawIconAspect(box, "flag_stalemate", LiveFgAlpha);

        float a = LiveFgAlpha * alpha;
        if (!DrawIconAspect(box, icon, a))
        {
            // Stand-in so the flag is never invisible: a team swatch + a short state tag.
            Rect2 fit = AspectFit(box, 1f);
            string tag = icon.EndsWith("_taken") ? "took"
                       : icon.EndsWith("_lost") ? "lost"
                       : icon.EndsWith("_carrying") ? "CARRY"
                       : icon.EndsWith("_shielded") ? "shld" : "";
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.12f, fit.Position.Y + fit.Size.Y * 0.1f,
                fit.Size.X * 0.36f, fit.Size.Y * 0.8f), new Color(fallbackColor.R, fallbackColor.G, fallbackColor.B, a));
            int sz = (int)Mathf.Clamp(fit.Size.Y * 0.32f, 8f, 16f);
            DrawText(new Vector2(fit.Position.X + fit.Size.X * 0.5f, fit.Position.Y + fit.Size.Y * 0.1f),
                fallbackLabel, new Color(1f, 1f, 1f, a), sz);
            DrawText(new Vector2(fit.Position.X + fit.Size.X * 0.5f, fit.Position.Y + fit.Size.Y * 0.5f),
                tag, new Color(1f, 1f, 1f, a * 0.85f), Mathf.Max(8, sz - 2));
        }
    }

    // ------------------------------------------------------------------------------------------- Keyhunt
    // Port of HUD_Mod_KH (cl_keyhunt.qc): the status packs four 5-bit key fields; decode each key's owner and
    // lay out the per-key icons (kh_<color>_taken/carrying + kh_dropped) in QC's quad/horizontal/vertical grid.
    private void DrawKeyhunt()
    {
        int state = ObjectiveStatus;
        if (state == 0) return; // QC: if(!state) return;

        // Per-team key counts (index 0..3 = red..pink, 4 = dropped) and carry total (QC team*_keys/carrying_keys).
        int[] teamKeys = new int[5];
        int allKeys = 0, carrying = 0;
        for (int i = 0; i < 4; i++)
        {
            int keyState = ((state >> (i * 5)) & 31) - 1;
            if (keyState == -1) continue;
            if (keyState == 30) { carrying++; keyState = MyTeam; } // 30 = carried-by-me
            switch (keyState)
            {
                case Num1: teamKeys[0]++; break;
                case Num2: teamKeys[1]++; break;
                case Num3: teamKeys[2]++; break;
                case Num4: teamKeys[3]++; break;
                case 29: teamKeys[4]++; break; // 29 = dropped
            }
            allKeys++;
        }
        if (allKeys == 0) return;

        DrawBackground();

        Vector2 size = Size2;
        float pad = Padding;
        var origin = new Vector2(pad, pad);
        Vector2 area = new(size.X - pad * 2f, size.Y - pad * 2f);

        // QC KH_SLOTS layout: quadratic when all 4 keys + the panel is roughly square; else horizontal/vertical.
        var slots = new Vector2[allKeys];
        Vector2 slotSize;
        if (allKeys == 4 && area.X * 0.5f < area.Y && area.Y * 0.5f < area.X)
        {
            slotSize = new Vector2(area.X * 0.5f, area.Y * 0.5f);
            slots[0] = origin;
            slots[1] = origin + new Vector2(slotSize.X, 0f);
            slots[2] = origin + new Vector2(0f, slotSize.Y);
            slots[3] = origin + new Vector2(slotSize.X, slotSize.Y);
        }
        else if (area.X > area.Y)
        {
            slotSize = new Vector2(area.X / allKeys, area.Y);
            for (int i = 0; i < allKeys; i++) slots[i] = origin + new Vector2(slotSize.X * i, 0f);
        }
        else
        {
            slotSize = new Vector2(area.X, area.Y / allKeys);
            for (int i = 0; i < allKeys; i++) slots[i] = origin + new Vector2(0f, slotSize.Y * i);
        }

        // QC carrying-all blink: oscillate 0.2..1 when the local team carries every key.
        double now = CurrentTime();
        float blink = 0.6f + Mathf.Sin((float)now * 2f * Mathf.Pi) * 0.4f;
        float baseAlpha = LiveFgAlpha;

        int slot = 0;
        // QC draws teams in order red, blue, yellow, pink, then dropped; the local team's keys use the
        // *_carrying art while carrying_keys remain, else *_taken.
        int carryRemain = carrying;
        for (int t = 0; t < 4; t++)
        {
            int n = teamKeys[t];
            // QC: when the local team carries every key, ALL its icons blink (t+1 == MyTeam => teamKeys[t]).
            bool teamBlinks = carrying != 0 && t + 1 == MyTeam && teamKeys[t] == allKeys;
            float a = baseAlpha * (teamBlinks ? blink : 1f);
            while (n-- > 0 && slot < slots.Length)
            {
                bool mine = t + 1 == MyTeam && carryRemain > 0;
                string pic = mine ? $"kh_{TeamSkin[t]}_carrying" : $"kh_{TeamSkin[t]}_taken";
                if (mine) carryRemain--;
                DrawKeySlot(slots[slot++], slotSize, pic, a, TeamColors[t]);
            }
        }
        int dropped = teamKeys[4];
        while (dropped-- > 0 && slot < slots.Length)
            DrawKeySlot(slots[slot++], slotSize, "kh_dropped", baseAlpha, new Color(0.6f, 0.6f, 0.6f, 1f));
    }

    private void DrawKeySlot(Vector2 pos, Vector2 slotSize, string pic, float alpha, Color fallback)
    {
        var box = new Rect2(pos, slotSize);
        if (!DrawIconAspect(box, pic, alpha))
        {
            Rect2 fit = AspectFit(box, 1f);
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.18f, fit.Position.Y + fit.Size.Y * 0.12f,
                fit.Size.X * 0.64f, fit.Size.Y * 0.76f),
                new Color(fallback.R, fallback.G, fallback.B, fallback.A * alpha));
        }
    }

    // ---------------------------------------------------------------------------------------- Domination
    // Port of HUD_Mod_Dom / DrawDomItem (cl_domination.qc): a HUD_GetRowCount grid of one cell per team; each
    // draws the dom_icon_<color> base + a clip-masked dom_icon_<color>-highlighted fill grown bottom-up by the
    // team's share of total points-per-second; layout 1 also draws the percentage text in the team color.
    private void DrawDomination(int layout)
    {
        DrawBackground();

        int n = Mathf.Clamp(TeamCount, 2, 4);
        float aspectRatio = layout != 0 ? 3f : 1f; // QC aspect_ratio = (layout) ? 3 : 1
        float pad = Padding;
        var inner = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);

        int rows = Mathf.Max(1, HudGetRowCount(n, inner.Size, aspectRatio));
        int columns = Mathf.Max(1, Mathf.CeilToInt(n / (float)rows));
        var itemSize = new Vector2(inner.Size.X / columns, inner.Size.Y / rows);

        int row = 0, column = 0;
        for (int i = 0; i < n; i++)
        {
            var pos = new Vector2(inner.Position.X + column * itemSize.X, inner.Position.Y + row * itemSize.Y);
            DrawDomItem(new Rect2(pos, itemSize), aspectRatio, layout, i);
            ++row;
            if (row >= rows) { row = 0; ++column; }
        }
    }

    /// <summary>QC <c>DrawDomItem</c>: the dom_icon for team <paramref name="i"/> with a bottom-up highlight fill
    /// proportional to its pps share, plus the optional percentage text.</summary>
    private void DrawDomItem(Rect2 cell, float aspectRatio, int layout, int i)
    {
        float stat = _domPps[i];
        float ratio = _domTotalPps > 0f ? Mathf.Clamp(stat / _domTotalPps, 0f, 1f) : 0f;
        string pic = $"dom_icon_{TeamSkin[i]}";

        Rect2 fit = AspectFit(cell, aspectRatio);
        Vector2 pos = fit.Position, size = fit.Size;

        // QC draws the icon as a square block sized to mySize.y on the left.
        float iconW = size.Y;
        var iconBox = new Rect2(pos, new Vector2(iconW, size.Y));

        if (layout != 0) // show text too (QC layout 2 = average pps with 2 decimals, else percentage)
        {
            Color baseCol = PureTeamColors[i];
            float sat = 0.5f + ratio * 0.5f; // half-saturated at min pps, full at max (QC color *= 0.5 + ratio*0.5)
            var col = new Color(baseCol.R * sat, baseCol.G * sat, baseCol.B * sat, LiveFgAlpha);
            int sz = (int)Mathf.Clamp(size.Y * 0.7f, 9f, 30f);
            float textX = pos.X + iconW;
            float textW = (2f / 3f) * size.X;
            string text = layout == 2
                ? stat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) // QC ftos_decimals(stat,2)
                : Mathf.RoundToInt(ratio * 100f) + "%";                                     // QC floor(ratio*100+0.5)+"%"
            DrawTextCentered(new Vector2(textX, pos.Y + (size.Y - sz) * 0.5f), textW, text, col, sz);
        }

        // Icon base + bottom-up highlight fill (QC clip-area trick) — skin art, swatch fallback.
        if (!DrawIconAspect(iconBox, pic, LiveFgAlpha))
        {
            DrawRect(iconBox, new Color(0f, 0f, 0f, 0.35f * LiveFgAlpha));
            DrawRect(new Rect2(iconBox.Position.X, iconBox.Position.Y + iconW * (1f - ratio), iconW, iconW * ratio),
                new Color(TeamColors[i].R, TeamColors[i].G, TeamColors[i].B, LiveFgAlpha));
            return;
        }
        if (ratio > 0f)
        {
            // QC clip-area trick: draw the -highlighted variant only over the bottom (ratio) band. Godot's C#
            // CanvasItem has no drawsetcliparea, so emulate it by region-drawing the matching slice of the
            // highlighted texture into the bottom band of the aspect-fit icon box.
            Rect2 iconFit = AspectFit(iconBox, 1f);
            float fillH = iconFit.Size.Y * ratio;
            var dst = new Rect2(iconFit.Position.X, iconFit.Position.Y + (iconFit.Size.Y - fillH),
                iconFit.Size.X, fillH);
            DrawIconRegionBottom(dst, pic + "-highlighted", ratio, LiveFgAlpha);
        }
    }

    // ------------------------------------------------------------------------- ClanArena / FreezeTag
    // Port of HUD_Mod_CA / HUD_Mod_CA_Draw / DrawCAItem (cl_clanarena.qc:19-92); FreezeTag reuses the same
    // drawer with its own layout cvar (HUD_Mod_FreezeTag, cl_freezetag.qc:10-18). A HUD_GetRowCount grid of one
    // cell per active team showing the alive count from STAT(REDALIVE..PINKALIVE); layout 1 adds the per-team
    // player_<color> skin icon on the cell's left half + the count, team-colored, on the right.
    private void DrawCaStyle(int layout)
    {
        DrawBackground();

        int n = Mathf.Clamp(TeamCount, 2, 4);
        float aspectRatio = layout != 0 ? 2f : 1f; // QC aspect_ratio = (layout) ? 2 : 1
        float pad = Padding;
        var inner = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);

        int rows = Mathf.Max(1, HudGetRowCount(n, inner.Size, aspectRatio));
        int columns = Mathf.Max(1, Mathf.CeilToInt(n / (float)rows));
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
        if (itemCount < 1) return 1;
        float aspect = size.Y / Mathf.Max(1f, size.X);
        float v = Mathf.Floor((Mathf.Sqrt(4f * itemAspect * aspect * itemCount + aspect * aspect) + aspect + 0.5f) * 0.5f);
        if (!float.IsFinite(v)) return 1; // a degenerate panel size must never yield rows=0 (→ /0 in the caller)
        return (int)Mathf.Clamp(v, 1f, itemCount);
    }

    /// <summary>QC <c>DrawCAItem</c>: aspect-fit-center the cell content, then layout 1 = the player_&lt;color&gt;
    /// team icon on the left half + the alive count in the team color on the right half; layout 0 = count only.</summary>
    private void DrawCaItem(Rect2 cell, float aspectRatio, int layout, int i)
    {
        int stat = _alive[i];
        Color countColor = new(PureTeamColors[i].R, PureTeamColors[i].G, PureTeamColors[i].B, LiveFgAlpha);

        Rect2 fit = AspectFit(cell, aspectRatio);
        Vector2 pos = fit.Position, size = fit.Size;

        int fontSize = (int)Mathf.Clamp(size.Y * 0.8f, 9f, 40f);
        if (layout != 0)
        {
            var iconBox = new Rect2(pos, new Vector2(0.5f * size.X, size.Y));
            if (!DrawIconAspect(iconBox, $"player_{TeamSkin[i]}", LiveFgAlpha))
            {
                Rect2 ifit = AspectFit(iconBox, 1f);
                DrawRect(new Rect2(ifit.Position.X + ifit.Size.X * 0.12f, ifit.Position.Y + ifit.Size.Y * 0.12f,
                    ifit.Size.X * 0.76f, ifit.Size.Y * 0.76f),
                    new Color(TeamColors[i].R, TeamColors[i].G, TeamColors[i].B, 0.85f * LiveFgAlpha));
            }
            DrawTextCentered(new Vector2(pos.X + 0.5f * size.X, pos.Y + (size.Y - fontSize) * 0.5f),
                0.5f * size.X, stat.ToString(), countColor, fontSize);
        }
        else
        {
            DrawTextCentered(new Vector2(pos.X, pos.Y + (size.Y - fontSize) * 0.5f),
                size.X, stat.ToString(), countColor, fontSize);
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
        Color color = hunter ? new Color(1f, 0f, 0f, LiveFgAlpha) : new Color(0f, 1f, 0f, LiveFgAlpha);
        float pad = Padding;
        int fontSize = (int)Mathf.Clamp((Size2.Y - pad * 2f) * 0.6f, 10f, 32f);
        DrawTextCentered(new Vector2(pad, (Size2.Y - fontSize) * 0.5f), Size2.X - pad * 2f, text, color, fontSize);
    }

    // ------------------------------------------------------------------------------------------ Assault
    // Port extension (stock has no Assault m_modicons): draw the local player's objective role using the
    // shipped as_defend / as_destroy skin art, aspect-fit-centered, with a text fallback. 0 = blank.
    private void DrawAssault()
    {
        if (AssaultStatus == 0) return;
        DrawBackground();
        bool destroy = AssaultStatus == 2;
        string pic = destroy ? "as_destroy" : "as_defend";
        float pad = Padding;
        var box = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);
        if (!DrawIconAspect(box, pic, LiveFgAlpha))
        {
            // QC AssaultDefend/Destroy waypoint color '1 0.5 0'.
            var col = new Color(1f, 0.5f, 0f, LiveFgAlpha);
            int fontSize = (int)Mathf.Clamp(box.Size.Y * 0.5f, 10f, 28f);
            DrawTextCentered(new Vector2(box.Position.X, box.Position.Y + (box.Size.Y - fontSize) * 0.5f),
                box.Size.X, destroy ? "Destroy" : "Defend", col, fontSize);
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>_teams_available</c> bitfield (BIT(0)=red BIT(1)=blue BIT(2)=yellow BIT(3)=pink). The
    /// port feeds team membership through <see cref="TeamCount"/>, so reconstruct the contiguous low bits.</summary>
    private int TeamsAvailableBits()
    {
        int n = Mathf.Clamp(TeamCount, 2, 4);
        int bits = 0;
        for (int i = 0; i < n; i++) bits |= 1 << i;
        return bits;
    }

    /// <summary>QC <c>blink(base, amplitude, frequency)</c> from client/draw.qh: a sinusoidal pulse used for the
    /// carrying-flag flash (base 0.85, amplitude 0.15, freq 5).</summary>
    private static float Blink(float baseAlpha, float amplitude, float frequency, double now)
    {
        float a = baseAlpha + amplitude * Mathf.Sin((float)now * frequency);
        return float.IsFinite(a) ? Mathf.Clamp(a, 0f, 1f) : baseAlpha;
    }
}
