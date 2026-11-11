using System.Diagnostics;
using System.Windows.Automation;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Win32;

/// <summary>The input locator's state for "Copy diagnostics"; it holds no names, values or text of the Codex window.</summary>
/// <param name="Tracking">Whether the locator follows a Codex window now.</param>
/// <param name="Found">Whether the input card was seen at the last read.</param>
/// <param name="Searches">The number of full searches of the Codex window since start.</param>
/// <param name="LastSearchMs">How long the last search took, in milliseconds.</param>
/// <param name="LastError">The exception type of the last failed read, or <see langword="null"/>.</param>
public sealed record InputLocatorDiagnostics(bool Tracking, bool Found, long Searches, double LastSearchMs, string? LastError);

/// <summary>An <c>Edit</c> element of the Codex window as the search sees it: its class and its rectangle, nothing else.</summary>
/// <param name="ClassName">The element's class (the DOM <c>class</c> attribute in Chromium).</param>
/// <param name="Bounds">The bounding rectangle in physical pixels, or <see langword="null"/> when it has none.</param>
internal readonly record struct EditorCandidate(string? ClassName, WindowBounds? Bounds);

/// <summary>
/// Finds Codex Desktop's input card through UI Automation and follows it, so the overlay can sit above the input box
/// whatever the side panels and the card's height (spec §3.1, §7.2). Geometry only: it reads the control type, class
/// and bounding rectangle of elements, never their names, values or text.
/// <para>
/// The editor is the lowest <c>Edit</c> whose class contains <c>ProseMirror</c>; the card is its outermost raw-view
/// ancestor that still hugs it (<see cref="PickCard"/>). A search walks the whole window (about 30–50 ms); while none
/// finds a card, the next waits twice as long, from <see cref="ReadIntervalMs"/> up to <see cref="SearchIntervalMs"/>.
/// While a card is known, each read re-reads only the card's and the editor's rectangles (about a millisecond of CPU in
/// this process and in Codex) and searches again at once when they no longer fit together. Reads run every
/// <see cref="ReadIntervalMs"/> while the card moves and for <see cref="SettleMs"/> after, then every
/// <see cref="IdleReadIntervalMs"/>. Everything
/// runs on a thread-pool timer, never on the UI thread, because a UI Automation call waits on Codex's own UI thread;
/// one read at a time, re-armed after it ends. Results reach the UI thread through the captured
/// <see cref="SynchronizationContext"/>.
/// </para>
/// <para>
/// Querying the web content turns on Chromium's accessibility tree in Codex, as a screen reader does
/// (docs/compatibility.md §6.2).
/// </para>
/// </summary>
public sealed class CodexInputLocator : IDisposable
{
    /// <summary>How often the card's rectangle is re-read while it moves, and for <see cref="SettleMs"/> after, in milliseconds.</summary>
    public const int ReadIntervalMs = 300;

    /// <summary>How often the card's rectangle is re-read once it has stood still for <see cref="SettleMs"/>, in milliseconds.</summary>
    public const int IdleReadIntervalMs = 1000;

    /// <summary>How long after the last change reads stay at <see cref="ReadIntervalMs"/>, in milliseconds.</summary>
    public const int SettleMs = 2000;

    /// <summary>The longest wait between searches while no card is found, in milliseconds.</summary>
    public const int SearchIntervalMs = 2000;

    /// <summary>How long a lost card is still reported while searches look for it again, in milliseconds (measured: up to 1 s on a thread switch).</summary>
    public const int LostGraceMs = 1500;

    /// <summary>How much wider than the editor the card may be, in DIP (measured: 56).</summary>
    private const double MaxCardExtraWidthDip = 80;

    /// <summary>How much taller than the editor the card may be, in DIP (measured: 114 with the project chips).</summary>
    private const double MaxCardExtraHeightDip = 160;

    /// <summary>How far the card's horizontal centre may be from the editor's, in DIP (measured: 0).</summary>
    private const double MaxCentreSkewDip = 2;

    /// <summary>How much taller than the editor the visible box must be to hold the buttons row, in DIP (measured: 58).</summary>
    private const double MinBoxExtraHeightDip = 24;

    /// <summary>How many raw-view ancestors of the editor are considered (the card measured fifth).</summary>
    private const int MaxAncestors = 10;

    private static readonly Condition EditCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    private readonly SynchronizationContext _ui;
    private readonly Action _changed;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    // Owned by the UI thread.
    private int _generation;

    // Shared under _gate.
    private nint _hwnd;
    private int _sharedGeneration;
    private bool _running;
    private bool _reading;
    private bool _disposed;
    private bool _found;
    private long _searches;
    private double _lastSearchMs;
    private string? _lastError;

    // Owned by the read in flight (one at a time).
    private nint _cachedHwnd;
    private AutomationElement? _editor;
    private AutomationElement? _card;
    private AutomationElement? _box;
    private long _lastSearchTimestamp;
    private int _searchDelayMs;
    private InputSighting? _lastSeen;
    private long _lostTimestamp;
    private InputSighting? _published;
    private int _publishedGeneration;
    private long _changedTimestamp;

    /// <summary>Creates the locator; <see cref="Track"/> starts it.</summary>
    /// <param name="ui">The UI thread's context; <see cref="Current"/> changes and <paramref name="changed"/> runs there.</param>
    /// <param name="changed">Called on the UI thread after <see cref="Current"/> changed.</param>
    public CodexInputLocator(SynchronizationContext ui, Action changed)
    {
        _ui = ui;
        _changed = changed;
        _timer = new Timer(_ => Read(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Gets where the input card was last seen in the tracked window, or <see langword="null"/>. Read it on the UI thread.</summary>
    public InputSighting? Current { get; private set; }

    /// <summary>
    /// Follows the input card of a Codex window, reading it at once. Tracking the same window again only resumes it; another
    /// window forgets <see cref="Current"/>. Call it on the UI thread.
    /// </summary>
    /// <param name="hwnd">The Codex window.</param>
    public void Track(nint hwnd)
    {
        lock (_gate)
        {
            if (_disposed || (_running && _hwnd == hwnd))
            {
                return;
            }

            if (_hwnd != hwnd)
            {
                _hwnd = hwnd;
                _sharedGeneration = ++_generation;
                Current = null;
            }

            _running = true;
            _timer.Change(0, Timeout.Infinite);
        }
    }

    /// <summary>Stops reading, keeping <see cref="Current"/> for a later <see cref="Track"/> of the same window. Call it on the UI thread.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;

            if (!_disposed)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>Describes the locator for diagnostics.</summary>
    /// <returns>The locator state.</returns>
    public InputLocatorDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            return new InputLocatorDiagnostics(_running, _found, _searches, _lastSearchMs, _lastError);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _timer.Dispose();
        }
    }

    /// <summary>
    /// Picks the editor among the window's <c>Edit</c> elements: the lowest one inside the window whose class contains
    /// <c>ProseMirror</c>. Codex's composer is that editor; other edits (a settings search box) are not.
    /// </summary>
    /// <param name="candidates">The <c>Edit</c> elements found.</param>
    /// <param name="window">The Codex window bounds.</param>
    /// <returns>The index of the editor, or -1 when there is none.</returns>
    internal static int PickEditor(IReadOnlyList<EditorCandidate> candidates, WindowBounds window)
    {
        int best = -1;

        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] is { ClassName: { } className, Bounds: { } bounds }
                && className.Contains("ProseMirror", StringComparison.Ordinal)
                && bounds.Width > 0 && bounds.Height > 0 && Contains(window, bounds)
                && (best < 0 || Bottom(bounds) > Bottom(candidates[best].Bounds!.Value)))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Picks the input card among the editor's ancestors, nearest first: walking up, the last one that still hugs the
    /// editor, i.e. is at most <see cref="MaxCardExtraWidthDip"/> wider and <see cref="MaxCardExtraHeightDip"/> taller; the
    /// first one bigger than that (the chat column) ends the walk. The card holds the project chips above the editor and
    /// the buttons below it, and is centred on the editor. An ancestor without a rectangle, or one that does not contain
    /// the editor centred (<see cref="FitsAround"/>), is skipped: Chromium keeps the stale size of a <c>display: contents</c>
    /// element after a DPI change (measured: 920 px wide at 100% in place of 736).
    /// </summary>
    /// <param name="editor">The editor's rectangle.</param>
    /// <param name="ancestors">The editor's ancestors' rectangles, nearest first; <see langword="null"/> for one without a rectangle.</param>
    /// <param name="dpiScale">The window's DPI scale, where 1.0 is 96 DPI.</param>
    /// <returns>The index of the card, or -1 when no ancestor hugs the editor and the editor itself is the card.</returns>
    internal static int PickCard(WindowBounds editor, IReadOnlyList<WindowBounds?> ancestors, double dpiScale)
    {
        int maxExtraWidth = (int)Math.Round(MaxCardExtraWidthDip * dpiScale);
        int maxExtraHeight = (int)Math.Round(MaxCardExtraHeightDip * dpiScale);
        int card = -1;

        for (int i = 0; i < ancestors.Count; i++)
        {
            if (ancestors[i] is not { } ancestor || !FitsAround(ancestor, editor, dpiScale))
            {
                continue;
            }

            if (ancestor.Width - editor.Width > maxExtraWidth || ancestor.Height - editor.Height > maxExtraHeight)
            {
                break;
            }

            card = i;
        }

        return card;
    }

    /// <summary>
    /// Picks the visible input box among the editor's ancestors up to the card: the nearest one that fits around the
    /// editor and is at least <see cref="MinBoxExtraHeightDip"/> taller, so it holds the buttons row. The card around it
    /// adds side padding (measured: 961 px wide against the box's 921 at 125 %) and, on New chat, the project chips.
    /// </summary>
    /// <param name="editor">The editor's rectangle.</param>
    /// <param name="ancestors">The editor's ancestors' rectangles, nearest first; <see langword="null"/> for one without a rectangle.</param>
    /// <param name="card">The card's index from <see cref="PickCard"/>; the search stops there.</param>
    /// <param name="dpiScale">The window's DPI scale, where 1.0 is 96 DPI.</param>
    /// <returns>The index of the box, or -1 when none below the card qualifies and the card is the box.</returns>
    internal static int PickBox(WindowBounds editor, IReadOnlyList<WindowBounds?> ancestors, int card, double dpiScale)
    {
        int minExtraHeight = (int)Math.Round(MinBoxExtraHeightDip * dpiScale);

        for (int i = 0; i <= card && i < ancestors.Count; i++)
        {
            if (ancestors[i] is { } ancestor && FitsAround(ancestor, editor, dpiScale) && ancestor.Height - editor.Height >= minExtraHeight)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether a card rectangle contains the editor and is centred on it, as Codex's composer is.</summary>
    /// <param name="card">The card's rectangle.</param>
    /// <param name="editor">The editor's rectangle.</param>
    /// <param name="dpiScale">The window's DPI scale, where 1.0 is 96 DPI.</param>
    /// <returns><see langword="true"/> when the two fit together.</returns>
    internal static bool FitsAround(WindowBounds card, WindowBounds editor, double dpiScale)
    {
        // Doubled centres stay whole numbers.
        int skew = Math.Abs((2 * card.Left) + card.Width - (2 * editor.Left) - editor.Width);

        return card.Width > 0 && card.Height > 0 && Contains(card, editor) && skew <= 2 * Math.Round(MaxCentreSkewDip * dpiScale);
    }

    private static int Bottom(WindowBounds bounds) => bounds.Top + bounds.Height;

    private static bool Contains(WindowBounds outer, WindowBounds inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Left + inner.Width <= outer.Left + outer.Width && Bottom(inner) <= Bottom(outer);

    private static WindowBounds? ToBounds(System.Windows.Rect rect) =>
        rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0
            ? null
            : new WindowBounds((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    /// <summary>
    /// One read on the timer thread: the card's rectangle, or a search when no card is known and one is due. Only one read
    /// runs at a time; it re-arms the timer when it ends, at once when the tracked window changed meanwhile.
    /// </summary>
    private void Read()
    {
        nint hwnd;
        int generation;

        lock (_gate)
        {
            if (!_running || _disposed || _reading)
            {
                return;
            }

            _reading = true;
            hwnd = _hwnd;
            generation = _sharedGeneration;
        }

        InputSighting? sighting = null;
        string? error = null;

        try
        {
            sighting = ReadSighting(hwnd);
        }
        catch (Exception exception)
        {
            // A UI Automation call fails when Codex closes or hangs mid-call; the next read searches again.
            // This is a thread-pool callback: an escaping exception would end the process.
            _editor = null;
            _card = null;
            _box = null;
            error = exception.GetType().Name;
        }

        bool changed = generation != _publishedGeneration || sighting != _published;
        _published = sighting;
        _publishedGeneration = generation;

        if (changed)
        {
            _changedTimestamp = Stopwatch.GetTimestamp();
        }

        // A card that stands still is read once a second: each read costs both processes about a millisecond.
        int delay = Stopwatch.GetElapsedTime(_changedTimestamp).TotalMilliseconds < SettleMs ? ReadIntervalMs : IdleReadIntervalMs;

        lock (_gate)
        {
            _reading = false;
            _found = sighting is not null;

            if (error is not null)
            {
                _lastError = error;
            }

            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                _timer.Change(generation == _sharedGeneration ? delay : 0, Timeout.Infinite);
            }
        }

        if (changed)
        {
            _ui.Post(_ =>
            {
                // A sighting of a window no longer tracked, or arriving after Dispose, is dropped.
                if (!_disposed && generation == _generation)
                {
                    Current = sighting;
                    _changed();
                }
            }, null);
        }
    }

    /// <summary>Reads the known card, or searches the window when none is known and a search is due.</summary>
    /// <param name="hwnd">The Codex window.</param>
    /// <returns>The sighting, or <see langword="null"/> when no card is seen.</returns>
    private InputSighting? ReadSighting(nint hwnd)
    {
        WindowGeometry geometry = WindowGeometry.Read(hwnd);

        if (geometry.Bounds is not { } window || NativeMethods.IsIconic(hwnd))
        {
            return null;
        }

        if (_cachedHwnd != hwnd)
        {
            _cachedHwnd = hwnd;
            _editor = null;
            _card = null;
            _box = null;
            _searchDelayMs = 0;
            _lastSeen = null;
        }

        if (_editor is not null && _card is not null)
        {
            try
            {
                if (ToBounds(_card.Current.BoundingRectangle) is { } known && Contains(window, known)
                    && ToBounds(_editor.Current.BoundingRectangle) is { } editor && FitsAround(known, editor, geometry.DpiScale))
                {
                    WindowBounds? box = _box is null ? null : ToBounds(_box.Current.BoundingRectangle);

                    return Seen(new InputSighting(known, window, box is { } inner && FitsAround(inner, editor, geometry.DpiScale) ? inner : null));
                }
            }
            catch (ElementNotAvailableException)
            {
                // Codex replaced the composer, as when another thread or page opens.
            }

            // Lost a card that was there, or it no longer fits its editor: search in this same read.
            _editor = null;
            _card = null;
            _box = null;
            _searchDelayMs = 0;
            _lostTimestamp = Stopwatch.GetTimestamp();
        }

        // No card known: search again after a delay that doubles from ReadIntervalMs to SearchIntervalMs while searches
        // fail. A card lost mid-relayout (another monitor's DPI) is back within a read or two; a page without one costs
        // a search every SearchIntervalMs.
        if (Stopwatch.GetElapsedTime(_lastSearchTimestamp).TotalMilliseconds < _searchDelayMs)
        {
            return Held();
        }

        long started = Stopwatch.GetTimestamp();
        _lastSearchTimestamp = started;
        InputSighting? searched = Search(hwnd, window, geometry.DpiScale);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _searchDelayMs = searched is null ? Math.Clamp(_searchDelayMs * 2, ReadIntervalMs, SearchIntervalMs) : 0;

        lock (_gate)
        {
            _searches++;
            _lastSearchMs = elapsed;
        }

        return searched is { } found ? Seen(found) : Held();
    }

    /// <summary>Remembers a card that was just seen.</summary>
    /// <param name="sighting">The sighting.</param>
    /// <returns>The same sighting.</returns>
    private InputSighting Seen(InputSighting sighting)
    {
        _lastSeen = sighting;

        return sighting;
    }

    /// <summary>
    /// The sighting to report while no card is seen: the last one for <see cref="LostGraceMs"/> after it was lost, so
    /// another thread's composer mounting does not drop the overlay to the window rule and back; then none.
    /// </summary>
    /// <returns>The held sighting, or <see langword="null"/>.</returns>
    private InputSighting? Held()
    {
        if (_lastSeen is { } last && Stopwatch.GetElapsedTime(_lostTimestamp).TotalMilliseconds < LostGraceMs)
        {
            return last;
        }

        _lastSeen = null;

        return null;
    }

    /// <summary>Searches the window for the editor, its card and its box, and keeps the elements for the following reads.</summary>
    /// <param name="hwnd">The Codex window.</param>
    /// <param name="window">Its bounds.</param>
    /// <param name="dpiScale">Its DPI scale.</param>
    /// <returns>The sighting, or <see langword="null"/> when there is no editor.</returns>
    private InputSighting? Search(nint hwnd, WindowBounds window, double dpiScale)
    {
        AutomationElementCollection edits = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, EditCondition);
        List<AutomationElement> elements = [];
        List<EditorCandidate> candidates = [];

        foreach (AutomationElement edit in edits)
        {
            elements.Add(edit);
            candidates.Add(new EditorCandidate(edit.Current.ClassName, ToBounds(edit.Current.BoundingRectangle)));
        }

        int index = PickEditor(candidates, window);

        if (index < 0)
        {
            return null;
        }

        AutomationElement editor = elements[index];
        WindowBounds editorBounds = candidates[index].Bounds!.Value;
        List<AutomationElement> ancestors = [];
        List<WindowBounds?> ancestorBounds = [];

        // The raw view: the control view leaves out the card's own boxes and keeps only a display: contents wrapper.
        TreeWalker walker = TreeWalker.RawViewWalker;

        for (AutomationElement? parent = walker.GetParent(editor); parent is not null && ancestors.Count < MaxAncestors; parent = walker.GetParent(parent))
        {
            ancestors.Add(parent);
            ancestorBounds.Add(ToBounds(parent.Current.BoundingRectangle));
        }

        int card = PickCard(editorBounds, ancestorBounds, dpiScale);
        int box = PickBox(editorBounds, ancestorBounds, card, dpiScale);
        _editor = editor;
        _card = card < 0 ? editor : ancestors[card];
        _box = box < 0 ? null : ancestors[box];

        return new InputSighting(card < 0 ? editorBounds : ancestorBounds[card]!.Value, window, box < 0 ? null : ancestorBounds[box]);
    }
}
