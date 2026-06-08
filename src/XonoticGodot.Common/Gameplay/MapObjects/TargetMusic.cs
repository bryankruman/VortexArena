// Port of qcsrc/common/mapobjects/target/music.qc (target_music / trigger_music).
//
// The Xonotic music system has three priority layers (from QC TargetMusic_Advance):
//   1. cdtrack (default) — the map's background music from its .mapinfo cdtrack line
//   2. target_music (targeted) — a point entity triggered on/off that overrides the default
//   3. trigger_music (brush volume) — highest priority, plays when the player is inside the brush
//
// On the SERVER side, these entities simply record their track/volume/fade parameters and respond to
// triggers (target_music) or touch events (trigger_music). The actual music PLAYBACK is client-side
// (MusicPlayer.cs in game/client/) — the server communicates music state to the client via the
// MusicState structure exposed on GameWorld, which the net layer / listen-server bridge reads.
//
// For the listen-server path (the current primary play path), target_music and trigger_music state is
// read directly by the client MusicPlayer each frame (no networking needed — shared process).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// <c>target_music</c> and <c>trigger_music</c> — map-entity music overrides. Registered by
/// <see cref="MapObjectsRegistry"/>.
/// </summary>
public static class TargetMusic
{
    // ========================================================================
    //  target_music — point entity, triggered on/off
    // ========================================================================

    /// <summary><c>spawnfunc(target_music)</c> — a triggered music override (or the map default when untargetable).</summary>
    public static void TargetMusicSetup(Entity this_)
    {
        this_.ClassName = "target_music";

        // Default volume to 1 if unset.
        if (this_.Volume == 0f)
            this_.Volume = 1f;

        // If no targetname, this is the DEFAULT music for the map (same role as cdtrack).
        // If it HAS a targetname, it's a triggered override.
        if (!string.IsNullOrEmpty(this_.TargetName))
        {
            this_.Use = TargetMusicUse;
            this_.Active = MapMover.ActiveNot; // starts inactive; triggered to activate
            MapMover.IndexRegister(this_);
        }
        else
        {
            // Default music: always active. This overrides cdtrack if present.
            this_.Active = MapMover.ActiveActive;
            MapMover.IndexRegister(this_);
        }
    }

    /// <summary>Toggle target_music on/off when triggered.</summary>
    private static void TargetMusicUse(Entity self, Entity activator)
    {
        // Toggle: active -> inactive, inactive -> active
        if (self.Active == MapMover.ActiveActive)
            self.Active = MapMover.ActiveNot;
        else
            self.Active = MapMover.ActiveActive;
    }

    // ========================================================================
    //  trigger_music — brush volume, plays when player is inside
    // ========================================================================

    /// <summary><c>spawnfunc(trigger_music)</c> — a brush volume that plays music when the player is inside.</summary>
    public static void TriggerMusicSetup(Entity this_)
    {
        this_.ClassName = "trigger_music";

        // Default volume to 1 if unset.
        if (this_.Volume == 0f)
            this_.Volume = 1f;

        // Initialize as a trigger volume (brush model for the overlap test).
        MapMover.InitTrigger(this_);

        // Touch handler: the MusicPlayer reads the touch state each frame to determine if the player is
        // inside any trigger_music volume. We set the Touch delegate so the touch system fires it.
        this_.Touch = TriggerMusicTouch;

        // Spawnflag 1 = START_DISABLED (from QC: can be toggled via relay_activate/deactivate).
        if ((this_.SpawnFlags & 1) != 0)
            this_.Active = MapMover.ActiveNot;
        else
            this_.Active = MapMover.ActiveActive;

        // Register so relay_activate / relay_deactivate can find it by targetname.
        if (!string.IsNullOrEmpty(this_.TargetName))
        {
            this_.Use = TriggerMusicActivateUse;
            MapMover.IndexRegister(this_);
        }
        else
        {
            MapMover.IndexRegister(this_);
        }
    }

    /// <summary>
    /// Touch handler for trigger_music. Called by the touch system when a player overlaps this brush volume.
    /// We mark the entity with a timestamp so the client MusicPlayer can detect "player is inside this volume
    /// THIS frame" — the standard DP pattern where a touch fires every physics frame the overlap holds.
    /// </summary>
    private static void TriggerMusicTouch(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        // Only react to players (not projectiles/items).
        if ((toucher.Flags & EntFlags.Client) != 0)
        {
            // Record the touch time on the trigger entity. The MusicPlayer scans all trigger_music entities
            // each frame and considers one "active" if its LastTouchTime is within the current frame window.
            if (Api.Services is not null)
                self.PushLTime = Api.Clock.Time;
        }
    }

    /// <summary>Use handler for relay_activate/deactivate toggling on trigger_music.</summary>
    private static void TriggerMusicActivateUse(Entity self, Entity activator)
    {
        // Toggle active state (relay_activate / relay_deactivate / relay_activatetoggle).
        if (self.Active == MapMover.ActiveActive)
            self.Active = MapMover.ActiveNot;
        else
            self.Active = MapMover.ActiveActive;
    }
}
