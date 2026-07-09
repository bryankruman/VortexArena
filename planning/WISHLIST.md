# XonoticGodot — Wishlist / shelved items

Deferred, non-blocking polish. Each entry: what it is, why it's shelved, and a sketch of how to do it.

## Menu

### Condensed-font labels (horizontal text squeeze)
**What:** Xonotic's `item/label.qc` has four text-overflow strategies — fit → **condense** → cut → wrap. When a
label's text is too wide and cutting/wrapping aren't allowed, the engine *squeezes the glyphs horizontally*
(`fs.x *= condenseFactor`, `draw_fontscale.x *= condenseFactor`, factor = `availWidth / textWidth`) so the text
always fits on one line without truncating. Used pervasively (every label, plus the gametype / weapon / language
guide lists).

**Why shelved:** Godot's `Label` only offers clip / ellipsis (`TextOverrunBehavior`) / autowrap — there is no
horizontal-scale ("condense") mode. Our labels currently fit, ellipsize, or wrap instead of condensing, which is
visually close in almost all cases (XonoticGodot labels rarely overflow). Implementing a true squeeze is disproportionate
to the payoff right now.

**How to do it later:** replace the affected `Label`s with a custom `Control` that draws text via
`Font.DrawString` / `RenderingServer.CanvasItemAdd*` under a non-uniform `Transform2D` (x-scale = condenseFactor),
or drive a `Label`/`RichTextLabel` through a `Control.Scale` of `(condenseFactor, 1)` after measuring with
`Font.GetStringSize` — measure on resize, like `Label_recalcPositionWithText`. The condense factor and the QC
fit/cut/wrap precedence are already documented in `label.qc`.

> Other faithful `menu/draw.qc` techniques are implemented: `draw_ButtonPicture` / `draw_VertButtonPicture`
> (`ButtonPictureStyleBox` / `VertButtonPictureStyleBox`), `draw_BorderPicture` (skin styleboxes), colour-coded
> text (`MenuColorCodes` → `RichTextLabel`), the skinned cursor, clipping (`ClipContents`/`ScrollContainer`), and
> the box-space transform/alpha model (Godot containers + `Modulate`). See the `rebirth-menu-skin` memory.
