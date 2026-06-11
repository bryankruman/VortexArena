# HUD Parity Refactor — Implementation Contract

Architect contract for the feature-complete HUD parity refactor of the XonoticGodot C# port.
All file paths are absolute. Reference QC = `C:\Users\Bryan\Projects\Xonotic\Base\data\xonotic-data.pk3dir\qcsrc\client\hud\`.
Reference skin defaults = `hud_luma.cfg` + `_hud_common.cfg` + `_hud_descriptions.cfg`.

This is the single source of truth for the parallel implementation fan-out. Read §5 (FILE OWNERSHIP MAP)
to know exactly which files you own. The **foundation** files must land FIRST (single owner); every other
work item is a disjoint file so panel agents never collide.

---

## 0. Goals & invariants

Add to the live HUD (`game/hud/`):

1. **HUD skin system** — 9-slice border-image frames (`draw_BorderPicture` successor) + skin background, loaded
   from `gfx/hud/<skin>/` (default skin `luma`, falling through to `gfx/hud/default/`) via the existing
   `TextureCache`.
2. **Live per-panel config** — panels read layout + look from `hud_panel_<id>_*` cvars each frame (throttled),
   with the luma reference defaults baked in. This **replaces** the hardcoded `Hud.Layout()` anchors and the
   hardcoded `HudPanel.BgColor/FgColor/Padding/FontSize` constants.
3. **Xolonium font** in all HUD text, not `ThemeDB.FallbackFont`.
4. **Self-declaring panels** — each panel declares its own id + default layout and is discovered automatically,
   so panel work parallelizes on disjoint files (no panel agent edits `HudManager.cs`). See §2 for the chosen
   mechanism (a static registry + virtual members; reflection used once at registry-build time).

### MUST NOT REGRESS (the port's own additions — preserve verbatim behavior)
These are NOT in stock Xonotic; they are port extensions that must keep working through the refactor:
- **`FpsPanel`** (`game/hud/FpsPanel.cs`) — DP `cl_showfps`/`showfps`, self-gating, debug-default-on via
  `CvarService.IsModified`. Full-viewport rect, right-aligns itself bottom-right.
- **`PingPanel`** (`game/hud/PingPanel.cs`) — `cl_showping`/`showping`, stacked one row above FPS, fed via
  `PingPanel.PingProvider` (ENet RTT). Self-gating.
- **`VignetteOverlay`** (`game/client/VignetteOverlay.cs`) — `cl_vignette*`, its own `CanvasLayer(1)`; not a
  `HudPanel`. Untouched by this refactor except that its `RegisterDefaults` call site in `ClientSettings`
  stays.
- **`WorldTint`** (`game/client/WorldTint.cs`) — `r_map_tint*`/`r_scene_tint*` global shader uniforms; not a
  HUD concern, leave wiring intact.
- **`FrameProfiler` hooks** (`game/client/FrameProfiler.cs`) — `Hud._Process` opens
  `FrameProfiler.Scope("hud.mgr")`. Keep that scope; new per-panel draw cost may add `Scope("hud.<id>")`
  inside `HudManager` but the existing `hud.mgr` scope must remain.
- **`MinigameRenderer` + `MinigameMenu`** (`game/hud/MinigameRenderer.cs`, `game/hud/MinigameMenu.cs`) — NOT
  `HudPanel`s (they capture clicks/keys). They are added directly to the `Hud` CanvasLayer, not through the
  panel registry, and the Pong key-drive in `Hud.DrivePongKeys` stays. The new PANEL_SHOW gating must treat
  "minigame menu open" as a first-class state (see §2.3).

---

## 1. NEW base API — `HudPanel.cs` (FOUNDATION, single owner)

`game/hud/HudPanel.cs` is rewritten to be skin- and cvar-aware. The current hardcoded constants
(`BgColor`, `FgColor`, `Padding`, `FontSize`) and the `Configure(Rect2)` flow are **replaced** by a
config snapshot loaded from cvars each frame. Existing panel subclasses keep working because the shared draw
helpers keep the same names/signatures (they just read the live config instead of constants).

### 1.1 Identity + default layout (the self-declaration surface)

```csharp
public abstract partial class HudPanel : Control
{
    /// QC panel.panel_name = strtolower(#id). The cvar prefix is "hud_panel_" + PanelId.
    /// e.g. "healtharmor", "weapons", "ammo", "notify" (NOT "notification"), "modicons".
    public abstract string PanelId { get; }

    /// QC PANEL_SHOW_* (BIT0 maingame / BIT1 minigame / BIT2 mapvote / BIT3 with-scoreboard).
    /// Default = MainGame only. Overlays (scoreboard/notify/vote/chat) widen this.
    public virtual PanelShow ShowFlags => PanelShow.MainGame;

    /// QC PANEL_CONFIG_* — whether the panel is user-toggleable (CanBeOff) / editor-movable (Main).
    public virtual PanelConfig ConfigFlags => PanelConfig.Main;

    /// The luma reference defaults for this panel, in NORMALIZED 0..1 viewport fractions
    /// (QC hud_panel_<id>_pos / _size). Used to seed the cvar defaults (ClientSettings) AND as the
    /// fallback when a cvar is unset. `viewport` is the live visible-rect size in case a panel wants a
    /// pixel-derived default (rare; most return constants).
    /// Return value: (posFraction, sizeFraction), each component in [0,1].
    public abstract PanelLayoutDefault DefaultLayout(Vector2 viewport);

    /// Per-panel background-image default (QC the panel's own hud_panel_<id>_bg in luma, e.g.
    /// "border_default_south", "border_tab_south", "0", or "" to inherit hud_panel_bg). Default "" (inherit).
    public virtual string DefaultBg => "";

    /// Optional per-panel bg_color / bg_alpha overrides (scoreboard sets "0 0.3 0.5" / 0.7). "" = inherit.
    public virtual string DefaultBgColor => "";
    public virtual string DefaultBgAlpha => "";
    public virtual string DefaultBgBorder => "";   // e.g. healtharmor/modicons "4"

    /// Master enable default (the hud_panel_<id> show-mode cvar 0..3). luma per-panel default.
    public virtual string DefaultEnable => "1";

    /// Whether contents change every frame (the existing IsDynamic). Unchanged.
    public virtual bool IsDynamic => true;
}
```

Supporting types (declared in `HudPanel.cs` or a tiny shared file — see §5; keep them in `HudPanel.cs` to
avoid a cross-file dependency for panel agents):

```csharp
[System.Flags]
public enum PanelShow { None = 0, MainGame = 1, Minigame = 2, Mapvote = 4, WithScoreboard = 8 }

[System.Flags]
public enum PanelConfig { No = 0, Main = 1, CanBeOff = 2 }

public readonly record struct PanelLayoutDefault(Vector2 PosFraction, Vector2 SizeFraction);
```

### 1.2 Per-frame config load (QC `HUD_Panel_LoadCvars`)

`HudPanel` owns a resolved snapshot and reloads it on a throttle (QC `update_time` / `hud_panel_update_interval`
= 2s; every frame in editor mode — we have no editor, so reload every `_ConfigReloadInterval` seconds, default
0.5s, and immediately when any `hud_panel_*` cvar changes — see §1.6). The manager calls `LoadConfig` once per
frame before `_Draw`; cheap because it short-circuits on the throttle.

```csharp
/// The resolved, pre-fade per-panel state (QC current_panel_*). Pixel-space.
protected readonly record struct PanelConfig2
{
    public Vector2 PosPx { get; init; }
    public Vector2 SizePx { get; init; }
    public string Bg { get; init; }          // resolved border-image NAME ("0" = none), or "" = none
    public Color BgColor { get; init; }
    public float BgAlpha { get; init; }      // base (pre-fade)
    public float BgBorder { get; init; }     // pixels
    public float Padding { get; init; }      // clamped: min(min(size)*0.5-5, padding)
    public float FgAlpha { get; init; }      // base (pre-fade)
    public int FontSize { get; init; }       // resolved hud_panel_<id>_fontsize or hud_fontsize
    public bool Enabled { get; init; }       // hud_panel_<id> != 0
}

/// Reload from cvars if the throttle elapsed; otherwise reuse the snapshot. `viewport`, `conScale`,
/// `fadeAlpha` (hud_fade_alpha), `panelFade` (panel_fade_alpha) are supplied by the manager each frame.
/// Stores PRE-fade values in the snapshot; applies fade to the live BgAlpha/FgAlpha every call.
public void LoadConfig(Vector2 viewport, float fadeAlpha, float panelFade);

/// Live (post-fade) values read by DrawBackground/draw helpers this frame.
protected PanelConfig2 Cfg { get; private set; }     // pre-fade snapshot
protected float LiveBgAlpha { get; private set; }    // Cfg.BgAlpha * fade
protected float LiveFgAlpha { get; private set; }    // Cfg.FgAlpha * fade
```

`LoadConfig` resolution rules (mirror QC `HUD_Panel_Get*`, see READER 5 §1):
- `pos`/`size` cvars are normalized 0..1 → multiply by `viewport` to get pixels. If unset → `DefaultLayout`.
- `_bg`: `""` → inherit global `hud_panel_bg`; `"0"` → no frame; else the border-image name. (We do NOT honor
  config-mode "show transparently" — no editor.)
- `_bg_color`: `""` → global `hud_panel_bg_color`; `"shirt"`/`"pants"` → player palette colors (use the same
  team/palette lookup the menu uses); else parse `"r g b"`. Team blend via `_bg_color_team` (when teamplay &&
  factor>0 → blend toward the local team color).
- `_bg_alpha`/`_bg_border`/`_bg_padding`: `""` → global default; else `float`. Padding clamped
  `min(min(size.x,size.y)*0.5 - 5, padding)`.
- `_fg_alpha`: global `hud_panel_fg_alpha` (there is no per-panel fg cvar in stock; keep a per-panel override
  read but default to global).
- `_fontsize`: per-panel `hud_panel_<id>_fontsize` if present, else global `hud_fontsize` (11).
- `Enabled`: `ConfigFlags has CanBeOff ? cvar("hud_panel_<id>") != 0 : true`.
- After resolving, set `Position`/`Size` to `PosPx`/`SizePx` (so child draw coords stay panel-local, as today).

### 1.3 Skin-aware background draw (QC `HUD_Panel_DrawBg` → `draw_BorderPicture`)

```csharp
/// Paint the panel chrome: resolve the border image via the skin, draw the 9-slice expanded by BgBorder on
/// every side (corner-slice = BORDER_MULTIPLIER(4) * BgBorder), tinted Cfg.BgColor at LiveBgAlpha. No-op when
/// Bg is "" or "0". Replaces the old DrawRect-only DrawBackground().
protected void DrawBackground();

/// Same but over an explicit local rect (kept for panels that frame a sub-region). Falls back to a flat
/// translucent DrawRect only when no border texture resolves (so the HUD still reads if art is missing).
protected void DrawBackground(Rect2 local);
```

The 9-slice primitive lives in `HudSkin` (§4 / §5 new file). `DrawBackground` calls:

```csharp
HudSkin.DrawBorderPicture(this, originLocal, Cfg.Bg, sizeExpanded, Cfg.BgColor, LiveBgAlpha, sliceSize);
```

`HudSkin.DrawBorderPicture` resolves `gfx/hud/<skin>/<bg>` → `gfx/hud/<skin>/border_default` →
`gfx/hud/default/border_default` via `TextureCache.GetFirst`, then issues 9 `DrawTextureRectRegion` calls on
the panel `CanvasItem` using the {0,.25,.75}×{.25,.5,.25} UV grid (READER 5 §2). Degenerate-size variants
(border<0, border==0, panel<2·border) handled per QC `lib/draw.qh:43-116`.

### 1.4 Font accessor (Xolonium) + skin-pic helper

```csharp
/// The Xolonium HUD font, wired by the host (see §4). Falls back to ThemeDB.FallbackFont if not yet set.
public static Font? HudFont { get; set; }
protected static Font Font => HudFont ?? ThemeDB.FallbackFont;

/// Draw a skin/art pic by bare VFS name (extension-agnostic), best-first over the skin→default chain.
/// Returns false if nothing resolved (caller draws its colored-box fallback). Modulated by color (alpha
/// folds in LiveFgAlpha at the call site as needed).
protected bool DrawSkinPic(string bareName, Rect2 local, Color modulate);

/// Same, taking an explicit candidate list (skin, default, res:// override) — wraps TextureCache.GetFirst.
protected bool DrawSkinPicFirst(Rect2 local, Color modulate, params string?[] candidates);
```

All existing text helpers (`DrawText`, `DrawTextCentered`, `DrawTextRight`, `MeasureText`) **change their
`ThemeDB.FallbackFont` references to `Font`** and default `size` to `Cfg.FontSize` (with the int overload
preserved for callers that pass an explicit size). Signatures otherwise unchanged so panel code compiles.

### 1.5 Live-cvar accessors (REAL store API — READER 1)

Use the shared `Api.Cvars` (`ICvarService`) which is `MenuState.Cvars` at runtime. Null-guard `Api.Services`.

```csharp
// Per-panel: read hud_panel_<id>_<suffix> with the "" = inherit / "0" = off semantics.
protected string CvarStr(string suffix);                 // GetString("hud_panel_"+PanelId+"_"+suffix)
protected float  CvarF(string suffix, float fallback);   // unset-vs-0 guard (GetString first)
protected bool   CvarBool(string suffix);                // CvarF(suffix,0) != 0

// Global HUD cvars (hud_panel_bg, hud_fontsize, hud_skin, hud_panel_update_interval, ...).
protected static string GlobalStr(string name);
protected static float  GlobalF(string name, float fallback);

// The unset-vs-explicit-0 guard (VignetteOverlay.CvarF pattern):
//   string raw = Api.Cvars.GetString(full); return IsNullOrWhiteSpace(raw) ? fallback : Api.Cvars.GetFloat(full);
```

Color parse helper (space-separated "r g b", 0..1, matching `CvarColorButton.Parse`):
```csharp
protected static bool TryParseRgb(string s, out Color c);   // "" / "0" handled by caller
```

### 1.6 Change reaction

Panels **poll** (the idiomatic choice — FpsPanel/PingPanel/VignetteOverlay all poll). `LoadConfig`'s throttle
makes this cheap. Optionally, `HudPanel` subscribes once to `CvarService.Changed` in `_EnterTree`/unsub in
`_ExitTree` and, on any name starting `hud_panel_`/`hud_skin`/`hud_fontsize`, forces `_configDirty = true` so
the next `LoadConfig` reloads immediately (leak-safe per `CvarControls.cs:114`). This gives instant menu
feedback without per-frame cvar churn. Skin change also calls `TextureCache.Clear()`.

### 1.7 Removed / changed members (migration notes for panel agents)
- `BgColor`, `FgColor`, `Padding`, `FontSize` constants → **removed**. Replace `BgColor` usage with
  `Cfg.BgColor`/`LiveBgAlpha`; `FgColor` with `new Color(1,1,1,LiveFgAlpha)`; `Padding` with `Cfg.Padding`;
  `FontSize` with `Cfg.FontSize`. (`NumColor`/`ToColor`/`SecondsToString`/`DrawBar` keep their names; `NumColor`
  now uses `LiveFgAlpha` for its alpha.)
- `Configure(Rect2)` → **removed** from the public surface; the manager no longer pushes rects. `Position`/`Size`
  are set inside `LoadConfig`. (Crosshair/Fps/Ping which wanted a full-viewport rect: their `DefaultLayout`
  returns `(0,0),(1,1)` so they get the whole viewport.)
- `Size2` accessor stays (now returns `Cfg.SizePx`).

---

## 2. `HudManager.cs` changes (FOUNDATION, single owner)

### 2.1 Registration / discovery mechanism — CHOSEN: static registry + reflection-once

**Decision:** a process-wide static registry (`HudRegistry`) that is populated ONCE by reflecting over all
non-abstract `HudPanel` subclasses in the loaded assembly, instantiating each, and reading its self-declared
`PanelId`/`ShowFlags`/`ConfigFlags`/`DefaultLayout`. `HudManager` then iterates the registry to build its panel
list. This is the cleanest of the two options because:
- Panel agents add a NEW file with a `HudPanel` subclass and **never touch `HudManager.cs`** — discovery is
  automatic (the "disjoint files" requirement). No central list to edit, no merge conflicts.
- Reflection runs exactly once (registry build), not per frame — no hot-path cost.
- The few panels the net layer feeds by strongly-typed handle (`Scoreboard`, `CenterPrint`, `Notify`,
  `Timer`, `InfoMessages`, `Weapons`, `Crosshair`, `Physics`, etc.) are exposed via typed accessors that
  `HudManager` resolves FROM the registry by type (`Get<ScoreboardPanel>()`), so those handles survive without
  a hand-maintained `new XxxPanel()` block.

```csharp
public static class HudRegistry
{
    /// Reflect all concrete HudPanel subclasses once; cache their Type list (skip MinigameRenderer/MinigameMenu
    /// — those are NOT HudPanel). Stable order: by a [HudPanelOrder] attribute if present, else by the QC
    /// _hud_panelorder list, else alphabetical PanelId.
    public static IReadOnlyList<System.Type> PanelTypes { get; }   // built lazily on first access

    public static HudPanel Create(System.Type t);   // Activator.CreateInstance
}
```

Optional ordering attribute (kept in `HudPanel.cs` so it needs no extra file):
```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class HudPanelOrderAttribute : Attribute { public int Order; public HudPanelOrderAttribute(int o)=>Order=o; }
```
Default z-order follows QC `_hud_panelorder` (READER 4 §2 list); panels with no attribute sort after, by id.
Draw order = reverse of panel-order so order[0] ends on top (QC `HUD_Main` loop), then overlays (radar maxed,
chat maxed, quickmenu, scoreboard) always last.

`HudManager._Ready` becomes:
```csharp
foreach (System.Type t in HudRegistry.PanelTypes)
    Add(HudRegistry.Create(t));     // Add() parents under the CanvasLayer + records in _panels
// resolve typed handles from the built list:
HealthArmor = Get<HealthArmorPanel>(); Ammo = Get<AmmoPanel>(); ... Scoreboard = Get<ScoreboardPanel>(); ...
// Minigame overlays are NOT HudPanels — add them explicitly, as today.
Minigame = new MinigameRenderer{...}; AddChild(Minigame);
MinigameMenu = new MinigameMenu{...}; AddChild(MinigameMenu);
ApplyPlayer();
GetViewport().SizeChanged += OnViewportResized;   // forces all panels _configDirty (re-resolve pixels)
```
`Get<T>()` returns the registered instance of that type (or null → the typed property may be null only if the
panel file was removed; guard `ApplyPlayer` as today). **No `Layout()` anchor block remains** — pos/size are
per-panel cvar-driven via `LoadConfig`.

### 2.2 Per-frame config-driven layout + draw

`Hud._Process` (keep `FrameProfiler.Scope("hud.mgr")`):
```csharp
float fade = ComputeHudFadeAlpha();              // QC hud_fade_alpha: 1 - _menu_alpha (1 if exit-menu open)
Vector2 vp = GetViewport().GetVisibleRect().Size;
SyncSkin();                                       // if hud_skin changed: WeaponHud.HudSkin = ...; TextureCache.Clear()
foreach (HudPanel p in _panels)
{
    bool show = ResolveShow(p, out float panelFade);   // §2.3 gating → sets Visible
    p.Visible = show;
    if (!show) continue;
    p.LoadConfig(vp, fade, panelFade);                 // resolves pos/size/bg/alpha/font (throttled)
    if (p.IsDynamic) p.QueueRedraw();
}
DrivePongKeys();
```
`ComputeHudFadeAlpha`: read `_menu_alpha` (the menu VM cvar; 0 closed..1 open) → `1 - _menu_alpha`. When fully
faded AND scoreboard fully up, the manager may early-out (QC `HUD_Main` return) but keep crosshair/centerprint
exceptions simple — just multiply alpha.

### 2.3 PANEL_SHOW gating (QC `HUD_Panel_Draw`, READER 5 §4)

Precedence (mutually exclusive, top wins): **scoreboard → minigame → mapvote/intermission → maingame.**
```csharp
bool ResolveShow(HudPanel p, out float panelFade)
{
    panelFade = 1f;
    PanelShow f = p.ShowFlags;
    bool show;
    if (ScoreboardUp && f.HasFlag(PanelShow.WithScoreboard))        show = true;        // SB cross-fade
    else if (MinigameMenuOpen && f.HasFlag(PanelShow.Minigame))     show = true;
    else if (Intermission == 2 && f.HasFlag(PanelShow.Mapvote))     show = true;
    else if (!(MinigameMenuOpen || Intermission == 2) && f.HasFlag(PanelShow.MainGame)) show = true;
    else                                                            show = false;
    if (show && !f.HasFlag(PanelShow.WithScoreboard))
    {
        panelFade = 1f - ScoreboardFade;                            // non-SB panels fade OUT as SB fades IN
        if (panelFade <= 0f) show = false;
    }
    return show;
}
```
Manager state inputs (settable by the net/match layer, mirroring today's data-injection surface):
- `public float ScoreboardFade { get; set; }` (0..1) and `bool ScoreboardUp => ScoreboardFade > 0`.
- `public bool MinigameMenuOpen` (true while `MinigameMenu.Visible`, computed from the live menu).
- `public int Intermission { get; set; }` (2 = mapvote/intermission).
The **port extras** (`FpsPanel`, `PingPanel`) keep `ShowFlags = MainGame|WithScoreboard|Minigame|Mapvote`
(always visible) AND remain self-gating internally via their own cvar (gating is additive: a self-gated panel
that decides to draw still respects PANEL_SHOW, but FPS/Ping want to show in all contexts). Crosshair shows in
maingame only (hidden under SB/menu) per QC.

### 2.4 Fade
`hud_fade_alpha` (menu) × `panel_fade_alpha` (scoreboard cross-fade) multiply into each panel's bg/fg alpha
inside `LoadConfig` (the manager passes both). No fade is baked into the snapshot — it is re-applied each frame
so fades animate between throttled reloads (QC behavior).

### 2.5 Keep the port extras
`MinigameRenderer`, `MinigameMenu`, `FpsPanel`, `PingPanel`, the `PongMoveSink`/`DrivePongKeys` path, and the
typed net-injection handles all survive. `RadarPanel.cs` exists but is currently unwired — once it has a
`PanelId`/`DefaultLayout` it is auto-discovered (it becomes a real panel for free; see §6).

---

## 3. Cvar list — what `ClientSettings` registers + panels read

All registered into `MenuState.Cvars` (= `Api.Cvars` at runtime) via a new
`HudConfig.RegisterDefaults(ICvarService)` called from `ClientSettings.ApplyAll()`. Idempotent (`Register`
keeps any cfg/user value). `Save` (=`seta`/archived) for everything the editor/menu writes.

### 3.1 Generic / global skin cvars (luma defaults — READER 4 §1)
```
hud_skin                  "luma"
hud_fontsize              "11"
hud_width                 "560"
hud_panel_update_interval "2"
hud_panel_bg              "0"            // default: no frame; panels opt in by name
hud_panel_bg_color        "0 0.14 0.25"
hud_panel_bg_color_team   "1"
hud_panel_bg_alpha        "1"
hud_panel_bg_border       "2"
hud_panel_bg_padding      "3"
hud_panel_fg_alpha        "1"
hud_dock                  "0"
hud_dock_color            "0 0 0"
hud_dock_color_team       "1"
hud_dock_alpha            "1"
hud_progressbar_alpha          "0.6"
hud_progressbar_health_color   "0.83 0.12 0"
hud_progressbar_armor_color    "0.28 0.8 0"
hud_progressbar_fuel_color     "0.77 0.67 0"
hud_progressbar_oxygen_color   "0.1 1 1"
hud_progressbar_strength_color "1 0.39 0"
hud_progressbar_shield_color   "0.36 1 0.07"
hud_progressbar_speed_color    "0.77 0.67 0"
hud_progressbar_acceleration_color     "0.2 0.65 0.93"
hud_progressbar_acceleration_neg_color "0.86 0.35 0"
hud_speed_unit            "1"
```

### 3.2 Generic per-panel cvars (registered for EVERY panel id, with the panel's own defaults)
`HudConfig.RegisterDefaults` iterates `HudRegistry.PanelTypes`; for each panel id `<id>` it registers:
```
hud_panel_<id>            <DefaultEnable>                 // show-mode 0..3 (master toggle)
hud_panel_<id>_pos        "<posX> <posY>"                 // from DefaultLayout, normalized
hud_panel_<id>_size       "<sizeX> <sizeY>"               // from DefaultLayout, normalized
hud_panel_<id>_bg         <DefaultBg>                     // "" inherit / "0" off / "border_*"
hud_panel_<id>_bg_color   <DefaultBgColor>                // "" inherit
hud_panel_<id>_bg_alpha   <DefaultBgAlpha>                // "" inherit
hud_panel_<id>_bg_border  <DefaultBgBorder>               // "" inherit
hud_panel_<id>_bg_color_team ""                           // "" inherit
hud_panel_<id>_bg_padding ""                              // "" inherit
hud_panel_<id>_fg_alpha   ""                              // "" inherit (no stock per-panel fg; keep readable)
hud_panel_<id>_fontsize   ""                              // "" → hud_fontsize
```
(`_fg_alpha`/`_fontsize` are port-readable conveniences; stock has no generic per-panel fg/font, but reading
them lets a config opt in. The menu won't write `_pos`/`_size`/`_fg_alpha`/`_fontsize` — READER 2 — so the
DefaultLayout values are the live source for position/size unless a cfg sets them.)

### 3.3 Per-panel layout + look defaults (luma — READER 4 §2)
Each panel's `DefaultLayout`/`DefaultBg`/`DefaultBgBorder`/`DefaultEnable` returns these; `HudConfig` bakes
them as the registered defaults. Pos/size normalized 0..1.

| id | pos | size | bg | bg_border | enable |
|---|---|---|---|---|---|
| healtharmor | 0.3 0.925 | 0.4 0.07 | border_default_south | 4 | 1 |
| ammo | 0.315 0.865 | 0.37 0.06 | border_tab_south | "" (pad 4) | 1 |
| powerups | 0.325 0.815 | 0.35 0.055 | border_shadow_south | "" | 1 |
| weapons | 0.965 0.125 | 0.035 0.77 | border_default_east | "" (pad 0) | 1 |
| notify | 0.73 0.8 | 0.265 0.2 | 0 | "" | 1 |
| timer | 0.45 0 | 0.1 0.05 | border_plain_north | "" (pad 0) | 1 |
| radar | 0 0 | 0.2 0.25 | border_corner_northwest | "" (pad 1) | 1 |
| score | 0.88 0 | 0.12 0.08 | border_corner_northeast | "" (pad 1) | 1 |
| racetimer | 0.36 0.11 | 0.28 0.09 | 0 | "" | 1 |
| vote | 0.74 0.69 | 0.19 0.09 | border_default | "" | 1 |
| modicons | 0.37 0.03 | 0.26 0.07 | border_fading_north | 4 | 1 |
| pressedkeys | 0.445 0.71 | 0.11 0.09 | border_default | "" (pad 1) | 1 (spectating) |
| chat | 0.01 0.7 | 0.46 0.19 | 0 | "" | 1 |
| engineinfo | 0.93 0.97 | 0.07 0.03 | 0 | "" | 0 (off) |
| infomessages | 0.68 0.1 | 0.28 0.08 | 0 | "" (pad 0) | 1 |
| physics | 0.41 0.625 | 0.18 0.08 | 0 | "" (alpha 0.7) | 3 |
| centerprint | 0.175 0.22 | 0.65 0.22 | 0 | "" | 1 |
| itemstime | 0.03 0.26 | 0.07 0.23 | border_default | "" | 2 |
| quickmenu | 0.6 0.445 | 0.22 0.24 | "" (inherit) | "" | (no enable; CONFIG no off) |
| scoreboard | 0.15 0.15 | 0.7 0.7 | border_default | "" (color "0 0.3 0.5" alpha 0.7) | 1 |
| strafehud | 0.32 0.57 | 0.36 0.02 | 0 | "" (alpha 0.7) | 3 |
| pickup | 0.01 0.945 | 0.26 0.035 | 0 | "" (alpha 1) | 1 |
| checkpoints | 0.7 0.19 | 0.25 0.17 | "" (inherit) | "" | 1 |
| minigamehelp | 0.22 0.78 | 0.50 0.20 | "" | "" | 1 |
| mapvote | 0 0 | 1 1 | border_default | "" | (CONFIG no off) |

### 3.4 Panel-specific behavior cvars (registered + read by the owning panel)
Defaults from READER 4 §2 / READER 2. Panels read these live. (Only the high-value parity set is listed; the
owning panel registers any it consumes.)

- **healtharmor**: `_combined 0`, `_flip 0`, `_progressbar 1`, `_baralign 3`, `_iconalign 3`,
  `_maxhealth 200`, `_maxarmor 200`, `_progressbar_gfx 1`, `_progressbar_gfx_smooth 2`,
  `_progressbar_gfx_damage 5`, `_progressbar_gfx_lowhealth 40`, `_text 1`.
- **ammo**: `_onlycurrent 0`, `_noncurrent_alpha 0.6`, `_noncurrent_scale 0.4`, `_iconalign 0`,
  `_progressbar 0`, `_text 1`, `_maxammo 40`.
- **weapons**: `_timeout 1`, `_timeout_effect 1`, `_timeout_fadebgmin 0.4`, `_timeout_fadefgmin 0.4`,
  `_timeout_speed_in 0.25`, `_timeout_speed_out 0.75`, `_onlyowned 1`, `_noncurrent_alpha 0.8`,
  `_noncurrent_scale 0.9`, `_label 2`, `_label_scale 0.3`, `_accuracy 0`, `_ammo 0`,
  `_ammo_color "0.58 1 0.04"`, `_ammo_alpha 1`, `_aspect 1`, `_complainbubble 1`,
  `_complainbubble_time 0`, `_complainbubble_fadetime 1`,
  `_complainbubble_color_outofammo "0.8 0.11 0"`, `_complainbubble_color_donthave "0.88 0.75 0"`,
  `_complainbubble_color_unavailable "0 0.71 1"`, `_selection_speed 10`.
- **notify**: `_time 10`, `_fadetime 3`, `_flip 0`, `_fontsize 0.8`, `_icon_aspect 1`.
- **physics**: `_flip 0`, `_progressbar 1`, `_baralign 0`, `_speed_vertical 0`, `_speed_max 1800`,
  `_speed_unit_show 1`, `_speed_colored 0`, `_topspeed 1`, `_topspeed_time 4`, `_acceleration_max 1.5`,
  `_acceleration_vertical 0`, `_acceleration_progressbar_mode 0`, `_acceleration_progressbar_scale 1`,
  `_text 1`, `_text_scale 0.7`.
- **score**: `_rankings 1`.
- **strafehud**: `_mode 0`, `_style 2`, `_range 90`, `_unit_show 1`, plus the bar/angle/indicator color+alpha
  triples (`_bar_neutral_color/_alpha`, `_bar_accel_*`, `_bar_overturn_*`, `_angle_*`, `_switch*`, `_wturn*`,
  `_bestangle*`) — READER 2 StrafeHUD block. Private `_hud_panel_strafehud_demo`.
- **pickup**: `_time 3`, `_fade_out 0.15`, `_iconsize 1.5`, `_showtimer 1`.
- **pressedkeys**: `_aspect 1.8`, `_attack 0`. Enable 0/1/2 (0 off / 1 spectating / 2 always).
- **quickmenu**: `_align 0`, `_translatecommands 0`, `_server_is_default 0`. No enable cvar.
- **chat**: ENGINE cvars `con_chatsize 8`, `con_chattime 30`, `con_chatsound 1` (NOT `hud_panel_chat_*`).
- **scoreboard**: `_fadeinspeed 10`, `_fadeoutspeed 5`, `_respawntime_decimals 1`, `_table_bg_alpha 0`,
  `_table_fg_alpha 0.9`, `_table_fg_alpha_self 1`, `_table_highlight 1`, `_table_highlight_alpha 0.2`,
  `_table_highlight_alpha_self 0.4`, `_table_highlight_alpha_eliminated 0.6` (already read live),
  `_accuracy_doublerows 0`, `_itemstats_doublerows 0`.
- **modicons**: `_ca_layout 1`, `_dom_layout 1`, `_freezetag_layout 1` (already read live).
- **itemstime**: `_iconalign 0`, `_progressbar 0`, `_progressbar_reduced 0`, `_text 1`, `_ratio 2`,
  `_dynamicsize 1`, `_progressbar_maxtime 30`. Enable 2.
- **centerprint**: `_align 0.5`, `_flip 0`, `_fontscale 1`, `_fontscale_bold 1.2`, `_fontscale_title 1.3`,
  `_time 3`, `_fade_in 0`, `_fade_out 0.15`.
- **radar**: `_foreground_alpha 1`, `_rotation 0`, `_zoommode 0`, `_scale 8192`, `_maximized_scale 5120`.
- **infomessages**: `_flip 1`.
- **checkpoints**: `_flip 0`, `_align 1`, `_fontscale 1.1`.

---

## 4. Font + skin art

### 4.1 Xolonium load + exposing a Godot `Font` to panels
- Physical: `assets/data/font-xolonium.pk3dir/fonts/xolonium-regular.otf` (+ `-bold.otf`). VFS vpaths
  `fonts/xolonium-regular.otf` / `fonts/xolonium-bold.otf`.
- Loader: `AssetLoader.GetFont("xolonium")` → `FontLoader.GetFont` → `FontFile` (a `Font`). Bold via
  `GetFont("xolonium-bold")`.
- **Wiring (host side, foundation):** at the two sites that already set `TextureCache.VfsResolver` and where
  `_assets` (an `AssetLoader`) is in scope, also set the static font:
  - `game/net/NetGame.cs:837` — add `HudPanel.HudFont = _assets.GetFont("xolonium");`
  - `game/GameDemo.cs:672` — add `Hud.HudFont = _assets.GetFont("xolonium");` (note `HudPanel.HudFont`).
  Also set `HudSkin.BoldFont = _assets.GetFont("xolonium-bold")` for centerprint titles.
- Panels use `Font` (the protected accessor) in every `DrawString`/`GetStringSize`. Fallback to
  `ThemeDB.FallbackFont` if null (assets missing) so nothing crashes.
- `MinigameRenderer`/`MinigameMenu`/`DamageTextLayer` currently use `ThemeDB.FallbackFont` — migrate them to
  `HudPanel.HudFont ?? ThemeDB.FallbackFont` opportunistically (each is a disjoint file; low priority).

### 4.2 Skin art — 9-slice border/bg names (`gfx/hud/default/`, READER 4 §3-4)
- The single 9-slice atlas is **`border_default`** (bare name; `.tga` resolved by `TextureCache`). ALL the
  per-panel `bg` values (`border_default_south`, `border_tab_south`, `border_shadow_south`,
  `border_corner_northwest`, `border_corner_northeast`, `border_fading_north`, `border_plain_north`,
  `border_small`, `border_default_east`) are **virtual variants** — the engine synthesizes them by directional
  cropping of the one `border_default` atlas. **Port approach:** `HudSkin` maps any `border_*` value to the
  `border_default` texture and applies an edge mask derived from the suffix:
  - `_south` → draw top + left + right edges (bottom-anchored panel, no bottom border).
  - `_north` / `_plain_north` / `_fading_north` / `_tab_south` etc. → the analogous edge subset.
  - `_east` → draw the right + top + bottom (left-open).
  - `_corner_northwest` / `_corner_northeast` → only the two edges meeting that corner.
  - bare `border_default` / `border_small` → full 9-slice frame.
  Phase-1 acceptable simplification: render the full 9-slice for ALL variants (visually close; the suffix
  masking is a fidelity follow-up). Resolution chain per draw:
  `gfx/hud/<skin>/<bg>` → `gfx/hud/<skin>/border_default` → `gfx/hud/default/border_default`.
- Selection/highlight frames: `border_highlighted`, `border_highlighted2` (used by weapons selection + the
  unported editor; only weapons selection matters now).
- Dock background: `dock_medium` (drawn full-screen when `hud_dock` enabled).
- Progress bars: `progressbar` (horizontal), `progressbar_vertical`, `accelbar` (physics), `num_leading`.

### 4.3 `key_*` pics for pressedkeys (READER 4 §4)
Bare names under `gfx/hud/default/`, normal + `_inv` (inverted/pressed) variants:
`key_forward`, `key_backward`, `key_left`, `key_right`, `key_jump`, `key_crouch`, `key_atck`, `key_atck2`
(each with a `_inv` companion). PressedKeysPanel draws the `_inv` variant when the key is held.

### 4.4 Other art families the panels consume (bare names, `gfx/hud/<skin>/` → `gfx/hud/default/`)
- Weapons: `weapon<icon>` (icon name via `WeaponHud.IconName`), `weapon_accuracy`, `weapon_ammo`,
  `weapon_complainbubble`, `weapon_current_bg`.
- Ammo: `ammo_shells/bullets/rockets/cells/fuel`, `ammo_current_bg`.
- Health/armor/items: `health`, `health_small/medium/big/mega`, `armor`, `armor_small/medium/big/mega`,
  `shield`, `item_mega_health`, `item_large_armor`.
- Powerups/buffs/nades: `strength`, `shield`, `superweapons`, `jetpack`, `fuelregen`, `buff_*`, `nade_*`.
- Notify: `notify_death/fall/headshot/...` + CTF/dom flag icons + `teamkill_*`.
- Modicons: `flag_<color>_<state>`, `kh_*`, `as_defend/destroy`, `player_<color>`, `ok_weapon_*`.
- Vote: `voteprogress_back/prog/voted`. Race: `race_new*`.

---

## 5. FILE OWNERSHIP MAP (parallel implementation)

Partitioned so **no two parallel agents edit the same file**. Foundation lands FIRST and is a single owner;
then NEW-panel and EXISTING-panel work fans out, each on its own file. Registration is automatic (§2.1), so
**no panel agent edits `HudManager.cs`** — that is the key property the registry buys us.

### 5.1 FOUNDATION — single owner, do first (must all land before fan-out)
| Work item | File(s) |
|---|---|
| New base API (PanelId, ShowFlags, ConfigFlags, DefaultLayout/Bg, LoadConfig, Cfg snapshot, DrawBackground 9-slice call, Font accessor, DrawSkinPic, cvar accessors, enums, HudPanelOrderAttribute) | `game/hud/HudPanel.cs` (rewrite) |
| 9-slice + skin resolution primitive | `game/hud/HudSkin.cs` (**new**) — `DrawBorderPicture`, `ResolveBorderTex`, suffix→edge-mask, `BoldFont`, `DrawSubPic` |
| Cvar defaults registration (generic + per-panel via registry) | `game/hud/HudConfig.cs` (**new**) — `RegisterDefaults(ICvarService)` iterating `HudRegistry` |
| Registry + reflection discovery | `game/hud/HudRegistry.cs` (**new**) — `PanelTypes`, `Create`, order resolution |
| Manager: registry-driven `_Ready`, per-frame `LoadConfig`, PANEL_SHOW gating, fade, typed `Get<T>` handles, keep Minigame/Fps/Ping/Pong | `game/hud/HudManager.cs` (rewrite) |
| Register HudConfig defaults at boot | `game/menu/framework/ClientSettings.cs` (add `HudConfig.RegisterDefaults(MenuState.Cvars)` to `ApplyAll`) |
| Font + skin wiring at host | `game/net/NetGame.cs` (~:837), `game/GameDemo.cs` (~:672) — set `HudPanel.HudFont` + `HudSkin.BoldFont` |

> NOTE on `ClientSettings.cs` / `NetGame.cs` / `GameDemo.cs`: these are shared host files edited ONLY by the
> foundation owner (one-line additions). No panel agent touches them. After foundation lands they are frozen
> for this refactor.

### 5.2 NEW panels — each its own new file (parallel, after foundation)
| Panel | File |
|---|---|
| Score | `game/hud/ScorePanel.cs` (**new**) |
| StrafeHUD | `game/hud/StrafeHudPanel.cs` (**new**) |
| Pickup | `game/hud/PickupPanel.cs` (**new**) |
| QuickMenu | `game/hud/QuickMenuPanel.cs` (**new**) |
| PressedKeys | `game/hud/PressedKeysPanel.cs` (**new**) |
| Chat | `game/hud/ChatPanel.cs` (**new**) |
| MinigameHelp | `game/hud/MinigameHelpPanel.cs` (**new**) |
| EngineInfo (optional, off by default) | `game/hud/EngineInfoPanel.cs` (**new**) |

Each NEW panel: subclass `HudPanel`, implement `PanelId` + `DefaultLayout` + `ShowFlags`, register its
behavior cvars in a `RegisterDefaults` (called from `HudConfig` via a convention or its own `_Ready`), draw.
Auto-discovered — no `HudManager.cs` edit.

### 5.3 EXISTING panels — fidelity upgrade, each its own existing file (parallel, after foundation)
Each adds `PanelId`/`DefaultLayout`/`ShowFlags` overrides + migrates off the removed constants (§1.7) + uses
`DrawBackground` (skin) + `Font` + reads its behavior cvars. One agent per file:
`HealthArmorPanel.cs`, `AmmoPanel.cs`, `WeaponsPanel.cs`, `PowerupsPanel.cs`, `ScoreboardPanel.cs`,
`CenterPrintPanel.cs`, `NotifyPanel.cs`, `CrosshairPanel.cs`, `TimerPanel.cs`, `InfoMessagesPanel.cs`,
`RaceTimerPanel.cs`, `CheckpointsPanel.cs`, `VotePanel.cs`, `ModIconsPanel.cs`, `ItemsTimePanel.cs`,
`PhysicsPanel.cs`, `MapVotePanel.cs`, `RadarPanel.cs` (newly wired via discovery),
`VehicleHud.cs` (not a panel-order panel but uses the skin helpers), `FpsPanel.cs`, `PingPanel.cs`
(only need `PanelId` + full-context `ShowFlags`; keep self-gating).

### 5.4 Shared-file edits a panel "needs" — and how the registry avoids them
- **Registration in `HudManager`**: AVOIDED — auto-discovery (§2.1). A panel exists by existing as a file.
- **Typed handle** (`Hud.Scoreboard` etc.): resolved by `Get<T>()` from the registry, so adding/removing a
  panel does not require editing the handle block beyond the foundation owner's initial set.
- **Cvar default registration**: AVOIDED per-panel-touching-ClientSettings — `HudConfig.RegisterDefaults`
  reflects the registry and reads each panel's virtual `DefaultLayout`/`DefaultBg`/`DefaultEnable`; behavior
  cvars are registered by the panel's own `static RegisterDefaults(ICvarService)` which `HudConfig` invokes by
  reflection (convention: any `HudPanel` subtype with a `public static void RegisterDefaults(ICvarService)` is
  called). No central list.

---

## 6. PER-PANEL FEATURE CHECKLIST (parity targets)

Tight, actionable list per panel. "✓ have" = present today; "+" = add for parity. All draw via the skin
background, Xolonium font, and live cvars.

**healtharmor** — + damage-flash (white pulse on health drop, `_progressbar_gfx_damage`), + low-health pulse
(red flash under `_progressbar_gfx_lowhealth` 40), + air/oxygen bar when underwater, + fuel bar (✓ partial),
+ combined mode (`_combined`), + flip/baralign/iconalign, + skin health/armor icons, + smooth bar
(`_progressbar_gfx_smooth`). Number tint via `NumColor`.

**armor** (part of healtharmor) — covered above.

**ammo** — + current-ammo highlight bg (`ammo_current_bg`), + per-ammo-type icons, + ammo bar
(`_progressbar`), + low-ammo color, + onlycurrent mode, + noncurrent alpha/scale, + iconalign.

**powerups** — + strength/shield/superweapons/jetpack/fuelregen icons + countdown timers, + progress bars per
powerup color, + buff icons, + iconalign/baralign, + flash when expiring.

**weapons** — + accuracy bar overlay (`_accuracy`, `weapon_accuracy`), + per-weapon ammo bar (`_ammo`,
`weapon_ammo`), + complainbubble (out-of-ammo/don't-have/unavailable colored bubble, `weapon_complainbubble`,
3 colors, time/fadetime), + timeout fade in/out (`_timeout`/`_timeout_effect` + speeds + fadebg/fgmin),
+ onlyowned, + noncurrent alpha/scale, + label modes 0..3 + label_scale, + current-weapon bg highlight
(`weapon_current_bg`) + selection animation (`_selection_speed`), + aspect.

**notify** — + kill-feed icons (`notify_*` per death type), + per-line fade (`_time`/`_fadetime`), + flip,
+ team-colored names, + CTF/objective event lines, + fontsize scale.

**timer** — + warmup/overtime/round states (✓ have), + skin frame, + count-up vs count-down, + suddendeath
color.

**radar** (newly wired) — + minimap render of the map + player blips, + rotation modes (`_rotation`),
+ zoommode/scale, + maximized mode (full-screen radar key), + team colors, + foreground alpha. (Phase-1 may
ship a placeholder frame + own-player dot if full minimap render is heavy.)

**score** — + rankings modes 0/1/2 (`_rankings`: off / show place / show score), + own score + frags/caps,
+ team scores in teamplay, + leader gap, + skin corner frame.

**racetimer** — + current lap time + delta to record (green/red), + best/last split, + checkpoint flash
(`race_new*` icons), + countdown.

**vote** — + callvote question text + yes/no counts + progress bar (`voteprogress_*`), + time remaining,
+ already-voted dim (`_alreadyvoted_alpha`), + your-vote highlight.

**modicons** — + CTF flag icons per team/state (`flag_*`), + dom point ownership, + CA/freezetag alive counts
(`player_<color>`), + keyhunt (`kh_*`), + assault (`as_*`), + overkill weapon icons, + layout cvars (✓ read).

**pressedkeys** — + 8-key WASD+jump+crouch+atck1/2 grid using `key_*`/`key_*_inv` pics, + aspect (`_aspect`
1.8), + attack-keys toggle (`_attack`), + enable 0/1/2 (off / only-spectating / always).

**chat** — + scrolling chat history using `con_chatsize`/`con_chattime`, + team/spec channel colors,
+ fade-out per line, + chat-sound (`con_chatsound`), + maximized chat mode. (Reads ENGINE `con_*` cvars.)

**engineinfo** (off by default) — + FPS/time line (`_fps_decimals`/`_fps_time`); low priority, our FpsPanel
already covers FPS.

**infomessages** — + warmup/spectating/ready-up/teamselect lines (✓ partial), + flip (`_flip` 1, right-align),
+ "press jump to spawn", + chase-cam/observer hints, + click-to-spawn countdown.

**physics** — + speedometer (✓ have), + topspeed memory (`_topspeed`/`_topspeed_time`), + jumpspeed,
+ acceleration bar (`accelbar`, `_acceleration_*`), + speed bar/progressbar (`_progressbar`/`_baralign`),
+ speed unit display (`_speed_unit_show` + `hud_speed_unit`), + vertical-speed include, + speed color, + flip.

**centerprint** — + queued messages with priority + fade in/out (`_fade_in`/`_fade_out`/`_time`), + align
(`_align` 0.5), + bold + title font scales (`_fontscale_bold`/`_title`), + countdown numbers, + flip,
+ color codes. Use `HudSkin.BoldFont` for titles.

**itemstime** — + per-item respawn timers (mega health, large armor, powerups) with icons + countdown,
+ progressbar (`_progressbar`/`_progressbar_reduced`/`_progressbar_maxtime`), + iconalign, + dynamicsize,
+ ratio columns, + spectating-only/always (enable 2).

**quickmenu** — + nested quick-chat menu (entries from a quickmenu file/default), + align (`_align`),
+ translatecommands, + server_is_default, + key navigation + click select, + no enable cvar (CONFIG no off).

**scoreboard** — + spectator list, + per-weapon accuracy rows (`_accuracy_doublerows`), + item-stats rows
(`_itemstats_doublerows`), + game-info footer (map/gametype/time), + team bg colors, + self highlight
(`_table_highlight_alpha_self`), + eliminated dim (`_table_highlight_alpha_eliminated`, ✓ read), + respawn
timer with decimals, + fade in/out (`_fadeinspeed`/`_fadeoutspeed`), + ping/pl columns.

**strafehud** — + the strafe-bar (neutral/accel/overturn colored zones), + angle indicator, + best-angle
marker (`_bestangle`), + wturn/switch indicators, + mode/style/range (`_mode`/`_style`/`_range`), + unit,
+ demo cvar. NEW panel (port extension faithful to stock strafehud).

**pickup** — + recent-pickup list with icons + names + fade-out (`_time`/`_fade_out`), + iconsize, + item
timer (`_showtimer` 0/1/2), bottom-left. NEW panel.

**checkpoints** — + race checkpoint splits list, + flip/align/fontscale, + delta coloring.

**mapvote** — + intermission map grid with mapshots + names + vote counts + your highlight (✓ partial),
+ abstain, + gametype, + countdown, + highlight border.

**minigamehelp** — + per-minigame help text panel (controls/rules) for the active minigame. NEW panel; shows
under PanelShow.Minigame.

**vehicle** (VehicleHud) — keep current; migrate to skin frame (`vehicle_frame`) + Xolonium; not in
panel-order.

### Port extras (MUST NOT REGRESS — minimal change)
- **FpsPanel** — add `PanelId="engineinfo-fps"` (own id, not the stock engineinfo) or keep self-managed;
  `ShowFlags = all`; keep `showfps`/`cl_showfps` self-gating + debug-default-on. Migrate `ThemeDB.FallbackFont`
  → `Font`.
- **PingPanel** — `ShowFlags = all`; keep `cl_showping` gating + `PingProvider`. Stays one row above FPS.
  Migrate to `Font`.
- **VignetteOverlay / WorldTint** — untouched (not HudPanels).
- **MinigameRenderer / MinigameMenu** — untouched as click-capturing Controls; the new `MinigameMenuOpen`
  state in the manager reads `MinigameMenu.Visible` for PANEL_SHOW gating; Pong key-drive stays.
- **FrameProfiler** — keep `hud.mgr` scope; optionally add per-panel `hud.<id>` scopes.

---

## 7. Acceptance / verification
- Build clean (`dotnet build XonoticGodot.csproj`).
- `--map <m> --screenshot` shows skinned frames (border_default 9-slice) on the bg-enabled panels, Xolonium
  text, and panels at the luma positions (health bottom-center, weapons right strip, etc.).
- `set hud_panel_healtharmor_pos "0 0"` (console) moves the panel live within `hud_panel_update_interval`.
- `set hud_skin default` reloads art (TextureCache cleared).
- Scoreboard up → maingame panels cross-fade out; minigame menu open → minigame panels show; intermission →
  mapvote shows. FPS/Ping/Vignette/WorldTint/Minigame all still function.

---

## 8. ORCHESTRATOR CORRECTIONS (AUTHORITATIVE — override any conflicting text above)

Verified against the real code before foundation build. These supersede §1–§7 where they differ.

1. **Cvar store = `MenuState.Cvars` (concrete `CvarService`), NOT `Api.Cvars`.** `MenuState` is in
   namespace `XonoticGodot.Game.Menu` (game/menu/framework/MenuState.cs); `CvarService` is in
   `XonoticGodot.Engine.Simulation` (src/.../EngineServices.cs). WorldTint.cs deliberately reads
   `MenuState.Cvars` so console `set` + the menu DialogHudPanel* dialogs (which also write `MenuState.Cvars`)
   take effect live — the HUD does the same. Every HUD file that reads/registers cvars does
   `using XonoticGodot.Game.Menu;` and uses `MenuState.Cvars`.
   - Members: `GetFloat(name)`, `GetString(name)`, `Set(name,val)`, `Register(name,default,CvarFlags)`,
     `bool IsModified(name)`, and **`event Action<string>? Changed`** (it DOES exist on the concrete type).
   - Unset-vs-explicit-0 guard (use everywhere): `string s = MenuState.Cvars.GetString(n); return
     string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(n);`
2. **Defaults registered via `HudConfig.RegisterDefaults(MenuState.Cvars)` added to `ClientSettings.ApplyAll()`**
   (game/menu/framework/ClientSettings.cs) alongside the existing Vignette/Tint/FrameProfiler registrations.
   Use `CvarFlags.Save` for menu/editor-written cvars.
3. **Font wiring** at the two sites that already set `TextureCache.VfsResolver`:
   - game/net/NetGame.cs:837 — after `TextureCache.VfsResolver = _assets.LoadTexture;` add
     `HudPanel.HudFont = _assets.GetFont("xolonium"); HudSkin.BoldFont = _assets.GetFont("xolonium-bold");`
   - game/GameDemo.cs:672 — same (`_assets` is in scope; `AssetLoader.GetFont(string)` returns `FontFile?`).
4. **Decoupling: NO existing panel file is edited by the foundation.** `HudPanel`'s `PanelId`, `DefaultLayout`,
   `ShowFlags`, `DefaultBg/Border/Enable/BgColor/BgAlpha` are all `virtual` and DEFAULT to lookups in a new
   `HudLayoutDefaults` table keyed by panel id. `PanelId` defaults to the class name minus a trailing
   `Panel`/`Hud`, lowercased (HealthArmorPanel→"healtharmor", VehicleHud→"vehicle"). Result: every existing
   panel jumps to the luma layout + skin frame + Xolonium + live cvars **with zero edits**, purely by being
   discovered. Panel agents later override these virtuals / add fidelity in their OWN files only.
5. **Backward-compat shims kept (build stays green):** `Configure(Rect2)` stays functional (NetGame.cs:1704
   calls `_scoreboard.Configure(...)`); `Size2`, `PanelRect`, `DrawBar`, `DrawText/Centered/Right`,
   `MeasureText`, `NumColor`, `ToColor`, `SecondsToString`, `IsDynamic` keep their names/signatures.
   `FontSize`(=11 now) and `Padding` remain compile-time consts (used as default args). `BgColor`/`FgColor`
   become live (driven by `Cfg`/`LiveFgAlpha`). The text helpers now use `Font` (Xolonium) internally.
6. **`DrawBackground()` now draws the skin 9-slice** (HudSkin.DrawBorderPicture) when `Cfg.Bg` is a
   `border_*` name; draws NOTHING when bg is `"0"`/inherit-of-`"0"`; flat translucent `DrawRect` fallback only
   when the border texture fails to resolve. So bg-"0" luma panels (notify/physics/centerprint/…) correctly
   have no frame.
7. **Manager visibility model is PRESERVED (regression-safe).** The foundation manager does layout/skin/font/
   cvar `LoadConfig` + `QueueRedraw` for every currently-visible panel and keeps the existing net-driven
   `Visible` + initial-visibility model (RaceTimer/Vote/etc. start hidden; net layer shows them). Fps/Ping are
   exempted via a type skip-set (they self-manage `Visible`+redraw in their own `_Process`) — they are NOT
   edited. A `ScoreboardFade` (0..1) hook multiplies non-scoreboard panels' alpha for the cross-fade without
   seizing `Visible`. Full PANEL_SHOW Visible-seizing precedence is deferred (panel-phase/follow-up) to avoid
   fighting the working net-driven show/hide.
8. **No `CvarService.Changed` dependency required** — panels POLL via a throttled `LoadConfig`
   (`hud_panel_update_interval`, default 2s; reload every 0.5s). Subscribing to `Changed` to set a dirty flag
   is an OPTIONAL latency optimization, not required.

---

## 9. NETGAME PLAY-PATH HUD ARCHITECTURE (discovered during foundation; agents MUST respect)

The real play path (`game/net/NetGame.cs`, `--host`/`--connect`) runs a HYBRID HUD, not a single Hud:
- **`_fullHud`** (a `Hud`, CanvasLayer Layer 4) — the full discovered panel set (the one this refactor skins/
  lays out). On a listen server (`--host`) its gameplay panels DO have a local server `Player`, so
  weapons/ammo/powerups/itemstime/notify/centerprint already render. On a pure `--connect` client there is no
  local Player → those panels self-blank.
- **`NetHud`** (`game/net/NetHud.cs`, Layer 5) — a 104-line lightweight Control that draws a bottom-left
  "♥ health  ▩ armor" label + a vector crosshair from the NETWORKED `ClientNet.Health/Armor` stats (works with
  no local Player). To avoid double-draw, NetGame sets `_fullHud.Crosshair.Visible=false`,
  `_fullHud.HealthArmor.Visible=false`, `_fullHud.Timer.Visible=false`.
- **standalone `_radar`** (a `RadarPanel`, Layer 5, manual pos 24,24 size 200) and **standalone `_scoreboard`**
  (a `ScoreboardPanel`, Layer 5) — fed from `ClientNet`. NOTE: `_fullHud` now ALSO auto-discovers a
  `RadarPanel`; the foundation keeps it hidden via `HudManager.StartHiddenIds` ("radar") so there is NO double
  radar. `_fullHud`'s own Scoreboard is left unfed (the standalone one owns the networked scoreboard).

Consequences for the fan-out:
- A panel agent improving `HealthArmorPanel`/`CrosshairPanel`/`TimerPanel` draw code will NOT see it on `--host`
  until the **NetGame integration task** unhides those `_fullHud` panels (and retires/【fallback】NetHud's
  duplicates + feeds Timer the net clock). That integration is a SEPARATE single-owner task on `NetGame.cs` —
  **no panel agent edits `NetGame.cs`/`GameDemo.cs`/`NetHud.cs`.**
- New panels (Score/Pickup/Chat/PressedKeys/StrafeHUD/QuickMenu/MinigameHelp) are auto-discovered into BOTH
  `_fullHud` and GameDemo's `_hud`. They MUST self-blank when they have no data (draw nothing) so they don't
  clutter until the integration task feeds them. Each new panel exposes public data setters/sources; the
  integration task wires the net/event data into them.
- GameDemo (`--map`) does NOT populate full player state (health/weapons), so its player-bound panels are
  expected to be mostly blank there; verify on `--host`.

### Base-API quick reference for panel agents (from the foundation, now in `HudPanel.cs`)
- Draw chrome: `DrawBackground()` (skin 9-slice; no-op when the panel's luma bg is "0"). Inner fill:
  `DrawBackground(Rect2)`.
- Text (Xolonium, with shadow): `DrawText(pos,text,color,size)`, `DrawTextCentered(pos,width,...)`,
  `DrawTextRight(rightX,topY,width,...)`, `MeasureText(text,size)`. Colored `^x` runs: use `HudText.Parse`.
- Skin art: `DrawSkinPic("weaponvortex", rect, modulate)` (skin→default fall-through) or
  `DrawSkinPicFirst(rect, modulate, "gfx/hud/.../a","gfx/hud/.../b")`; `TextureCache.GetFirst(...)` for raw.
- Geometry/look: `Size2` (panel px size), `Cfg.Padding`, `Cfg.FontSize`, `LiveFgAlpha`, `LiveBgAlpha`,
  `FgColor` (live), bars via `DrawBar(area,frac,fill)`.
- Cvars (READ/REGISTER from `MenuState.Cvars`): instance `CvarStr("suffix")`, `CvarF("suffix",fallback)`,
  `CvarBool("suffix")` (→ `hud_panel_<id>_<suffix>`); static `GlobalF("hud_progressbar_health_color"...)`.
  Register behavior-cvar defaults via a `public static void RegisterDefaults(CvarService c)` on the panel
  (auto-invoked by `HudConfig`) — use `c.Register("hud_panel_<id>_<x>", "<default>", CvarFlags.Save)`.
- Identity/visibility: override `PanelId`/`DefaultLayout`/`ShowFlags`/`DefaultBg` ONLY if the luma table default
  is wrong for you (it usually isn't). `IsDynamic=false` for static panels.
