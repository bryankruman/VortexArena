using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards the per-player exterior-weapon VIEW block (QC the networked <c>wepent.*</c> charge/clip/load/heat
/// fields that drive OTHER players' / spectatees' crosshair rings — the owner's own rings ride the separate
/// <see cref="OwnerWeaponRings"/> owner block, which is UNCHANGED). The new channel is
/// <see cref="EntityField.WepentView"/> (bit 20, the new tail of the entity delta) carrying a fixed
/// <see cref="WepentViewState"/> block.
///
/// <para>This is the <see cref="OwnerWeaponRingsTests"/> pattern applied to the entity-stream path: a single
/// <see cref="WepentViewState"/> Write/Read pair is the source of truth for the block layout, and the new mask
/// bit is appended AFTER the existing <see cref="EntityField.Wepent"/> block in
/// <see cref="EntityStateCodec.WriteDelta"/>/<see cref="EntityStateCodec.ReadDelta"/>, so the prior fields stay
/// byte-aligned and only the new tail is added. The desync class these lock down: a field added to Write but not
/// Read (the same regression OwnerWeaponRings warns about) would shift every block after it for a real remote
/// client.</para>
///
/// <para>Intended divergence (documented on the block): Base codes some pools at ×16; the port uses ×255
/// uniformly for all [0,1] charge/pool/heat values — the same precision class as the existing 13i-coord /
/// ×255-alpha divergences — so each of those fields round-trips to within 1/255.</para>
/// </summary>
public class WepentViewStateTests
{
    /// <summary>1/255 quantization tolerance for the ×255→byte→/255 [0,1] charge/pool/heat fields.</summary>
    private const float ChargeTol = 1f / 255f;

    /// <summary>A fully-populated sample exercising every field with a distinct, non-default value.</summary>
    private static WepentViewState Sample => new()
    {
        VortexCharge = 0.25f,
        OknexCharge = 0.50f,
        VortexChargePool = 0.75f,
        OknexChargePool = 0.125f,
        ClipLoad = 7,
        ClipSize = 12,
        HagarLoad = 3,
        MinelayerMines = 2,
        ArcHeat = 0.8f,
        ViewmodelFrame = 4,
        BeamState = 0b101, // arc beam active + electro beam active
    };

    // ---- (1) Write -> Read round-trips every field within quantization tolerance --------------------------

    [Fact]
    public void Write_Read_RoundTrips_EveryField_WithinQuantizationTolerance()
    {
        var w = new BitWriter();
        WepentViewCodec.Write(w, Sample);

        var r = new BitReader(w.WrittenSpan);
        WepentViewState got = WepentViewCodec.Read(ref r);

        Assert.False(r.BadRead);
        // [0,1] charge/pool/heat: ×255 → byte → /255, so within 1/255.
        Assert.InRange(got.VortexCharge, 0.25f - ChargeTol, 0.25f + ChargeTol);
        Assert.InRange(got.OknexCharge, 0.50f - ChargeTol, 0.50f + ChargeTol);
        Assert.InRange(got.VortexChargePool, 0.75f - ChargeTol, 0.75f + ChargeTol);
        Assert.InRange(got.OknexChargePool, 0.125f - ChargeTol, 0.125f + ChargeTol);
        Assert.InRange(got.ArcHeat, 0.8f - ChargeTol, 0.8f + ChargeTol);
        // shorts / bytes: exact within their integer range.
        Assert.Equal(7, got.ClipLoad);
        Assert.Equal(12, got.ClipSize);
        Assert.Equal(3, got.HagarLoad);
        Assert.Equal(2, got.MinelayerMines);
        Assert.Equal(4, got.ViewmodelFrame);
        Assert.Equal(0b101, got.BeamState);
    }

    [Fact]
    public void Write_Read_PreservesTheClipLoadNeedsReloadSentinel()
    {
        // Base clip_load is SIGNED: -1 = "needs reload" — it must survive the signed-short coding intact, distinct
        // from ClipSize 0 = "weapon has no clip → ring hidden".
        var s = Sample;
        s.ClipLoad = -1;   // needs-reload sentinel
        s.ClipSize = 0;    // no-clip weapon

        var w = new BitWriter();
        WepentViewCodec.Write(w, s);
        var r = new BitReader(w.WrittenSpan);
        WepentViewState got = WepentViewCodec.Read(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(-1, got.ClipLoad);
        Assert.Equal(0, got.ClipSize);
    }

    /// <summary>The desync guard (the OwnerWeaponRings sentinel pattern): write a sentinel, the block, then a
    /// second sentinel, and confirm the reader lands EXACTLY on the trailing sentinel after the block — i.e. Read
    /// consumes precisely the 13 bytes Write produced. Misaligned by even one field and the trailing sentinel is
    /// garbage, exactly as the live entity stream would desync.</summary>
    [Fact]
    public void Read_ConsumesExactlyWhatWriteProduced_KeepingTheStreamAligned()
    {
        var w = new BitWriter();
        w.WriteUShort(0xBEEF);
        int before = w.Length;
        WepentViewCodec.Write(w, Sample);
        int blockBytes = w.Length - before;
        w.WriteUShort(0xD00D);

        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(0xBEEF, r.ReadUShort());
        WepentViewState got = WepentViewCodec.Read(ref r);
        Assert.Equal(0xD00D, r.ReadUShort()); // misaligned by even one field and this is garbage
        Assert.False(r.BadRead);

        Assert.Equal(13, blockBytes); // 4 bytes + 2 shorts (4) + 5 bytes = 13 bytes when present
        Assert.Equal(7, got.ClipLoad);
    }

    // ---- (2) NetEntityState.Diff sets WepentView iff WepentView changed -----------------------------------

    private static NetEntityState Player(int id) => new()
    {
        EntNum = id, Kind = NetEntityKind.Player, ModelIndex = 7, Origin = Vector3.Zero, Health = 100,
        // match the producer + the Empty baseline so the wepent group bits stay clear for a plain player.
        SwitchWeapon = -1, SwitchingWeapon = -1,
    };

    [Fact]
    public void Diff_WepentView_Bit_Set_Iff_WepentView_Changed()
    {
        var baseline = Player(10);

        // two identical states → bit clear (the "idle costs one zero mask" contract).
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, baseline) & EntityField.WepentView);

        // any change to the WepentView block → bit set.
        var cur = baseline;
        cur.WepentView = Sample;
        Assert.Equal(EntityField.WepentView, NetEntityState.Diff(baseline, cur) & EntityField.WepentView);
    }

    [Fact]
    public void Diff_WepentView_Tracks_EachField_AndNothingOutsideTheBlock()
    {
        var baseline = Player(10);
        baseline.WepentView = Sample; // a non-default baseline so each per-field flip is meaningful

        void AssertField(System.Func<WepentViewState, WepentViewState> mutate)
        {
            var cur = baseline;
            cur.WepentView = mutate(baseline.WepentView);
            Assert.Equal(EntityField.WepentView, NetEntityState.Diff(baseline, cur) & EntityField.WepentView);
        }

        AssertField(v => { v.VortexCharge = 0.9f; return v; });
        AssertField(v => { v.OknexCharge = 0.1f; return v; });
        AssertField(v => { v.VortexChargePool = 0.0f; return v; });
        AssertField(v => { v.OknexChargePool = 0.6f; return v; });
        AssertField(v => { v.ClipLoad = 1; return v; });
        AssertField(v => { v.ClipSize = 30; return v; });
        AssertField(v => { v.HagarLoad = 1; return v; });
        AssertField(v => { v.MinelayerMines = 1; return v; });
        AssertField(v => { v.ArcHeat = 0.0f; return v; });
        AssertField(v => { v.ViewmodelFrame = 9; return v; });
        AssertField(v => { v.BeamState = 0b010; return v; });

        // a non-wepentview change (the older Wepent group) must NOT spuriously set the WepentView bit.
        var wep = baseline; wep.WepPhase = 2;
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, wep) & EntityField.WepentView);
    }

    [Fact]
    public void Diff_PlainPlayer_LeavesWepentView_Clear()
    {
        // an all-default player (no charge, WepentView at its default) must NOT set the new bit — it costs nothing
        // on the wire until a producer sets the block.
        EntityField m = NetEntityState.Diff(NetEntityState.Empty(10), Player(10));
        Assert.Equal(EntityField.None, m & EntityField.WepentView);
    }

    // ---- (3) Codec carries the block only when the bit is set, prior fields stay aligned -------------------

    [Fact]
    public void EntityCodec_RoundTrips_WepentView_WhenSet()
    {
        var baseline = Player(10);
        var cur = baseline;
        cur.WepentView = Sample;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.WepentView, mask & EntityField.WepentView);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        Assert.InRange(got.WepentView.VortexCharge, 0.25f - ChargeTol, 0.25f + ChargeTol);
        Assert.InRange(got.WepentView.ArcHeat, 0.8f - ChargeTol, 0.8f + ChargeTol);
        Assert.Equal(7, got.WepentView.ClipLoad);
        Assert.Equal(12, got.WepentView.ClipSize);
        Assert.Equal(2, got.WepentView.MinelayerMines);
        Assert.Equal(0b101, got.WepentView.BeamState);
    }

    [Fact]
    public void EntityCodec_WepentView_AbsentWhenUnchanged_CarriesBaselineBlock()
    {
        // change ONLY a body field (Health): WepentView must stay clear and the block carries from the baseline.
        var baseline = Player(10);
        baseline.WepentView = Sample; // a non-default baseline so "carried from baseline" is meaningful

        var moved = baseline; moved.Health = 75;
        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, moved);
        Assert.Equal(EntityField.None, mask & EntityField.WepentView);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal(75, got.Health);
        Assert.Equal(7, got.WepentView.ClipLoad);   // carried from baseline — bit was clear
        Assert.Equal(12, got.WepentView.ClipSize);
    }

    /// <summary>
    /// The alignment guard the OwnerWeaponRings desync class warns about: changing ONLY WepentView must leave the
    /// PRIOR delta fields intact. Here a non-default baseline carries an AnimAction overlay and a Wepent block; the
    /// current state flips only the WepentView block. Both prior groups must round-trip unchanged from the baseline
    /// (their bits clear), and the new WepentView tail must decode — proving the new bit is appended AFTER the
    /// existing Wepent block in identical order on both halves.
    /// </summary>
    [Fact]
    public void EntityCodec_WepentViewOnlyChange_LeavesPriorWepentAndAnimActionFieldsIntact()
    {
        var baseline = Player(10);
        // prior groups at non-default baseline values (carried, since only WepentView changes):
        baseline.UpperAction = 3;            // AnimAction group
        baseline.AnimActionTime = 12.5f;
        baseline.WepPhase = 2;               // Wepent group
        baseline.ViewmodelSkin = 1;
        baseline.WepAlpha = 128;
        baseline.GunAlign = 3;
        baseline.SwitchWeapon = 5;
        baseline.SwitchingWeapon = 4;
        baseline.WepentView = Sample;

        var cur = baseline;
        var view = Sample;
        view.VortexCharge = 0.6f;   // the ONLY change is inside the WepentView block
        cur.WepentView = view;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        // exactly the WepentView bit, none of the prior groups.
        Assert.Equal(EntityField.WepentView, mask);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        // prior fields stay aligned — carried verbatim from the baseline.
        Assert.Equal((byte)3, got.UpperAction);
        Assert.Equal(12.5f, got.AnimActionTime);
        Assert.Equal((byte)2, got.WepPhase);
        Assert.Equal((byte)1, got.ViewmodelSkin);
        Assert.Equal((byte)128, got.WepAlpha);
        Assert.Equal((byte)3, got.GunAlign);
        Assert.Equal(5, got.SwitchWeapon);
        Assert.Equal(4, got.SwitchingWeapon);

        // the new tail decoded.
        Assert.InRange(got.WepentView.VortexCharge, 0.6f - ChargeTol, 0.6f + ChargeTol);
    }

    [Fact]
    public void EntityCodec_TwoIdenticalStates_CostOneZeroMask()
    {
        // the "idle entity costs one zero mask" contract: identical states → empty mask → just the 32-bit mask.
        var s = Player(10);
        s.WepentView = Sample;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, s, s);
        Assert.Equal(EntityField.None, mask);
        Assert.Equal(4, w.Length); // the 32-bit zero mask, nothing else
    }

    // ---- (4) WepentResolver.Resolve returns None for a dead / observer player -----------------------------

    [Fact]
    public void WepentResolver_DeadPlayer_ResolvesToNone()
    {
        var p = new Player { DeadState = DeadFlag.Dead };
        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        AssertIsNone(got);
    }

    [Fact]
    public void WepentResolver_ObserverPlayer_ResolvesToNone()
    {
        var p = new Player { IsObserver = true };
        WepentViewState got = WepentResolver.Resolve(p, _ => 0, now: 10f);
        AssertIsNone(got);
    }

    /// <summary>The None block: all charge/clip/load/heat fields default (no rings drawn for this player).</summary>
    private static void AssertIsNone(in WepentViewState got)
    {
        WepentViewState none = WepentViewState.None;
        Assert.True(none.Equals(got),
            "a dead/observer player must resolve to WepentViewState.None (no rings for this player)");
    }
}
