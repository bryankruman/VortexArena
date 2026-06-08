using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Checkpoints panel — port of Base/.../qcsrc/client/hud/panel/checkpoints.qc (HUD panel #27). The QC version
/// draws the persistent list of stored checkpoint split lines for the current race run — the
/// <c>race_checkpoint_splits[]</c> array, one colored string per checkpoint passed (built by the race timer's
/// <c>MakeRaceString</c> + speed text and stashed via <c>StoreCheckpointSplits</c>) — stacked top-to-bottom
/// (or bottom-up when <c>hud_panel_checkpoints_flip</c>), word-wrapped to the panel width with an optional
/// alignment, at <c>hud_panel_checkpoints_fontscale</c>.
///
/// The split strings are produced server-side / by the race net path, so the net layer pushes them here via
/// <see cref="SetSplits"/> / <see cref="StoreSplit"/> (the analogue of QC <c>StoreCheckpointSplits</c>),
/// indexed by checkpoint number (0/255 = finish). The panel just renders the non-empty entries in order,
/// honoring <see cref="Flip"/>, <see cref="Align"/> and <see cref="FontScale"/>. QC word-wrapping is
/// approximated by drawing one line per split (already short).
/// </summary>
public partial class CheckpointsPanel : HudPanel
{
    /// <summary>QC <c>hud_panel_checkpoints_flip</c>: stack the splits bottom-up instead of top-down.</summary>
    public bool Flip { get; set; }

    /// <summary>QC <c>hud_panel_checkpoints_align</c>: 0 = left, 1 = right (fraction in between).</summary>
    public float Align { get; set; } = 1f;

    /// <summary>QC <c>hud_panel_checkpoints_fontscale</c>: text-size multiplier (relative to the base size).</summary>
    public float FontScale { get; set; } = 1.1f;

    // QC race_checkpoint_splits[256]: one stored split string per checkpoint (null/empty = unused). We keep a
    // sparse list of (index, text) so iteration is cheap; index ordering matches QC's "highest cp first" walk.
    private readonly SortedDictionary<int, string> _splits = new();

    // QC CHECKPOINTS_BASE_SIZE: the splits font is 0.75 of the base HUD font height.
    private const float CheckpointsBaseScale = 0.75f;

    /// <summary>This panel only changes when splits are pushed; no per-frame animation.</summary>
    public override bool IsDynamic => false;

    /// <summary>
    /// Replace the whole stored-splits set (QC: the race code repopulating <c>race_checkpoint_splits</c>).
    /// Keys are checkpoint numbers; pass an empty/null value to clear a slot.
    /// </summary>
    public void SetSplits(IEnumerable<KeyValuePair<int, string>>? splits)
    {
        _splits.Clear();
        if (splits is not null)
            foreach (var kv in splits)
                if (!string.IsNullOrEmpty(kv.Value))
                    _splits[kv.Key] = kv.Value;
        QueueRedraw();
    }

    /// <summary>
    /// Store one checkpoint's split line (QC <c>StoreCheckpointSplits</c>): checkpoint 0 maps to the finish
    /// slot (255), matching the QC <c>race_checkpoint ? race_checkpoint : 255</c> indexing.
    /// </summary>
    public void StoreSplit(int checkpoint, string text)
    {
        int slot = checkpoint != 0 ? checkpoint : 255;
        if (string.IsNullOrEmpty(text)) _splits.Remove(slot);
        else _splits[slot] = text;
        QueueRedraw();
    }

    /// <summary>Clear all stored splits (QC <c>ClearCheckpointSplits</c>).</summary>
    public void ClearSplits()
    {
        _splits.Clear();
        QueueRedraw();
    }

    protected override void DrawPanel()
    {
        if (_splits.Count == 0) return;

        DrawBackground();

        float pad = Padding;
        float w = Size2.X - pad * 2f;
        int size = (int)Mathf.Clamp(Size2.Y * 0.12f * CheckpointsBaseScale * Mathf.Max(0.125f, FontScale), 9f, 22f);
        float lineH = size + 2f;
        float align = Mathf.Clamp(Align, 0f, 1f);

        // QC walks from the highest checkpoint down (most recent at the listed end); flip reverses the stack
        // direction on screen. We collect the lines highest-first, then place them top-down or bottom-up.
        var lines = new List<string>();
        foreach (var kv in _splits) lines.Add(kv.Value); // ascending; we render so latest reads naturally
        lines.Reverse(); // highest checkpoint first (QC for j = end; j >= 0; --j)

        float y = Flip ? Size2.Y - pad - lineH : pad;
        foreach (string line in lines)
        {
            // Clip to the panel in the active stacking direction (QC: break once the next line won't fit).
            if (Flip ? y < pad : y > Size2.Y - pad - lineH * 0.5f) break;
            DrawAlignedColored(new Vector2(pad, y), w, line, FgColor, size, align);
            y += Flip ? -lineH : lineH;
        }
    }

    /// <summary>
    /// Draw a (possibly <c>^N</c> color-coded) split line, aligned within <paramref name="width"/> by
    /// <paramref name="align"/> (0 = left, 1 = right) — QC's <c>Checkpoints_drawstring</c> alignment offset.
    /// </summary>
    private void DrawAlignedColored(Vector2 pos, float width, string line, Color baseColor, int size, float align)
    {
        var runs = HudText.Parse(line, baseColor);
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        float cx = pos.X + (width - total) * align;
        if (cx < pos.X) cx = pos.X;
        foreach (HudText.Run r in runs)
        {
            DrawText(new Vector2(cx, pos.Y), r.Text, r.Color, size);
            cx += MeasureText(r.Text, size);
        }
    }
}
