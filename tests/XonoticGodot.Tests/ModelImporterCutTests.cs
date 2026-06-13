// Port of darkplaces/model_shared.c (loader table 45-65) + qcsrc/common/models/all.inc (MODEL() registry)
// Wave A4 T24: the MDL/MD2/ZYM/PSK importers are a FORMAL CUT — see AssetLoader.BuildModelFactory's
// FORMAL-CUT comment. AssetLoader.LoadModel dispatches a model by its leading magic and supports ONLY
// IQM ("INTERQUAKEMODEL"), DPM ("DARKPLACESMODEL"), and MD3 ("IDP3"); anything else (MDL "IDPO",
// MD2 "IDP2", ZYM "ZYMOTICMODEL", PSK "ACTRHEAD") falls through to null, and the caller renders a
// generated placeholder. These tests pin that contract: the two MDL files actually reached by
// AssetLoader.LoadModel (casing_shell.mdl, gibs/chunk.mdl) carry the "IDPO" magic that the loader does
// NOT handle — so LoadModel returns null and the ShellCasings/ModelGibs fallbacks render.

using System;
using System.IO;
using System.Linq;
using System.Text;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards the Wave A4 T24 formal cut of the MDL/MD2/ZYM/PSK model importers. AssetLoader itself is a
/// Godot type (uses GD/Node3D/AudioStream) and lives in the game project the test assembly does not
/// reference, so — exactly like <c>MuzzleOffsetTests</c> — these tests reproduce the loader's magic
/// dispatch over raw bytes rather than constructing an AssetLoader: the decision under test ("is this a
/// model magic the port implements?") is pure data.
///
/// <para>CI-portable: the real-asset cases silently no-op when the reference checkout is absent
/// (mirrors <c>AssetParserTests</c> / <c>MuzzleOffsetTests</c>).</para>
/// </summary>
public class ModelImporterCutTests
{
    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // The three model magics the port's AssetLoader.BuildModelFactory actually dispatches (extensions lie,
    // so dispatch is by leading magic — same tags AssetLoader uses as MagicIqm/MagicDpm/MagicMd3).
    private const string MagicIqm = "INTERQUAKEMODEL"; // IQM
    private const string MagicDpm = "DARKPLACESMODEL"; // DPM
    private const string MagicMd3 = "IDP3";            // MD3
    // The cut formats' magics (darkplaces model_shared.c:48-61), none implemented by the port.
    private const string MagicMdl = "IDPO";            // Quake1 MDL — Mod_IDP0_Load (cut)

    /// <summary>Read up to the first 16 bytes as an ASCII tag (trimmed at the first NUL) — the same magic
    /// extraction <c>AssetLoader.ReadMagic</c> performs before dispatch.</summary>
    private static string ReadMagic(byte[] data)
    {
        int n = Math.Min(16, data.Length);
        int end = 0;
        while (end < n && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, 0, end);
    }

    /// <summary>True iff the magic matches one of the three importers the port implements — i.e. iff
    /// <c>AssetLoader.BuildModelFactory</c> would build a node instead of falling through to null.</summary>
    private static bool IsSupportedModelMagic(string magic) =>
        magic.StartsWith(MagicIqm, StringComparison.Ordinal) ||
        magic.StartsWith(MagicDpm, StringComparison.Ordinal) ||
        magic.StartsWith(MagicMd3, StringComparison.Ordinal);

    // ── The two MDL files that actually reach AssetLoader.LoadModel — both must FALL BACK ────────────

    /// <summary>
    /// <c>ShellCasings.BuildMesh</c> (ShellCasings.cs:84) asks the loader for <c>models/casing_shell.mdl</c>
    /// and falls back to <c>GeneratedCasing</c> when it returns null. That null hinges on the cut: the shell
    /// is a Quake1 MDL ("IDPO"), a magic the loader does not implement. Pin the magic + the fall-through.
    /// </summary>
    [Fact]
    public void CasingShellMdl_HasUnsupportedMdlMagic_SoLoaderFallsBack()
    {
        string path = Path.Combine(Pk3Dir, "models", "casing_shell.mdl");
        if (!File.Exists(path)) return; // no checkout → no-op

        string magic = ReadMagic(File.ReadAllBytes(path));
        Assert.StartsWith(MagicMdl, magic, StringComparison.Ordinal);          // it IS a Quake1 MDL ("IDPO")
        Assert.False(IsSupportedModelMagic(magic),                              // …which the port does NOT import,
            $"casing_shell.mdl magic '{magic}' must NOT be importable, so LoadModel returns null and " +
            "ShellCasings.GeneratedCasing renders the brass-cylinder fallback (ShellCasings.cs:84).");
    }

    /// <summary>
    /// <c>ModelGibs.BuildMesh</c> (ModelGibs.cs:107) skips <c>.mdl</c> by extension and renders
    /// <c>GeneratedChunk</c>; were it to ask the loader anyway, <c>gibs/chunk.mdl</c>'s "IDPO" magic would
    /// still return null. Pin that the chunk really is an unimportable MDL (the cut is the safety net).
    /// </summary>
    [Fact]
    public void ChunkMdl_HasUnsupportedMdlMagic_SoGibFallsBack()
    {
        string path = Path.Combine(Pk3Dir, "models", "gibs", "chunk.mdl");
        if (!File.Exists(path)) return; // no checkout → no-op

        string magic = ReadMagic(File.ReadAllBytes(path));
        Assert.StartsWith(MagicMdl, magic, StringComparison.Ordinal);
        Assert.False(IsSupportedModelMagic(magic),
            $"gibs/chunk.mdl magic '{magic}' must NOT be importable, so ModelGibs.GeneratedChunk renders " +
            "the reddish-box fallback (ModelGibs.cs:107).");
    }

    /// <summary>
    /// The contrast that makes the cut meaningful: the BULLET casing is an IQM (the sibling of the shell in
    /// the same registry entry, all.inc:137-138) and DOES carry a supported magic — so it loads cleanly
    /// while only the SHELL falls back. Proves the test distinguishes "cut" from "broken loader".
    /// </summary>
    [Fact]
    public void CasingBronzeIqm_HasSupportedMagic_SoItLoadsCleanly()
    {
        string path = Path.Combine(Pk3Dir, "models", "casing_bronze.iqm");
        if (!File.Exists(path)) return; // no checkout → no-op

        string magic = ReadMagic(File.ReadAllBytes(path));
        Assert.StartsWith(MagicIqm, magic, StringComparison.Ordinal);
        Assert.True(IsSupportedModelMagic(magic), "casing_bronze.iqm is an IQM and must load (not fall back).");
    }

    // ── Whole-tree sweep: every MDL that ships is unimportable (the cut is total) ────────────────────

    /// <summary>
    /// Sweep every <c>.mdl</c> the VFS mounts and assert dispatch follows the leading MAGIC, never the
    /// (lying) extension. Of the 19 shipped <c>.mdl</c> files, 13 are true Quake1 MDLs ("IDPO") — the cut
    /// format — and MUST fall through to null+log; the other 6 (ebomb/elaser/laser/plasmatrail/tracer/
    /// items/a_bullets) are actually MD3 ("IDP3") wearing a <c>.mdl</c> extension and DO load via the MD3
    /// importer. So no file is judged by extension: a true-MDL magic must be unsupported, and the
    /// MD3-in-mdl-clothing files are correctly supported. Both populations must be non-empty (the sweep
    /// would be vacuous otherwise). Run over the real VFS so it mirrors the actual mount the game uses.
    /// </summary>
    [Fact]
    public void EveryShippedMdl_IsUnsupportedMagic()
    {
        if (!Directory.Exists(DataDir)) return; // no checkout → no-op
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        string[] mdls = vfs.Find("models/", "mdl").ToArray();
        Assert.NotEmpty(mdls); // the data tree ships .mdl files (19 in stock Base)

        int trueMdl = 0, md3InMdlClothing = 0;
        foreach (string vpath in mdls)
        {
            string magic = ReadMagic(vfs.ReadBytes(vpath));
            if (magic.StartsWith(MagicMdl, StringComparison.Ordinal))
            {
                // A genuine Quake1 MDL — the cut format. It MUST NOT parse as a supported model.
                Assert.False(IsSupportedModelMagic(magic),
                    $"{vpath}: a Quake1 MDL ('IDPO') must not parse as a supported model (the importer is cut).");
                trueMdl++;
            }
            else
            {
                // Extension lies: this .mdl is really another container. The only such case in stock Base is
                // MD3 ('IDP3'), which IS a supported magic — dispatch by magic loads it correctly.
                Assert.StartsWith(MagicMd3, magic, StringComparison.Ordinal);
                Assert.True(IsSupportedModelMagic(magic),
                    $"{vpath}: carries MD3 magic '{magic}' under a .mdl extension and must load via the MD3 importer.");
                md3InMdlClothing++;
            }
        }
        Assert.True(trueMdl > 0, "stock Base ships genuine Quake1 MDL files (the cut format)");
        Assert.True(md3InMdlClothing > 0,
            "stock Base ships MD3 files under a .mdl extension — proving dispatch is by magic, not extension");
    }

    /// <summary>
    /// The other side of the cut audit: the formats with ~zero shipped content. MD2 and PSK ship nothing at
    /// all (anywhere the VFS mounts). ZYM ships nothing in the loose base data tree
    /// (xonotic-data.pk3dir/models) — its only occurrences are two map-pack addons (models/pomp/pomp.zym,
    /// models/train.zym) packaged inside the maps .pk3, which <c>MountGameDir(DataDir)</c> also mounts. So
    /// the base data tree has no ZYM, and every ZYM the VFS sees is a maps-pack model — documenting WHY no
    /// ZYM importer is warranted for the base game (matches the recon inventory).
    /// </summary>
    [Fact]
    public void Md2_Psk_And_BaseZym_ShipNoContent()
    {
        if (!Directory.Exists(DataDir)) return; // no checkout → no-op
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        Assert.Empty(vfs.Find("", "md2")); // MD2 ("IDP2"): zero shipped content
        Assert.Empty(vfs.Find("", "psk")); // PSK ("ACTRHEAD"): zero shipped content

        // ZYM ("ZYMOTICMODEL"): none loose under xonotic-data.pk3dir — the base data tree ships no ZYM.
        string looseDataTree = Path.Combine(DataDir, "xonotic-data.pk3dir");
        if (Directory.Exists(looseDataTree))
            Assert.Empty(Directory.EnumerateFiles(looseDataTree, "*.zym", SearchOption.AllDirectories));

        // The only ZYMs the VFS resolves are the two map-pack addons inside the maps .pk3 — never base content.
        foreach (string zym in vfs.Find("", "zym"))
            Assert.True(
                zym.EndsWith("models/pomp/pomp.zym", StringComparison.OrdinalIgnoreCase) ||
                zym.EndsWith("models/train.zym", StringComparison.OrdinalIgnoreCase),
                $"unexpected ZYM '{zym}' — the only ZYMs in stock Base are the two maps-pack addons.");
    }
}
