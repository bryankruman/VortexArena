# Recon T28 — Menu skin/theme loader (skinvalues.txt) + Localization (i18n / gettext .po + language list)

READ-ONLY recon. Two independent subsystems. The port already has substantial *visual* theme infra
(`MenuSkin.cs`) and *partial* skinvalues parsing, but the faithful **full SKIN\* table** and the **entire
localization layer** are missing. Below is the Base spec, the port gap, exact seams, a plan, and conflicts.

---

## PART A — SKIN / THEME (skinvalues.txt → SKIN\* table)

### A.1 Base spec — the SKIN\* schema (`menu/skin.qh` + `menu/skin-customizables.inc`)

`skin.qh` is an X-macro. It is included twice:

1. **Declarations** (skin.qh:3-14): each `SKINVECTOR(name,def)` → `vector SKINname = def;`,
   `SKINFLOAT` → `float SKINname = def;`, `SKINSTRING` → `string SKINname = def;`. So every key becomes a
   global named **`SKIN<KEY>`** seeded with its shipped default. (Note: vectors expose component globals too,
   e.g. `SKINSIZE_CURSOR_x` — used in menu.qc tooltip math.)
2. **The setter** (skin.qh:16-28):
   ```c
   void Skin_ApplySetting(string key, string _value) { switch(key) {
     case "FONTSIZE_NORMAL": SKINFONTSIZE_NORMAL = stof(_value); break;   // SKINFLOAT
     case "MARGIN_TOOLTIP":  SKINMARGIN_TOOLTIP  = stov(_value); break;   // SKINVECTOR
     case "GFX_TOOLTIP":     SKINGFX_TOOLTIP     = strzone(_value); break;// SKINSTRING
     ...
     case "": break; case "//": break;
     default: LOG_TRACE("Invalid key in skin file: ", key);
   } }
   ```
   Important: the **case label is the bare key WITHOUT the `SKIN` prefix** (`#name`), but the global it
   writes is `SKIN<name>`. Vectors use `stov` (parses `'r g b'` or `r g b`), floats `stof`, strings stored raw.

`skin-customizables.inc` (skin-customizables.inc:36-282) is **the authoritative schema** — ~190 keys with
shipped defaults. This is the Generic/default skin baked into the binary; `skinvalues.txt` only OVERRIDES
keys it lists. Full key list with defaults (reproduce ALL of these as the table's fallback values):

- **Font sizes:** FONTSIZE_NORMAL=12, HEIGHT_NORMAL=1.5, FONTSIZE_TITLE=16, HEIGHT_TITLE=1.5, HEIGHT_ZOOMEDTITLE=-1
- **Tooltips:** GFX_TOOLTIP="tooltip", MARGIN_TOOLTIP='5 5 0', BORDER_TOOLTIP='1 1 0', AVOID_TOOLTIP='8 8 0',
  WIDTH_TOOLTIP=0.3, FONTSIZE_TOOLTIP=12, ALPHA_TOOLTIP=0.7, COLOR_TOOLTIP='1 1 1'
- **Dialog bg colors (per dialog):** COLOR_DIALOG_FIRSTRUN..COLOR_DIALOG_HUDCONFIRM (24 of them; full list in
  skin-customizables.inc:55-77). Defaults mostly '0.7 0.7 1' (blue), some '1 0.7 0.7', '1 1 0.7', '1 0 0', '1 1 1'.
- **Nexposee window positions:** POSITION_DIALOG_MULTIPLAYER='0.9 0.5 0', _SINGLEPLAYER='0.1 0.1 0',
  _MEDIA='0.9 0.9 0', _SETTINGS='0.1 0.9 0', _CREDITS='0.3 1.2 0', _QUIT='0.9 1.2 0'
- **Mouse:** GFX_CURSOR="cursor", SIZE_CURSOR='32 32 0', OFFSET_CURSOR='0 0 0', ALPHA_CURSOR_INTRO=0
- **General:** COLOR_BACKGROUND='0 0 0', GFX_BACKGROUND="background", GFX_BACKGROUND_INGAME="background_ingame",
  ALIGN_BACKGROUND="5", ALIGN_BACKGROUND_INGAME="5", ALPHA_BACKGROUND_INGAME=0.7, ALPHA_DISABLED=0.2,
  ALPHA_BEHIND=0.5, ALPHA_TEXT=0.7, COLOR_TEXT='1 1 1', ALPHA_HEADER=0.5, COLOR_HEADER='1 1 1'
- **Button:** GFX_BUTTON="button", GFX_BUTTON_GRAY="buttongray", GFX_BUTTON_BIG="bigbutton",
  GFX_BUTTON_BIG_GRAY="bigbuttongray", COLOR_BUTTON_N/C/F/D='1 1 1', MARGIN_BUTTON=0.5
- **Campaign / checkbox / colorpicker / credits / cvarlist / dialog / inputbox / clearbutton / gametypelist /
  keygrabber / listbox / maplist / nexposee / colorbutton / modeltitle / charmap / crosshairpicker /
  radiobutton / scrollbar / serverlist / serverinfo / skinlist / demolist / screenshotlist / slider** —
  see skin-customizables.inc:120-281 for the full set (every GFX\_/COLOR\_/ALPHA\_/MARGIN\_/WIDTH\_/BOOL\_/
  ROWS\_/FADEALPHA\_ key + default). Notable for the port's existing usage:
  - GFX_DIALOGBORDER="border", GFX_CLOSEBUTTON="closebutton", MARGIN_TOP/BOTTOM/LEFT/RIGHT=8,
    MARGIN_COLUMNS/ROWS=4, HEIGHT_DIALOGBORDER=1
  - GFX_INPUTBOX="inputbox", GFX_CHECKBOX="checkbox", GFX_RADIOBUTTON="radiobutton",
    GFX_SLIDER="slider", GFX_SCROLLBAR="scrollbar", GFX_CURSOR="cursor"
  - COLOR_LISTBOX_SELECTED='0 0 1', ALPHA_LISTBOX_SELECTED=0.5, COLOR_LISTBOX_FOCUSED='0 0 1',
    ALPHA_LISTBOX_FOCUSED=0.7, FADEALPHA_LISTBOX_FOCUSED=0.3, COLOR_LISTBOX_BACKGROUND='0 0 0',
    ALPHA_LISTBOX_BACKGROUND=0.5
  - WIDTH_SCROLLBAR=16, WIDTH_SLIDERTEXT=0.333..., ALPHAS_MAINMENU='0.6 0.8 0.9'
  - COLOR_SKINLIST_TITLE='1 1 1', COLOR_SKINLIST_AUTHOR='0.4 0.4 0.7'

  > NOTE: the **shipped `gfx/menu/luma/skinvalues.txt` (the live skin) overrides MANY of these** (e.g.
  > COLOR_TEXT, COLOR_LISTBOX_SELECTED → orange). The defaults above are the Generic fallback; the loader
  > MUST overlay the file on top. The port currently bakes luma values as C# fallbacks (see A.3) — that is
  > the gap: it does not load the Generic baseline, and reads only ~10 keys.

### A.2 Base spec — the LOADER (`menu/menu.qc` `m_init_delayed`, menu.qc:165-233)

Order of operations (faithful list):

1. Build skin path with **fallback chain**:
   - if `cvar_string("menu_skin") != ""` → try `gfx/menu/<menu_skin>/skinvalues.txt`
   - else/failing, if `cvar_defstring("menu_skin") != ""` → reset cvar to its default, try
     `gfx/menu/<defstring>/skinvalues.txt`
   - else/failing → hardcoded `gfx/menu/wickedx` (literal default skin name in the binary), try its skinvalues.txt
   - if still fails → `error("cannot load any menu skin")`
   - `draw_currentSkin` is set to the chosen folder path (used to resolve all skin gfx).
2. Parse the file line by line (`fgets`):
   - skip lines starting `"title "` (6 chars) and `"author "` (7 chars) — those belong to skinlist.qc.
   - `n = tokenize_console(s)`; if `n < 2` skip.
   - **`Skin_ApplySetting(argv(0), substring(s, argv_start_index(1), argv_end_index(-1) - argv_start_index(1)))`**
     — KEY = first token; VALUE = the raw substring from the start of token 1 to the **end of the LAST token**
     (so multi-word vectors `'1 0.7 0.7'` and space-containing values survive verbatim). Whitespace between
     key and value is the delimiter only.
3. Precache: `search_begin("<skin>/*.tga", true, true)` → `precache_pic` every match. (Port loads TGAs lazily,
   so this maps to "TGA decode via VFS"; no eager precache needed.)
4. `draw_setMousePointer(SKINGFX_CURSOR, SKINSIZE_CURSOR, SKINOFFSET_CURSOR)`.

skinvalues.txt on-disk format (confirmed from the Perl generator at skin-customizables.inc:1-33): one
`KEY value` per line, `%-31s %s` padded, vectors as bare `r g b` (NOT quoted in the file). Comment lines
begin `// `.

### A.3 Port state — what EXISTS (`game/menu/framework/MenuSkin.cs`, 717 lines)

- A `Theme` builder that reproduces the *visual* luma look via Godot styleboxes from the TGAs — comprehensive
  and good. This is NOT the gap.
- A **partial** skinvalues.txt reader: `EnsureValues()` (MenuSkin.cs:642-682) parses the file into a
  `Dictionary<string,string> _values` — but only on demand via `Rgb()`/`Rgba()`/`Num()` (MenuSkin.cs:684-715),
  and only ~10 keys are ever read (COLOR_TEXT, COLOR_HEADER, COLOR_CREDITS_TITLE, COLOR_LISTBOX_SELECTED,
  COLOR_LISTBOX_BACKGROUND, ALPHA_TEXT/HEADER/DISABLED/LISTBOX_*). Everything else is a hardcoded Godot value.
  - Its parser (MenuSkin.cs:658-676) is *close* but **diverges from Base** in two ways worth noting (parity):
    (a) it strips leading/trailing `'`/`"` off the WHOLE value then re-trims — fine for `'1 1 1'` but it would
    mangle a value that legitimately needed them; Base never strips quotes (vectors are bare in the file).
    (b) it does not implement the Generic-default baseline (so a key ABSENT from luma's file but present in
    skin-customizables.inc has the C#-hardcoded fallback, not the schema default).
  - `SkinName` getter (MenuSkin.cs:526-533) honors `menu_skin` cvar, defaults to "luma" (Base default skin is
    luma per xonotic-client.cfg:605 `seta menu_skin "luma"`), and `LoadSkinTexture` (MenuSkin.cs:536-548) has a
    luma fallback — a reasonable stand-in for the menu.qc fallback chain (no `wickedx` since the content repo
    only ships luma).
- `DialogSettingsUser.cs` (game/menu/dialogs/DialogSettingsUser.cs:20-97): skin picker = a TextSlider over a
  HARDCODED 3-name list (luma/luminos/xolonium), NOT a data-driven scan. "Set skin" issues
  `menu_restart; menu_cmd skinselect` (matches QC). `DialogFirstRun.cs` similar for language.

### A.4 Port gap — SKIN

The visual theme is already faithful. The realistic T28 skin scope (per the task brief "focus on (a) loading
skinvalues.txt into the SKIN\* table faithfully") is the **canonical SKIN\* table + faithful loader**, so the
theme is *data-driven from the schema* rather than from a hardcoded subset:

1. **No full SKIN\* table.** Reproduce `skin-customizables.inc` as a C# schema (every key + typed default).
2. **No `Skin_ApplySetting` switch.** Apply parsed file values onto that table (stof/stov/string), with the
   exact case-label semantics (bare key, `// ` + `""` ignored, unknown → log).
3. **Loader fallback chain** (menu_skin cvar → defstring → wickedx) only partly present (luma hardcoded fallback).
4. **Value-substring semantics**: Base preserves the value verbatim from token-1-start to last-token-end; the
   port splits on first space then trims-quotes. Make the table loader match Base (split first whitespace, keep
   the remainder; do NOT strip quotes — but tolerate them since the existing reader does for back-compat).
5. (Optional, lower priority) data-driven skin LIST (skinlist.qc): scan `gfx/menu/*/skinvalues.txt` via
   `vfs.Find("gfx/menu/")` filtered to `skinvalues.txt`, extract the `*` segment + `title`/`author` lines.
   Currently a 3-item hardcoded TextSlider.

The cleanest faithful design (matches the port's `MenuPickerMath` headless-logic pattern): a Godot-free
`SkinValues` static (or instance) in `src/XonoticGodot.Common/...` or `game/menu/framework/` that holds the typed
table + `ApplySetting(key,value)` + `Load(text)` (string in, no Godot/VFS), unit-tested headlessly; then
`MenuSkin` consumes it (replace the ad-hoc `_values`/`Rgb`/`Num` with table lookups). **Minimize churn**:
`MenuSkin` is shared with many widgets — keep its public accessors (Text/Header/Accent/...) but back them by
the table.

---

## PART B — LOCALIZATION (i18n / gettext)

### B.1 Base spec — the QC side (`lib/i18n.qh`)

- `string prvm_language;` global (i18n.qh:8). Set in `m_init` (menu.qc:65-74): read `prvm_language` cvar; if
  empty → "en", set cvar, `menu_restart`. Then mirror into `_menu_prvm_language`.
- `language_filename(s)` (i18n.qh:13-27): DEPRECATED helper — if lang is ""/"en"/"dump" return s unchanged,
  else try `s.<lang>` file; used for per-language asset files (not strings).
- `CTX(s)` (i18n.qh:45-68): **msgctxt emulation**. Strips a `PREFIX^` from the front of `s` (the part before
  the first `^`, if >1 char and contains no space) and returns the remainder. So `_("GAMETYPE^Deathmatch")`
  is translated as the full string `"GAMETYPE^Deathmatch"` (the .po msgid INCLUDES the prefix), but DISPLAYED
  as `"Deathmatch"` after CTX strips it. `ZCTX(s)` = `strzone(CTX(s))`. Cached in a HashMap.
- The actual translation operator is `_("...")` — a **GMQCC compile-time feature** (`-ftranslatable-strings`):
  every `_()` literal is collected into a `.pot`, and at PROGS LOAD the engine rewrites the string globals via
  the .po (see B.2). i18n.qh is just the runtime helpers around it.

### B.2 Base spec — the gettext ENGINE (`darkplaces/prvm_edict.c`)

Type `po_t` = `po_string_t *hashtable[PO_HASHSIZE]` (prvm_edict.c:1701-1705); each `po_string_t` = {key,value,
nextonhashchain}. CRC_Block hash (1906/1921).

- **`PRVM_PO_ParseString(out,in,outsize)`** (prvm_edict.c:1747-1805): decode C-escapes in a quoted msgid/msgstr
  body — `\a \b \t \r \n \\ \"` and **octal `\NNN`** (1-3 octal digits), else literal char. (Inverse:
  `PRVM_PO_UnparseString` 1706, only needed for `-dump`.)
- **`PRVM_PO_Load(filename, filename2, pool)`** (prvm_edict.c:1806-1918):
  - Loops i=0,1: reads **filename2 first (i=0), then filename (i=1)** "so that progs.dat.de.po wins over
    common.de.po, and within file, last item wins" (comment at 1822-1824). i.e. caller passes
    filename=`<progs>.<lang>.po`, filename2=`common.<lang>.po`; common loaded first, progs second & overrides.
  - Per file, scan lines:
    - `#...` comment → skip to newline (handles `#:`, `#,`, plain `#`).
    - blank `\r`/`\n` → skip.
    - `msgid "` (7-char prefix incl. quote) → mode=0, p+=6.
    - `msgstr "` (8) → mode=1, p+=7.
    - else → skip line.
    - Then consume **consecutive `"..."` lines** (continuation), concatenating decoded bodies (handles the
      multi-line wrapped strings seen in common.de.po header). Each body parsed via PRVM_PO_ParseString.
    - mode 0 → set `thisstr.key` to the decoded text.
    - mode 1 → **only if decoded length > 0 AND thisstr.key set** (skips empty translations = untranslated),
      store {key, value} into hashtable at CRC(key)%PO_HASHSIZE, prepended (so duplicate later wins on lookup
      because it's earlier in the chain... NB: prepend + linear scan ⇒ FIRST stored that matches wins; combined
      with load order this yields the documented "progs wins, within file last wins" — replicate the order, not
      the data structure).
  - Returns `po` (NULL if neither file present).
- **`PRVM_PO_Lookup(po, str)`** (prvm_edict.c:1919-1930): CRC hash → linear chain `strcmp` → value or NULL.
- **Application** (prvm_edict.c:2631-2657): at progs load (non-dump branch), `PRVM_PO_Load("%s.%s.po"
  realfilename prvm_language, "common.%s.po" prvm_language, pool)`; then for every string global whose name
  is NOT `notranslate_*` (deftrans mode), look it up and replace with the translation if found. **Key
  consequence for the port:** translation is keyed on the **English source string** (the msgid IS the literal),
  not an id. So the C# equivalent is `Tr("English literal")` → PO lookup → translated-or-self.
  - `en`/empty/`dump` ⇒ effectively no translation (en has no `common.en.po` with real entries; lookups
    return self).

### B.3 Base spec — the language picker (`menu/xonotic/languagelist.qc` + `.qh`)

- `getLanguages` (languagelist.qc:167-193): `fopen("languages.txt")`, per line `tokenize_console`, need ≥3
  tokens; store id(argv0), name(argv1), localized-name(argv2), percentage(argv3, only if present & != "100%").
  Format of `languages.txt` (confirmed, 34 langs): `id  "English name"  "Localized name"  NN%`. e.g.
  `de "German" "Deutsch" 82%`, `en "English" "English" 100%`.
- `loadCvars` (languagelist.qc:108-137): default-select the `en` row, then select the row whose id ==
  `_menu_prvm_language` cvar; save back (unknown → "en"). `saveCvars` writes `_menu_prvm_language` = selected id.
- `setLanguage` (languagelist.qc:195-204): if `prvm_language != _menu_prvm_language` cvar:
  - not connected → `localcmd("prvm_language \"$_menu_prvm_language\"; menu_restart; menu_cmd languageselect")`.
  - connected → open `languageWarningDialog` (warns: menu-only change live, full change next game).
- Draw (languagelist.qc:28-80): per-row name + percentage column; rows with <90% faded (90→1.0, 50→0.65,
  else 0.3); 100% shows a "ready" icon instead of a number. (Visual detail; the picker's *logic* is the
  load/save/select above.)
- `.qh` (languagelist.qh): `.name = "languageselector"` (the directmenu name; matched by
  MenuCommand `languageselect → languageselector`).

### B.4 Port state — LOCALIZATION

- **There is NO translation layer at all.** Grep for `_(`/`Tr(`/`gettext`/`Localize`/`i18n` in `game/menu`
  finds only DOC COMMENTS quoting the QC `_()` calls — every UI string is a **raw English C# literal** passed
  to `Ui.Header`/`Ui.Label`/`MakeTitle`/`MakeLabel`/`Widgets.CheckBox(...,"label","tooltip")`/`CommandButton`.
- `MenuColorCodes.cs` handles `^`-codes → BBCode (unrelated to gettext; keep).
- Language picker = hardcoded TextSlider in `DialogSettingsUser.cs:33-43,68-71` and `DialogFirstRun.cs:86-89`
  (8 representative locales), bound to `_menu_prvm_language`. NOT data-driven from languages.txt.
- `DialogLanguageWarning.cs` already exists (faithful warning dialog), command strings verbatim. Good.
- Command host `MenuCommand.cs`:
  - `languageselect`/`skinselect` ARE routed (to OpenDialogOverlay → languageselector/skinselector,
    MenuCommand.cs:193-194). But those dialog NAMES are **not in `MenuDialogRegistry`** (only nexposee/servers/
    profile/settings/inputsettings/videosettings/guide/quitdialog) — so the overlay open is currently inert.
  - `prvm_language` and `menu_restart` and `saveconfig` FALL THROUGH to the inert default-log
    (MenuCommand.cs:220-222). So "Set language"/"Set skin"/"Save settings" change `_menu_prvm_language` (the
    set inside the command) but the `prvm_language`/`menu_restart` parts do nothing.
- cvar defaults (confirmed xonotic-client.cfg): `prvm_language en` (line 91), `set _menu_prvm_language ""`
  (92), `seta menu_skin "luma"` (605). `prvm_language` is on the ConfigInterpreter NonCvarCommands denylist
  (ConfigInterpreter.cs:70) — so it's treated as a command, not a cvar set, today.
- `CvarService` (EngineServices.cs:268+) API: `GetString/GetFloat/Set/Register/GetDefault/IsModified/
  ResetToDefault/MarkArchived/IsArchived/Names/ArchivedNames/Changed`. **No `Defstring` alias** — use
  `GetDefault(name)` for the menu.qc `cvar_defstring` fallback. (`prvm_language` is registered via the cfg as a
  command, so it may need an explicit `Register("prvm_language","en")` if the loader reads it as a cvar.)

### B.5 Port gap — LOCALIZATION (the real T28 substance)

1. **PO parser + store** (Godot-free, headless-testable like `MenuPickerMath`): load `common.<lang>.po` then
   `menu.<lang>.po`/`progs.dat.<lang>.po` (overlay order per B.2), parse msgid/msgstr with continuation lines +
   C-escapes + octal, skip `#` comments and empty translations, key on the English source string. `Lookup(s)`
   → translated-or-null. Use a `Dictionary<string,string>` (last-write-wins to mirror the documented semantics
   — load common first, progs second so progs overwrites). Live language = `prvm_language` (en/empty ⇒ identity).
2. **`Tr(string)` + `CtxTr(string)` helper** = the `_()` / `CTX(_())` stand-in: `Tr` = PO lookup or self;
   `CtxTr`/`Ctx` strips a leading `PREFIX^` (faithful to i18n.qh CTX) AFTER translation. Route the menu's text
   helpers through it (the single insertion seam — see C below).
3. **Data-driven language list** from `languages.txt` (vfs.ReadText): parse id/name/localized/percentage.
   Replace the hardcoded `Languages[]` in DialogSettingsUser + DialogFirstRun (or back them by this loader).
4. **Wire `prvm_language` + `menu_restart`** in MenuCommand so "Set language" actually swaps the active PO and
   rebuilds the menu (or at minimum reloads strings). `menu_restart` ⇒ rebuild the current MenuRoot screen
   stack (Shell has the MenuRoot). `prvm_language X` ⇒ set the active language on the PO store + reload.
5. Register `languageselector`/`skinselector` in `MenuDialogRegistry` so the `*select` overlay verbs resolve
   (today they route to a name the registry doesn't know). Map them to DialogSettingsUser (or dedicated
   list dialogs). Lower priority — the in-tab pickers already work via the cvar.

---

## C — INSERTION SEAMS (functions + line ranges)

| Seam | File:lines | What to do |
|---|---|---|
| SKIN table consumption | `game/menu/framework/MenuSkin.cs:642-715` (`EnsureValues`/`Rgb`/`Rgba`/`Num`/`TryVec`) | Replace ad-hoc `_values` dict + on-demand readers with a full SKIN\* table (schema defaults + file overlay). Keep public accessors (`Text`,`Header`,`Accent`,`Selection`,`ListBackground`,`DisabledAlpha` MenuSkin.cs:54-66) backed by the table. |
| SKIN loader fallback chain | `MenuSkin.cs:526-548` (`SkinName`/`LoadSkinTexture`) + new `Load()` | Implement menu.qc fallback (menu_skin cvar → `GetDefault("menu_skin")` → "luma"/"wickedx"); parse with Base value-substring semantics. |
| **`Tr()` text seam (i18n)** | `game/menu/framework/Ui.cs` — `Title`(19), `Header`(29), `Label`(39), `Row`(47), `Button`(63); AND `game/menu/MenuScreen.cs` — `MakeTitle`(44), `MakeHeader`(59), `MakeLabel`(69), `MakeButton`(77), `MakeRow`(93) | Route the `text`/`labelText` argument through `Tr(...)` so every dialog string is translated with zero per-dialog edits (mirrors how QC got it free via `_()` at compile time). This is THE central i18n seam. |
| Widget label seam | `game/menu/framework/CvarControls.cs` — `CheckBox`(22), `FlagCheckBox`(26), `TextSlider`(34), `CommandButton`(46) | Route `label`/`tooltip` through `Tr(...)`. (Same idea; covers the bound widgets.) |
| Language picker data | `game/menu/dialogs/DialogSettingsUser.cs:33-43,68-71`; `DialogFirstRun.cs` (Languages[] + :86-89) | Back the locale list with a `LanguagesTxt.Load(vfs)` parse (id/name/localized) instead of the hardcoded array. |
| `prvm_language`/`menu_restart`/`saveconfig` | `game/menu/framework/MenuCommand.cs:220-222` (default case) | Add cases: `prvm_language <id>` → set active language + reload PO + restyle; `menu_restart` → rebuild MenuRoot; `saveconfig` → `MenuState.SaveUserConfig()`. |
| Dialog name registry | `game/menu/framework/MenuDialogRegistry.cs:26-36` | Add `["skinselector"]`/`["languageselector"]` factories so the `*select` overlay verbs resolve. |
| Boot hook (load PO at startup) | `game/menu/framework/MenuState.cs` `Boot` (after config load, ~line 132) | After cvars load, read `prvm_language` and load the matching PO into the new store so the menu builds translated. |

---

## D — STEP PLAN (faithful, mirrors Base)

**Localization (primary):**
1. `src/XonoticGodot.Common/.../PoCatalog.cs` (Godot-free): `Parse(string poText)` → entries; `Load(common, progs)`
   overlay order; `Lookup(src)`. Implement C-escape + octal + continuation-line + `#`/empty-skip exactly per
   PRVM_PO_ParseString/PRVM_PO_Load. Unit-test against hand-written fixtures + a slice of `common.de.po`.
2. `Localization` facade (game/menu/framework/): holds active `PoCatalog` + `CurrentLanguage`; `Tr(s)` (lookup
   or self; en/""/dump ⇒ self), `Ctx(s)`/`CtxTr(s)` (strip `PREFIX^` per i18n.qh CTX). `SetLanguage(id, vfs)`
   loads `common.<id>.po` + `menu.<id>.po`.
3. `LanguagesTxt.Load(vfs)` (Godot-free parse of languages.txt → list of {Id,Name,Localized,Percentage}).
   Unit-test the tokenizer (quoted names, percentage optional).
4. Route `Ui.*` + `MenuScreen.Make*` + `CvarControls` label/tooltip args through `Localization.Tr` (the seam).
5. Back DialogSettingsUser/DialogFirstRun language pickers with LanguagesTxt; keep the same `_menu_prvm_language`
   binding + the verbatim "Set language" command.
6. MenuCommand: implement `prvm_language`/`menu_restart`/`saveconfig`; register skinselector/languageselector;
   load PO at MenuState.Boot from `prvm_language`.

**Skin (secondary, mostly already done visually):**
7. `SkinValues.cs` (Godot-free): the full schema from skin-customizables.inc (every key + typed default) +
   `ApplySetting(key,value)` (bare-key switch, stof/stov/string, `//`+`""` ignore, unknown log) +
   `Load(text)` (line parse w/ Base value-substring semantics, skip title/author). Unit-test ApplySetting +
   a luma skinvalues.txt slice (esp. vectors + override-vs-default).
8. Point `MenuSkin` at `SkinValues` (replace `_values`/`Rgb`/`Num`); add the menu.qc fallback chain incl.
   `GetDefault("menu_skin")`. Keep all existing public accessors.
9. (Optional) data-driven skin LIST via `vfs.Find("gfx/menu/")` → skinvalues.txt names + title/author.

---

## E — CVARS + SHIPPED DEFAULTS

| cvar | default | source | notes |
|---|---|---|---|
| `menu_skin` | `luma` | xonotic-client.cfg:605 (`seta`) | folder under gfx/menu/. Loader fallback: cvar→GetDefault→"wickedx". |
| `prvm_language` | `en` | xonotic-client.cfg:91 | the ACTIVE language (drives PO load). On ConfigInterpreter denylist (treated as cmd). |
| `_menu_prvm_language` | `""` | xonotic-client.cfg:92 (`set`) | the menu's pending selection; "Set language" copies it into prvm_language + restart. |
| `menu_font_cfg` | (a cfg path) | client cfg | exec'd after a lang change in menu.qc (font may depend on lang). Lower priority. |
| `cl_gentle`,`cl_gentle_gibs`,`cl_gentle_messages`,`cl_gentle_damage` | 0 | client cfg | the gentle-mode checkboxes already in DialogSettingsUser (not i18n, leave as-is). |

SKIN\* defaults: the full Generic baseline is `skin-customizables.inc:36-282` (enumerated in A.1). The live
overlay is `gfx/menu/luma/skinvalues.txt` in the content repo.

---

## F — TEST PLAN

Headless xUnit (`tests/XonoticGodot.Tests`, the `MenuPickerLogicTests` Godot-free pattern):
- **PoCatalogTests:** parse fixtures — basic msgid/msgstr; multi-line continuation (header block);
  C-escapes (`\n \t \" \\`) + octal `\NNN`; `#:`/`#,`/`#` comment skip; empty-msgstr skipped (untranslated →
  Lookup returns null/self); overlay order (common then progs ⇒ progs wins); unknown key ⇒ self; en/""/dump ⇒
  identity. Use a small slice of `Base/.../common.de.po` to prove real-file parse (e.g. msgid
  `"Checkpoint times:"` → `"Kontrollpunktzeiten:"`).
- **CtxTests:** `Ctx("GAMETYPE^Deathmatch")=="Deathmatch"`; no `^` ⇒ unchanged; leading `^` (color code) ⇒
  unchanged (space-before-caret guard per i18n.qh:58-60).
- **LanguagesTxtTests:** parse `languages.txt` → 34 entries; quoted localized names with non-ASCII; percentage
  optional/"100%" handling.
- **SkinValuesTests:** every schema key has a default of the right type; `ApplySetting("COLOR_TEXT","1 1 1")`
  → vector; `ApplySetting("FONTSIZE_NORMAL","12")` → float; bare-key (no SKIN prefix) cases; `//`/`""`/unknown
  ignored; `Load()` value-substring keeps `'1 0.7 0.7'` intact; override beats schema default; absent key keeps
  default.
- **DialogSettingsUser/DialogFirstRun** language list is sourced from LanguagesTxt (assert the list contents
  match the file, not the old hardcoded 8).
- Build gate: `dotnet build XonoticGodot.csproj` + `dotnet test tests/XonoticGodot.Tests` (Windows dotnet per memory).
- Manual (optional): `set prvm_language de; menu_restart` → menu strings switch; `set menu_skin <x>` → restyle.

---

## G — RISKS / PARITY TRAPS

1. **Translation is keyed on the English SOURCE string, not an id.** The msgid in the .po is the literal. So
   `Tr("Menu Skin")` must pass the EXACT same literal the .pot was generated from (incl. `^` color codes and
   `CTX^` prefixes). If the port paraphrased any label vs the QC `_()` text, that string won't translate. The
   port's labels were ported from the QC `_()` (doc comments confirm), so most should match — but verify a few.
2. **CTX prefix lives in the msgid.** `_("GAMETYPE^Foo")` translates the WHOLE `"GAMETYPE^Foo"`; CTX strips the
   prefix for DISPLAY only. So the PO key includes `GAMETYPE^`. Don't strip before lookup; strip after.
3. **PO load/override order is load-order-sensitive** (common first, progs second, last wins). A naive
   dict.Add would throw on dup; use indexer (overwrite). Mirror the order, not the prepend-chain data structure.
4. **Empty msgstr = untranslated** → must be skipped (return source), else partially-translated languages blank
   out UI. (PRVM_PO_Load:1902 `decodedpos > 0`.)
5. **Skin value-substring semantics:** Base keeps the value verbatim from token1 to last token (spaces in
   vectors survive). The existing MenuSkin parser splits on first space then *strips quotes* — fine for luma
   (bare vectors) but technically divergent. New loader: split first whitespace, keep remainder, do NOT strip
   quotes (tolerate them for back-compat with the current reader only if needed).
6. **`Skin_ApplySetting` case label is the BARE key** (no SKIN prefix); the schema/table must map bare-key →
   typed slot. Easy to accidentally key on `SKIN...`.
7. **`menu_restart` must rebuild the menu** for a language/skin change to be visible — a no-op `menu_restart`
   leaves stale strings. MenuRoot rebuild is the faithful effect. Don't just swap the catalog without rebuild.
8. **`prvm_language` is on the ConfigInterpreter NonCvarCommands denylist** (ConfigInterpreter.cs:70) and
   `_menu_prvm_language` defaults to `""` — the language picker must seed selection from `prvm_language`
   (default "en") when `_menu_prvm_language` is empty (languagelist.qc loadCvars selects en first).
9. **No `wickedx` skin in the content repo** (only luma ships). The fallback chain's final `wickedx` step will
   miss; luma is the de-facto floor. Keep luma as the ultimate fallback (port already does).
10. **Font dependency:** menu.qc re-execs `menu_font_cfg` on a language change (CJK needs a different font).
    The port's single Xolonium font won't render CJK glyphs; out of realistic scope but note it (CJK labels
    will tofu). Don't claim full CJK support.
11. **`MenuSkin` is shared/hot** across many widgets + the global Theme; changing its internals risks the whole
    front-end. Keep public API stable; back it by the table. (This is a refactor-in-place, not a rewrite.)

---

## H — CROSS-TASK CONFLICTS (hotFiles)

T28 owns `game/menu/*` and the new localization infra. The only listed shared file is **MenuState.cs** (Boot),
which is NOT in any other A2 task's owned set (T36/T43/T37/T51/T56/T30) — those are gameplay/server/engine
files. **No overlap with another A2 task's owned files.** New files (PoCatalog, Localization, LanguagesTxt,
SkinValues) are greenfield. Edits to Ui.cs/MenuScreen.cs/CvarControls.cs/MenuCommand.cs/MenuDialogRegistry.cs/
MenuSkin.cs/DialogSettingsUser.cs/DialogFirstRun.cs are all menu-only (T28-owned). No `MutatorHooks.cs`/
`DamageSystem.cs`/`GameWorld.cs`/`ServerNet.cs`/`NetGame.cs`/`MapObjectsRegistry.cs`/`Registries.cs`/
`GameInit.cs`/`Services.cs`/`Commands.cs` touch required.

Registration mechanism check (A1 lesson): the menu does NOT use `[Mutator]`/`[GameType]` reflection. Dialogs
register via the manual `MenuDialogRegistry` table; skin/lang lists are data-driven file scans. So T28's
registry edit (skinselector/languageselector) is a normal manual-table edit on a T28-owned file — no shared
registry contention.
