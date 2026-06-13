// Port of qcsrc/common/mapinfo.qc (the .mapinfo parser + the create-game map-info dialog's
//   loadMapInfo, qcsrc/menu/xonotic/dialog_multiplayer_create_mapinfo.qc:9-39).
//
// MapInfo_Get_ByName_NoFallbacks (mapinfo.qc:1035-1381) opens maps/<bsp>.mapinfo and parses the
// title / description / author / gametype / has-* / size / hidden / cdtrack lines, accumulating the set
// of supported gametypes. The menu's XonoticMapInfoDialog_loadMapInfo then derives the displayed
// title/author (decolorized, author stripped from title) and the preview-image fallback chain
// (maps/<bsp> → levelshots/<bsp> → nopreview_map), and disables the gametype checklist entries the map
// doesn't support.
//
// This is a faithful, Godot-free (ADR-0008) port: the parser is pure string work; the file read and the
// image-existence probe are injected seams the menu host wires to the Godot VFS (tests pass fakes). The
// QC autogeneration path (writing a .mapinfo by scanning the BSP for spawnpoints/weapons) is NOT ported
// here — without a .mapinfo the map still appears in the create-game list (MapList scans maps/*.bsp), it
// just shows the bsp name as the title and an empty gametype set, which is the honest headless behavior.

using System;
using System.Collections.Generic;

namespace XonoticGodot.Common.Menu;

/// <summary>
/// The parsed contents of one map's <c>.mapinfo</c> — C# successor to the QC <c>MapInfo_Map_*</c> globals
/// (mapinfo.qh:6-13). <see cref="SupportedGametypes"/> holds gametype NetNames (the QC
/// <c>MapInfo_Map_supportedGametypes</c> bitmask, modeled here as a name set since the port has no
/// gametype flag bits).
/// </summary>
public sealed class MapInfo
{
    /// <summary>The bsp/file stem (QC MapInfo_Map_bspname).</summary>
    public string BspName = "";

    /// <summary>Decolorized title, author stripped (QC MapInfo_Map_title after strdecolorize + title_sans_author).</summary>
    public string Title = "";

    /// <summary>Decolorized author (QC MapInfo_Map_author; empty when the source was "&lt;AUTHOR&gt;").</summary>
    public string Author = "";

    /// <summary>Description line (QC MapInfo_Map_description; empty/"&lt;DESCRIPTION&gt;" when unset).</summary>
    public string Description = "";

    /// <summary>The gametype NetNames this map supports (QC MapInfo_Map_supportedGametypes).</summary>
    public readonly HashSet<string> SupportedGametypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if a "hidden" line was present (QC MAPINFO_FLAG_HIDDEN).</summary>
    public bool Hidden;

    /// <summary>True if a usable .mapinfo (or .arena/.defi — not ported) was found; false when the parser fell back to bare defaults.</summary>
    public bool HasMapInfoFile;

    /// <summary>Does this map support <paramref name="gametypeNetName"/>? (QC MapInfo_Map_supportedGametypes &amp; flags.)</summary>
    public bool Supports(string gametypeNetName) => SupportedGametypes.Contains(gametypeNetName);
}

/// <summary>
/// Parses <c>maps/&lt;bsp&gt;.mapinfo</c> and resolves a map's display title/author/preview + supported
/// gametypes — C# successor to <c>MapInfo_Get_ByName_NoFallbacks</c> (mapinfo.qc:1035-1381) and the
/// create-game dialog's <c>loadMapInfo</c> (dialog_multiplayer_create_mapinfo.qc:9-39). Godot-free: the
/// file read + image-exists probe are injected (the menu host wires them to the VFS).
/// </summary>
public sealed class MapInfoBackend
{
    private const string Untitled = "<TITLE>";
    private const string Unauthored = "<AUTHOR>";
    private const string Undescribed = "<DESCRIPTION>";

    private readonly Func<string, string?> _readText;  // vpath → text, or null when absent
    private readonly Func<string, bool> _imageExists;  // vpath → does the preview image exist
    private readonly Dictionary<string, MapInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="readText">Reads a map's .mapinfo text (e.g. "maps/foo.mapinfo"), or null if it isn't present.</param>
    /// <param name="imageExists">Probes whether a preview image vpath exists (the QC draw_PictureExists chain).</param>
    public MapInfoBackend(Func<string, string?> readText, Func<string, bool> imageExists)
    {
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _imageExists = imageExists ?? (_ => false);
    }

    /// <summary>
    /// Get (and cache) the parsed info for map <paramref name="bspName"/> — QC <c>MapInfo_Get_ByName</c>
    /// with the cache-retrieve shortcut (mapinfo.qc:1050-1052). The bsp name is the file stem
    /// (e.g. "stormkeep"); a "/" in it is rejected like the QC (mapinfo.qc:1044-1048).
    /// </summary>
    public MapInfo Get(string bspName)
    {
        if (_cache.TryGetValue(bspName, out MapInfo? cached))
            return cached;

        MapInfo info = Parse(bspName);
        _cache[bspName] = info;
        return info;
    }

    /// <summary>Drop the cache (QC MapInfo_Cache invalidation on a content reload).</summary>
    public void ClearCache() => _cache.Clear();

    private MapInfo Parse(string bspName)
    {
        var info = new MapInfo { BspName = bspName };

        if (bspName.IndexOf('/') >= 0) // mapinfo.qc:1044-1048 — invalid char in map name
            return Finish(info, rawTitle: Untitled, rawAuthor: Unauthored, rawDescription: Undescribed);

        string? text = _readText("maps/" + bspName + ".mapinfo");
        if (text == null)
        {
            // No .mapinfo and no .arena/.defi (not ported) and no autogeneration here: bare defaults.
            return Finish(info, rawTitle: Untitled, rawAuthor: Unauthored, rawDescription: Undescribed);
        }

        info.HasMapInfoFile = true;

        // _MapInfo_Map_Reset defaults (mapinfo.qc:443-456).
        string rawTitle = Untitled;
        string rawAuthor = Unauthored;
        string rawDescription = Undescribed;

        foreach (string line in SplitLines(text))
        {
            string s = line;

            // mapinfo.qc:1164-1175 — comment styles + trailing "//".
            if (s.Length == 0) continue;
            if (s[0] == '#') continue;                                  // UNIX style
            if (s.Length >= 2 && s[0] == '/' && s[1] == '/') continue;  // C++ style
            if (s[0] == '_') continue;                                  // q3map style
            int comment = s.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                s = s.Substring(0, comment);

            string t = Car(s, out string rest);
            if (t.Length == 0)
                continue;

            switch (t)
            {
                case "title":
                    rawTitle = rest;
                    break;
                case "description":
                    rawDescription = rest;
                    break;
                case "author":
                    rawAuthor = rest;
                    break;
                case "hidden":
                    info.Hidden = true;
                    break;
                case "type":   // legacy keyword (mapinfo.qc:1216-1226)
                case "gametype":
                {
                    string gt = Car(rest, out _);
                    string? netName = ResolveGametype(gt);
                    if (netName != null)
                        info.SupportedGametypes.Add(netName);
                    break;
                }
                // has / size / fog / cdtrack / settemp_for_type / flags: parsed-and-ignored here (the menu
                // map-info dialog only displays title/author/description/preview + the gametype checklist).
                default:
                    break;
            }
        }

        return Finish(info, rawTitle, rawAuthor, rawDescription);
    }

    // Apply the QC display normalization (mapinfo.qc:1360-1373): decolorize, strip author from title.
    private static MapInfo Finish(MapInfo info, string rawTitle, string rawAuthor, string rawDescription)
    {
        // title_sans_author: a title "Foo by Bar" promotes "Bar" to author when author is unset.
        string title = rawTitle;
        string author = rawAuthor;
        TitleSansAuthor(ref title, ref author);

        info.Title = MenuTextFormat.Decolorize(title == Untitled ? info.BspName : title);
        info.Author = (author == Unauthored) ? "" : MenuTextFormat.Decolorize(author); // mapinfo.qc:1372-1373
        info.Description = (rawDescription == Undescribed) ? "" : rawDescription;
        return info;
    }

    /// <summary>
    /// The preview-image fallback chain for map <paramref name="bspName"/> — QC loadMapInfo
    /// (dialog_multiplayer_create_mapinfo.qc:19-30): <c>/maps/&lt;bsp&gt;</c>, then the Quake 3
    /// <c>/levelshots/&lt;bsp&gt;</c>, then <c>nopreview_map</c>. Returns the first that exists (probed via the
    /// injected image-exists seam); always returns a non-null value (the last is the always-available
    /// placeholder).
    /// </summary>
    public string PreviewImage(string bspName)
    {
        string primary = "/maps/" + bspName;
        if (_imageExists(primary))
            return primary;
        string levelshot = "/levelshots/" + bspName; // Quake 3 compatibility (dialog:20-21)
        if (_imageExists(levelshot))
            return levelshot;
        return "nopreview_map"; // dialog:28
    }

    /// <summary>
    /// Resolve a <c>.mapinfo</c> gametype token to a port gametype NetName — C# successor to
    /// <c>MapInfo_Type_FromString</c> (mapinfo.qc:620-648): the deprecated-name remaps, then the gametype's
    /// short name (QC <c>mdl</c>). One port-specific fixup: QC's <c>ft</c> maps to the port's
    /// <c>freezetag</c> NetName (the lone NetName↔short-name mismatch, mirrored from CreateGameScreen).
    /// Returns null for an unknown token (QC returns NULL and the line is ignored). Q3-only forms (arena→ca)
    /// are accepted unconditionally here since the create-game list shows all supported modes.
    /// </summary>
    public static string? ResolveGametype(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        // mapinfo.qc:624-639 — deprecated / compat name remaps to the canonical short name.
        string gt = token switch
        {
            "nexball" => "nb",
            "freezetag" => "ft",
            "keepaway" => "ka",
            "invasion" => "inv",
            "assault" => "as",
            "race" => "rc",
            "ffa" => "dm",
            "cctf" => "ctf",
            "oneflag" => "ctf",
            "tourney" => "duel",
            "arena" => "ca", // Q3 compat (only when is_q3compat in QC; accepted here)
            _ => token,
        };

        // Port fixup: QC short name "ft" ↔ port NetName "freezetag" (CreateGameScreen.GametypeIconName).
        if (gt == "ft")
            return "freezetag";

        return gt;
    }

    // mapinfo.qc:title_sans_author — promote a trailing "by <author>" from the title to the author field.
    // The QC strips a "<title> by <author>" suffix and, if author was "<AUTHOR>", sets it. We mirror the
    // common-case " by " split (the QC also handles a few separators; this covers the shipped maps).
    private static void TitleSansAuthor(ref string title, ref string author)
    {
        if (title == Untitled)
            return;
        int by = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (by <= 0)
            return;
        string maybeAuthor = title.Substring(by + 4).Trim();
        if (maybeAuthor.Length == 0)
            return;
        title = title.Substring(0, by).Trim();
        if (author == Unauthored)
            author = maybeAuthor;
    }

    // QC car(): the first whitespace-delimited token; rest = the remainder (cdr).
    private static string Car(string s, out string rest)
    {
        int i = 0;
        int n = s.Length;
        while (i < n && IsSpace(s[i])) i++;
        int start = i;
        while (i < n && !IsSpace(s[i])) i++;
        string head = s.Substring(start, i - start);
        while (i < n && IsSpace(s[i])) i++;
        rest = i < n ? s.Substring(i) : "";
        return head;
    }

    private static bool IsSpace(char c) => c == ' ' || c == '\t';

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
