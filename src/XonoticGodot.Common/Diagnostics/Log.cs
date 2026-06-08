using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Diagnostics;

/// <summary>The seven Xonotic log levels (<c>lib/log.qh</c>), in the file's declaration order.</summary>
public enum LogLevel
{
    /// <summary>QC <c>LOG_FATAL</c> — unrecoverable; logged then <see cref="Log.Fatal"/> throws (QC <c>error()</c>).</summary>
    Fatal,

    /// <summary>QC <c>LOG_SEVERE</c> — a serious bug; logged with a stack trace (QC <c>backtrace()</c>), execution continues.</summary>
    Severe,

    /// <summary>QC <c>LOG_WARN</c> — a non-fatal problem; always shown.</summary>
    Warn,

    /// <summary>QC <c>LOG_INFO</c> — informational; the message is always shown, the <c>[::prog::INFO]</c> header only at <c>developer&gt;0</c>.</summary>
    Info,

    /// <summary>QC <c>LOG_TRACE</c> — developer trace (QC <c>dprint</c>); shown only at <c>developer&gt;0</c>.</summary>
    Trace,

    /// <summary>QC <c>LOG_DEBUG</c> — verbose developer debug; shown only at <c>developer&gt;1</c>.</summary>
    Debug,

    /// <summary>QC <c>LOG_HELP</c> — command help text; always shown, never carries a header.</summary>
    Help,
}

/// <summary>
/// Thrown by <see cref="Log.Fatal"/> after the message is logged — the C# successor to QuakeC's
/// <c>error()</c> builtin (which prints then aborts the program). Catchable so a host/test can decide
/// whether a fatal gameplay invariant should tear down the match or just the current operation.
/// </summary>
public sealed class LogFatalException : Exception
{
    public LogFatalException(string message) : base(message) { }
}

/// <summary>
/// The game-code logging facade — the C# successor to Xonotic's <c>lib/log.qh</c> (<c>LOG_INFO</c> /
/// <c>LOG_TRACE</c> / <c>LOG_DEBUG</c> / <c>LOG_WARN</c> / <c>LOG_SEVERE</c> / <c>LOG_FATAL</c> /
/// <c>LOG_HELP</c>) layered over the <c>print</c>/<c>dprint</c> builtins. Lives in <c>XonoticGodot.Common</c> so
/// both the headless server core and the Godot client/menu call one API; it never references Godot.
///
/// <para><b>Verbosity gate.</b> Exactly mirrors QC's <c>autocvar_developer</c>:
/// <list type="bullet">
///   <item><see cref="Fatal"/>, <see cref="Severe"/>, <see cref="Warn"/>, <see cref="Help"/> — always emitted.</item>
///   <item><see cref="Info"/> — the message is always emitted; the <c>[::prog::INFO]</c> header appears only at
///         <c>developer&gt;0</c>, and the source location only at <c>developer&gt;1</c>.</item>
///   <item><see cref="Trace"/> — emitted only at <c>developer&gt;0</c> (QC <c>dprint</c>).</item>
///   <item><see cref="Debug"/> — emitted only at <c>developer&gt;1</c>.</item>
/// </list>
/// The level is read live from the <c>developer</c> cvar (via <see cref="DeveloperLevel"/>), so
/// <c>set developer 1</c> / <c>toggle developer</c> take effect immediately, exactly like the engine.</para>
///
/// <para><b>Sink.</b> Every emitted line goes to <see cref="Sink"/> as <c>(level, line)</c>. The default
/// (<see cref="DefaultConsoleSink"/>) writes to <see cref="Console"/> — stderr for Fatal/Severe/Warn, stdout
/// otherwise — so output is <em>never silently dropped</em> (headless server, <c>dotnet test</c>). The Godot
/// host overrides it at boot to route to <c>GD.Print</c>/<c>GD.PrintErr</c> (see <c>Main._Ready</c>), which is
/// what appears in the editor Output panel and the player console. A richer sink can colour by
/// <see cref="LogLevel"/>.</para>
///
/// <para><b>Source location.</b> Captured at the call site for free via <see cref="CallerFilePathAttribute"/> /
/// <see cref="CallerLineNumberAttribute"/> / <see cref="CallerMemberNameAttribute"/> (compile-time, no PDB
/// needed) — the C# equivalent of QC's <c>__FILE__</c>/<c>__LINE__</c>/<c>__FUNC__</c>.</para>
///
/// <para><b>Formatting.</b> QC needs <c>LOG_INFOF</c>/<c>sprintf</c> because QuakeC has no string interpolation;
/// C# does, so a single <c>string</c> overload suffices — write <c>Log.Info($"loaded {n} cvars")</c>. For a
/// genuinely hot path, guard with <see cref="WillTrace"/>/<see cref="WillDebug"/> to skip building the string
/// when the level is disabled.</para>
/// </summary>
public static class Log
{
    private static readonly char[] PathSeparators = { '/', '\\' };
    private static readonly object ConsoleLock = new();

    /// <summary>
    /// The program tag in the header (QC <c>PROGNAME</c>: SVQC/CSQC/MENUQC). The host sets it once at boot —
    /// <c>"server"</c> on the headless/listen-server core, <c>"client"</c> in the Godot front-end.
    /// </summary>
    public static string Program { get; set; } = "game";

    /// <summary>
    /// Where formatted log lines go: <c>(level, line)</c>. Defaults to <see cref="DefaultConsoleSink"/> so logs
    /// are visible without any wiring; the Godot host replaces it with a <c>GD.Print</c>/<c>GD.PrintErr</c> sink.
    /// Lines may contain Quake <c>^</c> colour codes — a plain sink should run them through
    /// <see cref="StripColors"/> (the default sink does).
    /// </summary>
    public static Action<LogLevel, string> Sink { get; set; } = DefaultConsoleSink;

    /// <summary>
    /// Reads the current verbosity (QC <c>autocvar_developer</c>). Defaults to the <c>developer</c> cvar via the
    /// ambient facade (0 when no world/cvar is booted). A test can override it to drive gating without a cvar store.
    /// </summary>
    public static Func<int> DeveloperLevel { get; set; } = DefaultDeveloperLevel;

    // ============================================================================ verbosity queries

    /// <summary>The live developer level (QC <c>autocvar_developer</c>).</summary>
    public static int Developer => DeveloperLevel();

    /// <summary>True when <see cref="Trace"/> output is enabled (<c>developer&gt;0</c>). Guard hot trace sites with this.</summary>
    public static bool WillTrace => DeveloperLevel() > 0;

    /// <summary>True when <see cref="Debug"/> output is enabled (<c>developer&gt;1</c>). Guard hot debug sites with this.</summary>
    public static bool WillDebug => DeveloperLevel() > 1;

    /// <summary>Whether a message at <paramref name="level"/> would currently be emitted (mirrors the QC gate).</summary>
    public static bool IsEnabled(LogLevel level) => level switch
    {
        LogLevel.Trace => DeveloperLevel() > 0,
        LogLevel.Debug => DeveloperLevel() > 1,
        _ => true,
    };

    // ============================================================================ the level methods

    /// <summary>QC <c>LOG_FATAL</c>: log at fatal level, then throw <see cref="LogFatalException"/> (QC <c>error()</c> aborts).</summary>
    public static void Fatal(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Fatal, message, file, line, member);

    /// <summary>QC <c>LOG_SEVERE</c>: log at severe level with a stack trace (QC <c>backtrace()</c>); execution continues.</summary>
    public static void Severe(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Severe, message, file, line, member);

    /// <summary>QC <c>LOG_WARN</c>: a non-fatal warning; always shown.</summary>
    public static void Warn(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Warn, message, file, line, member);

    /// <summary>QC <c>LOG_INFO</c>: informational; the message is always shown, the header only at <c>developer&gt;0</c>.</summary>
    public static void Info(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Info, message, file, line, member);

    /// <summary>QC <c>LOG_TRACE</c>: developer trace; shown only at <c>developer&gt;0</c>.</summary>
    public static void Trace(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Trace, message, file, line, member);

    /// <summary>QC <c>LOG_DEBUG</c>: verbose developer debug; shown only at <c>developer&gt;1</c>.</summary>
    public static void Debug(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Debug, message, file, line, member);

    /// <summary>QC <c>LOG_HELP</c>: command help text; always shown, never carries a header.</summary>
    public static void Help(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Emit(LogLevel.Help, message, file, line, member);

    // ============================================================================ the core (QC _LOG)

    /// <summary>
    /// The C# successor to QC's <c>_LOG</c> macro family: assemble the header (gated like <c>_LOG_HEADER</c>) +
    /// message for <paramref name="level"/> and hand it to <see cref="Sink"/>, applying each level's
    /// <c>autocvar_developer</c> gate.
    /// </summary>
    private static void Emit(LogLevel level, string message, string file, int line, string member)
    {
        Action<LogLevel, string>? sink = Sink;
        if (sink is null)
            return;

        int dev = DeveloperLevel();
        message ??= "";

        switch (level)
        {
            case LogLevel.Help:
                sink(level, message);                                    // QC: bare message, no header
                break;

            case LogLevel.Info:
                // QC _LOG_INFO: message always; header only at dev>0; source location only at dev>1.
                sink(level, dev > 0 ? Header("INFO", dev > 1, file, line, member) + message : message);
                break;

            case LogLevel.Warn:
                // QC _LOG_WARN: always; source location at dev>0.
                sink(level, Header("WARNING", dev > 0, file, line, member) + message);
                break;

            case LogLevel.Trace:
                // QC _LOG_TRACE (dprint): only at dev>0.
                if (dev > 0)
                    sink(level, Header("TRACE", true, file, line, member) + message);
                break;

            case LogLevel.Debug:
                // QC _LOG_DEBUG: only at dev>1.
                if (dev > 1)
                    sink(level, Header("DEBUG", true, file, line, member) + message);
                break;

            case LogLevel.Severe:
                // QC _LOG_SEVERE: header (always) + backtrace(message). backtrace() brackets the message and
                // the stack with the engine's CUT markers; we use the managed stack trace.
                sink(level, Header("SEVERE", dev > 0, file, line, member)
                    + "\n--- CUT HERE ---\n" + message + "\n" + Environment.StackTrace + "\n--- CUT UNTIL HERE ---");
                break;

            case LogLevel.Fatal:
                // QC _LOG_FATAL: header (always) + error(message). error() aborts → we throw after logging.
                sink(level, Header("FATAL", dev > 0, file, line, member) + message);
                throw new LogFatalException(message);
        }
    }

    /// <summary>
    /// QC <c>_LOG_HEADER</c>: <c>[::&lt;prog&gt;::&lt;level&gt;] </c>, optionally followed by the source location
    /// (QC <c>__SOURCELOC__</c>, which ends in a newline so the message starts on the next line).
    /// </summary>
    private static string Header(string levelTag, bool sourceLoc, string file, int line, string member)
    {
        string head = "[::" + Program + "::" + levelTag + "] ";
        if (!sourceLoc)
            return head;
        return head + member + " (" + FileName(file) + ":" + line.ToString(CultureInfo.InvariantCulture) + ")\n";
    }

    private static string FileName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "?";
        int i = path.LastIndexOfAny(PathSeparators);
        return i >= 0 ? path.Substring(i + 1) : path;
    }

    // ============================================================================ sinks + colour codes

    /// <summary>
    /// The default <see cref="Sink"/>: write colour-stripped lines to <see cref="Console"/> — stderr for
    /// Fatal/Severe/Warn, stdout for everything else — under a lock so concurrent logs don't interleave.
    /// </summary>
    public static void DefaultConsoleSink(LogLevel level, string line)
    {
        string text = StripColors(line);
        bool err = level is LogLevel.Fatal or LogLevel.Severe or LogLevel.Warn;
        lock (ConsoleLock)
            (err ? Console.Error : Console.Out).WriteLine(text);
    }

    /// <summary>
    /// Remove Quake/DarkPlaces colour codes from a string: <c>^0</c>–<c>^9</c> (single digit), <c>^xRGB</c>
    /// (3 hex digits), and <c>^^</c> → a literal <c>^</c>. Fast-paths strings with no caret. Used by plain-text
    /// sinks (console, <c>GD.Print</c>) since they don't render the codes.
    /// </summary>
    public static string StripColors(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('^') < 0)
            return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }        // ^^  -> literal ^
                if (n is >= '0' and <= '9') { i++; continue; }          // ^d  -> drop
                if (n == 'x' && i + 4 < s.Length                        // ^xRGB -> drop
                    && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]))
                {
                    i += 4;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    // ============================================================================ Godot rich markup

    /// <summary>
    /// Translate a formatted log line into Godot BBCode for <c>GD.PrintRich</c>: literal <c>[</c> is escaped
    /// (<c>[lb]</c>) so our <c>[::prog::LEVEL]</c> header and any message brackets aren't parsed as tags, Quake
    /// <c>^</c> colour codes become nested <c>[color]</c> spans, and the whole line is tinted by
    /// <see cref="LogLevel"/> (Info/Help keep the panel's default colour). A pure string transform — no Godot
    /// dependency — so the host's rich sink is a one-liner and this stays unit-testable.
    /// </summary>
    public static string ToBBCode(LogLevel level, string line)
    {
        line ??= "";
        string? tint = LevelColor(level);
        var sb = new StringBuilder(line.Length + 32);
        if (tint != null)
            sb.Append("[color=").Append(tint).Append(']');

        bool spanOpen = false;          // a ^-code colour span currently open inside the level tint
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '^' && i + 1 < line.Length)
            {
                char n = line[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }            // ^^ -> literal ^
                string? span = QuakeColorToBBCode(n, line, i, out int extra);
                if (span != null)
                {
                    if (spanOpen) { sb.Append("[/color]"); spanOpen = false; }
                    if (span.Length > 0) { sb.Append("[color=").Append(span).Append(']'); spanOpen = true; }
                    i += extra;
                    continue;
                }
                // unrecognised ^code: fall through and emit the caret literally
            }
            if (c == '[') { sb.Append("[lb]"); continue; }                  // escape so it isn't read as a tag
            sb.Append(c);
        }

        if (spanOpen) sb.Append("[/color]");
        if (tint != null) sb.Append("[/color]");
        return sb.ToString();
    }

    /// <summary>The Output-panel tint for a level (null = the panel's default colour, used for Info/Help).</summary>
    private static string? LevelColor(LogLevel level) => level switch
    {
        LogLevel.Fatal => "#ff5555",
        LogLevel.Severe => "#ff5555",
        LogLevel.Warn => "#ffcc33",
        LogLevel.Trace => "#cc88ff",
        LogLevel.Debug => "#88cc88",
        _ => null,                       // Info, Help -> default colour
    };

    /// <summary>
    /// One Quake colour code → a Godot BBCode colour: a hex string for <c>^0</c>–<c>^9</c> / <c>^xRGB</c>,
    /// <c>""</c> for <c>^7</c> (reset to the surrounding colour), or null when it isn't a colour code.
    /// <paramref name="extra"/> receives how many chars after the caret the code occupies.
    /// </summary>
    private static string? QuakeColorToBBCode(char first, string s, int caret, out int extra)
    {
        extra = 0;
        if (first is >= '0' and <= '9')
        {
            extra = 1;
            return first switch
            {
                '0' => "#000000",
                '1' => "#ff0000",
                '2' => "#00ff00",
                '3' => "#ffff00",
                '4' => "#0000ff",
                '5' => "#00ffff",
                '6' => "#ff00ff",
                '7' => "",               // white == reset to the surrounding colour
                '8' => "#999999",
                _ => "#cccccc",          // ^9
            };
        }
        if (first == 'x' && caret + 4 < s.Length
            && IsHex(s[caret + 2]) && IsHex(s[caret + 3]) && IsHex(s[caret + 4]))
        {
            char r = s[caret + 2], g = s[caret + 3], b = s[caret + 4]; // ^xRGB (3 hex) -> #RRGGBB
            extra = 4;
            return new string(new[] { '#', r, r, g, g, b, b });
        }
        return null;
    }

    // ============================================================================ test support

    private static int DefaultDeveloperLevel()
        => Api.Services is null ? 0 : (int)Api.Cvars.GetFloat("developer");

    /// <summary>Restore <see cref="Sink"/>, <see cref="Program"/>, and <see cref="DeveloperLevel"/> to defaults
    /// (the facade is global static state; tests that override them reset here, e.g. in test teardown).</summary>
    public static void ResetForTests()
    {
        Sink = DefaultConsoleSink;
        Program = "game";
        DeveloperLevel = DefaultDeveloperLevel;
    }
}
