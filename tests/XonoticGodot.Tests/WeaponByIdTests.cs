using System.Linq;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the <c>weapon_byid_N</c> selection order — the C# port of QC's <c>Weapon_from_impulse(230 + N)</c>
/// (common/weapons/all.qh:150) over the <c>m_unique_impulse</c> table the STATIC_INIT in all.qh allocates.
///
/// <para>The hazard this guards: <see cref="Registry{T}.Sort"/> orders the live weapon registry ALPHABETICALLY
/// by NetName (so <c>ById(0)</c> is "arc"), but QC keeps the 19 "hardcoded-impulse" core weapons in DEFINITION
/// order — <c>REGISTRY_SORT(Weapons, WEP_HARDCODED_IMPULSES + 1)</c> only strcmp-sorts indices &gt;= 20 — and
/// allocates the by-id impulses in that registry order. So <c>weapon_byid_0</c> must be blaster, not arc.
/// <see cref="WeaponOrder.ByIdOrder"/> reproduces the QC order WITHOUT touching the global registry sort; this
/// test pins it so the major can't silently regress back to the alphabetical ById.</para>
/// </summary>
[Collection("GlobalState")]
public class WeaponByIdTests
{
    public WeaponByIdTests() => GameRegistries.Bootstrap();

    /// <summary>The QC by-id order: the 19 core weapons in all.inc definition order, then the Overkill tail in
    /// strcmp(registered_id) == ordinal(NetName) order (okhmg/okmachinegun/oknex/okrpc/okshotgun).</summary>
    private static readonly string[] ExpectedByIdOrder =
    {
        // weapon_byid_0 .. weapon_byid_18 — the WEP_HARDCODED_IMPULSES (19) core weapons, all.inc order.
        "blaster", "shotgun", "machinegun", "mortar", "minelayer", "electro", "crylink", "vortex", "hagar",
        "devastator", "porto", "vaporizer", "hook", "hlac", "tuba", "rifle", "fireball", "seeker", "arc",
        // weapon_byid_19 .. — the strcmp-sorted tail (the Overkill weapons).
        "okhmg", "okmachinegun", "oknex", "okrpc", "okshotgun",
    };

    [Fact]
    public void ByIdOrder_Is_QC_DefinitionOrder_Not_Alphabetical()
    {
        string[] order = WeaponOrder.ByIdOrder().Select(w => w.NetName).ToArray();
        Assert.Equal(ExpectedByIdOrder, order);

        // weapon_byid_0 is blaster — the headline regression. The alphabetical registry sort would yield "arc".
        Assert.Equal("blaster", order[0]);
        Assert.Equal("arc", Registry<Weapon>.ById(0).NetName); // the registry IS alphabetical (arc first)…
        Assert.NotEqual("arc", order[0]);                       // …but by-id is NOT.
    }

    [Theory]
    [InlineData(0, "blaster")]
    [InlineData(1, "shotgun")]
    [InlineData(2, "machinegun")]
    [InlineData(3, "mortar")]
    [InlineData(7, "vortex")]
    [InlineData(9, "devastator")]
    [InlineData(18, "arc")]   // last hardcoded-impulse core weapon
    [InlineData(19, "okhmg")] // first tail weapon
    public void WeaponByIdIndex_Resolves_The_QC_Weapon(int idx, string expectedNetName)
    {
        Weapon? w = WeaponOrder.WeaponByIdIndex(idx);
        Assert.NotNull(w);
        Assert.Equal(expectedNetName, w!.NetName);
    }

    [Fact]
    public void WeaponByIdIndex_OutOfRange_Returns_Null()
    {
        Assert.Null(WeaponOrder.WeaponByIdIndex(-1));
        Assert.Null(WeaponOrder.WeaponByIdIndex(Registry<Weapon>.Count)); // one past the last reachable id
        Assert.Null(WeaponOrder.WeaponByIdIndex(9999));
    }

    [Fact]
    public void ByIdOrder_Covers_Every_ImpulseReachable_Weapon_Exactly_Once()
    {
        var order = WeaponOrder.ByIdOrder();
        // No port weapon is impulse-skipped (no SPECIALATTACK, no Ball-Stealer MUTATORBLOCKED+TYPE_OTHER),
        // so the by-id order is a permutation of the whole registry.
        int skipped = Registry<Weapon>.All.Count(w =>
            (w.SpawnFlags & WeaponFlags.SpecialAttack) != 0
            || ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0 && (w.SpawnFlags & WeaponFlags.TypeOther) != 0));
        Assert.Equal(0, skipped);

        Assert.Equal(Registry<Weapon>.Count, order.Count);
        Assert.Equal(order.Count, order.Select(w => w.NetName).Distinct().Count());
        // Every registry weapon appears.
        Assert.Equal(
            Registry<Weapon>.All.Select(w => w.NetName).OrderBy(n => n).ToList(),
            order.Select(w => w.NetName).OrderBy(n => n).ToList());
    }
}
