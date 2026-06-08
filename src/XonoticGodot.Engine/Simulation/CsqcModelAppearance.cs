// Port of qcsrc/client/csqcmodel_hooks.qc — CSQCPlayer_ModelAppearance_Apply (lines 141-363). The testable
// decisions of the force-model / force-color / unique-color / death-fade appearance pass are factored out here
// (pure, Godot-free, unit-tested); the Godot glue (game/client/ModelTint.cs + ClientWorld) reads the cvars and
// resolves the actual model/colormap, calling these for the math. Constants/branch order mirror the QC.
//
// The full colormapPaletteColor 16-entry table comes from lib/color.qh (colormapPaletteColor_) and
// gfx/colormap_palette.pl; the team-color subset already lived in ModelTint.TeamColor.

using System;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Pure helpers for the CSQC player-model appearance hook: which force-color mode is active for the current
/// gametype, the deterministic per-enemy unique-color combo, the friend/enemy forced colormaps, the
/// <c>colormapPaletteColor</c> palette, and the death-fade glow factor.
/// </summary>
public static class CsqcModelAppearance
{
    /// <summary>QC NUM_SPECTATOR (common/teams.qh:10) — the team value that means "spectating".</summary>
    public const int NumSpectator = 5;

    /// <summary>Which model a player entity should render as, after the FORCEMODEL branch resolves
    /// (csqcmodel_hooks.qc:204-235). Mirrors the QC if/else cascade as a pure decision.</summary>
    public enum ForcedModelSource
    {
        /// <summary><c>cl_forcemyplayermodel</c> (my model) — set + good + this entity is a friend.</summary>
        MyModel,
        /// <summary><c>cl_forceplayermodels</c> — force everyone to my resolved <c>_cl_playermodel</c>.</summary>
        ForcedModel,
        /// <summary>The entity's own (good) networked model — no force applies.</summary>
        Own,
        /// <summary>The guaranteed-good fallback model (entity's own model was missing).</summary>
        GuaranteedGood,
    }

    /// <summary>The resolved FORCEMODEL choice: which model source + the skin to use.</summary>
    public readonly record struct ForcedModelDecision(ForcedModelSource Source, int Skin);

    /// <summary>
    /// QC FORCEMODEL branch (csqcmodel_hooks.qc:204-235) as a pure decision. <paramref name="isFriend"/> is the
    /// QC <c>isfriend</c> (teamplay ? cm==1024+17*myteam : islocalplayer). The cvars: <paramref name="forceMyModel"/>
    /// = <c>cl_forcemyplayermodel != ""</c>, <paramref name="forceMyModelGood"/> = that model exists,
    /// <paramref name="forceMySkin"/> = <c>cl_forcemyplayerskin</c>; <paramref name="forceAll"/> =
    /// <c>cl_forceplayermodels</c>, <paramref name="forceAllGood"/> = the resolved <c>_cl_playermodel</c> exists,
    /// <paramref name="forceAllSkin"/> = its skin; <paramref name="ownGood"/> = the entity's own networked model
    /// exists, <paramref name="ownSkin"/> = its skin (also used for the guaranteed-good fallback's skin, per QC).
    /// </summary>
    public static ForcedModelDecision ResolveForcedModel(
        bool forceMyModel, bool forceMyModelGood, int forceMySkin, bool isFriend,
        bool forceAll, bool forceAllGood, int forceAllSkin,
        bool ownGood, int ownSkin)
    {
        if (forceMyModel && forceMyModelGood && isFriend)
            return new ForcedModelDecision(ForcedModelSource.MyModel, forceMySkin);
        if (forceAll && forceAllGood)
            return new ForcedModelDecision(ForcedModelSource.ForcedModel, forceAllSkin);
        if (ownGood)
            return new ForcedModelDecision(ForcedModelSource.Own, ownSkin);
        return new ForcedModelDecision(ForcedModelSource.GuaranteedGood, ownSkin);
    }

    /// <summary>
    /// QC FORCECOLORS branch (csqcmodel_hooks.qc:260-327) as a pure decision: returns the forced colormap to use,
    /// or 0 when no force applies (the entity keeps its networked colormap). Mirrors the teamplay vs FFA split.
    /// <para><paramref name="cm"/> is the QC resolved colormap of THIS entity (savecolormap, &gt;=1024 form);
    /// <paramref name="myColormapFull"/> = <c>1024 + 17*myteam</c> (the friend-color test).</para>
    /// </summary>
    /// <param name="forcePlayerColorsEnabled">QC <c>forceplayercolors_enabled</c> (from <see cref="ForcePlayerColorsEnabled"/>).</param>
    /// <param name="forceMyColors">QC <c>cl_forcemyplayercolors</c> (0 = unset).</param>
    /// <param name="clColor">QC <c>autocvar__cl_color</c> (the enemy-force color value).</param>
    /// <param name="forceUnique">QC <c>cl_forceuniqueplayercolors</c>.</param>
    /// <param name="isLocalPlayer">QC <c>islocalplayer</c>.</param>
    /// <param name="is1v1">QC <c>gametype.m_1v1</c>.</param>
    /// <param name="teamplay">QC <c>teamplay</c>.</param>
    /// <param name="myTeam">QC <c>myteam</c>.</param>
    /// <param name="cm">QC <c>cm</c> — this entity's resolved colormap (&gt;=1024).</param>
    /// <param name="playerLocalNum">QC <c>player_localnum</c>.</param>
    /// <param name="entityNum">QC <c>entnum</c> (or <c>sv_entnum</c>) for the unique-color combo.</param>
    /// <returns>The forced colormap (&gt;=1024), or 0 to keep the entity's own colormap.</returns>
    public static int ResolveForcedColormap(
        bool forcePlayerColorsEnabled, int forceMyColors, int clColor, bool forceUnique,
        bool isLocalPlayer, bool is1v1, bool teamplay, int myTeam, int cm, int playerLocalNum, int entityNum)
    {
        if (teamplay)
        {
            // own team's color is never forced
            int forcecolorFriend = 0, forcecolorEnemy = 0;
            if (forceMyColors != 0)
                forcecolorFriend = 1024 + forceMyColors;
            if (forcePlayerColorsEnabled)
                forcecolorEnemy = 1024 + clColor;

            int myFull = 1024 + 17 * myTeam;
            if (forcecolorEnemy != 0 && forcecolorFriend == 0)
            {
                // only enemy color forced? verify it is not equal to the friend (own-team) color
                if (forcecolorEnemy == myFull)
                    forcecolorEnemy = 0;
            }
            // NOTE: QC also re-checks a friend-only force against every team color (csqcmodel_hooks.qc:281-292);
            // the port doesn't network the team list here, so it compares against the LOCAL team only (the common
            // collision). Documented parity gap — a friend force equal to another team's color isn't suppressed.
            if (forcecolorFriend != 0 && forcecolorEnemy == 0)
            {
                if (forcecolorFriend == myFull)
                    forcecolorFriend = 0;
            }

            if (cm == myFull)
                return forcecolorFriend != 0 ? forcecolorFriend : 0;
            return forcecolorEnemy != 0 ? forcecolorEnemy : 0;
        }
        else // !teamplay (FFA)
        {
            if (forceMyColors != 0 && isLocalPlayer)
                return 1024 + forceMyColors;
            if (forceUnique && !isLocalPlayer && !is1v1)
                return UniqueColormap(entityNum - 1);
            if (forcePlayerColorsEnabled)
                return 1024 + playerLocalNum + 1;
            return 0;
        }
    }

    /// <summary>
    /// QC <c>forceplayercolors_enabled</c> predicate (csqcmodel_hooks.qc:242-258). <paramref name="fpc"/> is
    /// <c>autocvar_cl_forceplayercolors</c>. The gametype gates which fpc values enable forcing:
    /// 1v1/Duel → fpc∈{1,2,3,5} (and myteam!=spectator); 2-team teamplay → fpc∈{2,4,5} (and myteam!=spectator);
    /// else (FFA) → fpc∈{1,2}.
    /// </summary>
    public static bool ForcePlayerColorsEnabled(int fpc, bool is1v1, bool isTeamplay, int teamCount, int myTeam)
    {
        if (is1v1)
        {
            return (myTeam != NumSpectator) && (fpc == 1 || fpc == 2 || fpc == 3 || fpc == 5);
        }
        else if (isTeamplay)
        {
            return (teamCount == 2) && (myTeam != NumSpectator) && (fpc == 2 || fpc == 4 || fpc == 5);
        }
        else
        {
            return (fpc == 1 || fpc == 2);
        }
    }

    /// <summary>
    /// QC unique-enemy-color combo (csqcmodel_hooks.qc:315-323). For enemy index <paramref name="num"/>
    /// (entnum-1) picks two palette colors 0..14 (15 is the rainbow, excluded) and packs them into a colormap:
    /// <c>c1 = num%15</c>, <c>q = floor(num/15)</c>, <c>c2 = (c1+1+q)%15</c>, <c>colormap = 1024 + (c1&lt;&lt;4) + c2</c>.
    /// </summary>
    public static int UniqueColormap(int num)
    {
        int c1 = num % 15;
        int q = num / 15;            // floor for num>=0 (QC floor(num/15))
        int c2 = (c1 + 1 + q) % 15;
        return 1024 + (c1 << 4) + c2;
    }

    /// <summary>
    /// QC <c>colormapPaletteColor(c, isPants)</c> (lib/color.qh) over the full 0..15 palette. Used to derive
    /// the model glowmod from the low colormap nibble (csqcmodel_hooks.qc:340). Color 15 is the animated
    /// rainbow — it needs <paramref name="time"/> and the <paramref name="isPants"/> phase; 0..14 are static.
    /// </summary>
    public static (float r, float g, float b) ColormapPaletteColor(int c, bool isPants, float time)
    {
        switch (c)
        {
            case 0:  return (1.000000f, 1.000000f, 1.000000f);
            case 1:  return (1.000000f, 0.333333f, 0.000000f);
            case 2:  return (0.000000f, 1.000000f, 0.501961f);
            case 3:  return (0.000000f, 1.000000f, 0.000000f);
            case 4:  return (1.000000f, 0.000000f, 0.000000f);
            case 5:  return (0.000000f, 0.666667f, 1.000000f);
            case 6:  return (0.000000f, 1.000000f, 1.000000f);
            case 7:  return (0.501961f, 1.000000f, 0.000000f);
            case 8:  return (0.501961f, 0.000000f, 1.000000f);
            case 9:  return (1.000000f, 0.000000f, 1.000000f);
            case 10: return (1.000000f, 0.000000f, 0.501961f);
            case 11: return (0.000000f, 0.000000f, 1.000000f);
            case 12: return (1.000000f, 1.000000f, 0.000000f);
            case 13: return (0.000000f, 0.333333f, 1.000000f);
            case 14: return (1.000000f, 0.666667f, 0.000000f);
            case 15:
                // The animated rainbow (lib/color.qh:32-40). M_E and M_PI are the QC math constants.
                const float M_E = 2.718281828459045f;
                const float M_PI = 3.141592653589793f;
                if (isPants)
                    return (
                        0.502f + 0.498f * MathF.Sin(time / M_E + 0f),
                        0.502f + 0.498f * MathF.Sin(time / M_E + M_PI * 2f / 3f),
                        0.502f + 0.498f * MathF.Sin(time / M_E + M_PI * 4f / 3f));
                return (
                    0.502f + 0.498f * MathF.Sin(time / M_PI + M_PI * 5f / 3f),
                    0.502f + 0.498f * MathF.Sin(time / M_PI + M_PI),
                    0.502f + 0.498f * MathF.Sin(time / M_PI + M_PI * 1f / 3f));
            default: return (0.000f, 0.000f, 0.000f);
        }
    }

    /// <summary>
    /// QC death-fade glow scalar (csqcmodel_hooks.qc:344-356): the factor multiplied into glowmod while a model
    /// is dead. <c>min_factor = bound(0, cl_deathglow_min, 1)</c> (default 0.5), halved when the model has a
    /// color (colormap&gt;0); <c>glow_fade = bound(0, 1 - (now - deathTime)/cl_deathglow, 1)</c>; the result is
    /// <c>min_factor + glow_fade * (1 - min_factor)</c>. Returns 1 (no fade) when <paramref name="deathglow"/>
    /// &lt;= 0 or the model isn't dead. At the death instant the factor is 1.0; long after, it's <c>min_factor</c>.
    /// </summary>
    public static float DeathGlowFactor(float deathglow, float deathglowMin, bool hasColor, bool isDead, float now, float deathTime)
    {
        if (deathglow <= 0f || !isDead)
            return 1f;
        float minFactor = Bound(0f, deathglowMin, 1f);
        if (hasColor)
            minFactor /= 2f;
        float glowFade = Bound(0f, 1f - (now - deathTime) / deathglow, 1f);
        return minFactor + glowFade * (1f - minFactor);
    }

    private static float Bound(float lo, float v, float hi) => Math.Min(Math.Max(v, lo), hi);
}
