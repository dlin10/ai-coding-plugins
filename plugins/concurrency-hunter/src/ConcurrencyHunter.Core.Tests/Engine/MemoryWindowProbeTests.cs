using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the windows <c>Memory&lt;T&gt;</c>, <c>ReadOnlyMemory&lt;T&gt;</c> and <c>Span&lt;T&gt;</c> cut over an array
/// field (issue 537), each with what run time calls for. Every probe is one singleton <c>Registry</c> shared by two hosted services,
/// each running one statement on it, analysed through the whole pipeline (<see cref="HostedServiceProbe"/>). The truths rest on these
/// guarantees (Learn, <c>MemoryExtensions.AsMemory</c>, <c>Memory&lt;T&gt;</c>, <c>ReadOnlyMemory&lt;T&gt;</c>, <c>Span&lt;T&gt;</c>):
/// <c>AsMemory(array, start, length)</c> and the constructors <c>Memory&lt;T&gt;(array, start, length)</c>,
/// <c>ReadOnlyMemory&lt;T&gt;(array, start, length)</c> and <c>Span&lt;T&gt;(array, start, length)</c> create a region over that portion
/// of the array, from <c>start</c> (the whole array, or from <c>start</c> to its end, where they name less), without copying it; an
/// <c>Index</c> from the end, <c>^n</c>, is <c>Length - n</c>, and a <c>Range</c> starts at its start; <c>Slice(start, length)</c> of
/// a memory forms a slice of the region it is called on; the implicit conversions of an array, of an <c>ArraySegment&lt;T&gt;</c> and of
/// a <c>Memory&lt;T&gt;</c> to a <c>Memory&lt;T&gt;</c> or a <c>ReadOnlyMemory&lt;T&gt;</c> keep the storage and the window they convert;
/// <c>Span</c> of a memory is a span over the same region, so its index <c>i</c> is the array's cell <c>start + i</c>, read or written
/// in place, and an object read through it is the object that cell of the array holds. A memory is a struct: a copy in a local, in a
/// field or in a parameter covers the same region, and a conditional gives one of its windows. <c>Parallel.For</c> may run its
/// iterations at once (Learn, <c>Parallel.For</c>), so two iterations writing one cell race. A string is immutable, and
/// <c>string.AsMemory()</c> gives only a read-only window on its characters. Two array cells at different indices are different
/// storage locations (C# specification, arrays); an access past a window's end throws, so a write through a window never reaches a cell
/// outside it. Every truth is the behaviour of the program the fixture stands for, its registrations taken as written. A probe the
/// analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class MemoryWindowProbeTests
{
    private const string CELLS = "private readonly int[] _cells = new int[4];\n";

    /// <summary>An array of two objects, filled in the constructor: an initializer would give the heap no region for them.</summary>
    private const string BOXES = "private readonly Box[] _boxes = new Box[2];\npublic Registry() { _boxes[0] = new Box(); _boxes[1] = new Box(); }\n";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["as-memory-whole-array"] = (Registry(CELLS + """
                                                    public void Fill() => _cells.AsMemory().Span[1] = 1;
                                                    public void Set() => _cells[1] = 2;
                                                    """, "_registry.Fill();", "_registry.Set();"),
                                     ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["as-memory-from-a-start"] = (Registry(CELLS + """
                                                     public void Fill() => _cells.AsMemory(1).Span[0] = 1;
                                                     public void Set() => _cells[1] = 2;
                                                     """, "_registry.Fill();", "_registry.Set();"),
                                      ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["as-memory-window-against-another-cell"] = (Registry(CELLS + """
                                                                    public void Fill() => _cells.AsMemory(1, 2).Span[0] = 1;
                                                                    public void Set() => _cells[0] = 2;
                                                                    """, "_registry.Fill();", "_registry.Set();"), []),
        ["as-memory-index-from-the-end"] = (Registry(CELLS + """
                                                           public void Fill() => _cells.AsMemory(^3).Span[0] = 1;
                                                           public void Set() => _cells[1] = 2;
                                                           """, "_registry.Fill();", "_registry.Set();"),
                                            ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["as-memory-index-from-the-end-against-another-cell"] = (Registry(CELLS + """
                                                                                public void Fill() => _cells.AsMemory(^1).Span[0] = 1;
                                                                                public void Set() => _cells[0] = 2;
                                                                                """, "_registry.Fill();", "_registry.Set();"), []),
        ["as-memory-range"] = (Registry(CELLS + """
                                              public void Fill() => _cells.AsMemory(1..3).Span[0] = 1;
                                              public void Set() => _cells[1] = 2;
                                              """, "_registry.Fill();", "_registry.Set();"),
                               ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["as-memory-range-against-another-cell"] = (Registry(CELLS + """
                                                                   public void Fill() => _cells.AsMemory(1..3).Span[0] = 1;
                                                                   public void Set() => _cells[0] = 2;
                                                                   """, "_registry.Fill();", "_registry.Set();"), []),
        ["memory-slice"] = (Registry(CELLS + """
                                           public void Fill() => _cells.AsMemory(0, 2).Slice(1).Span[0] = 1;
                                           public void Set() => _cells[1] = 2;
                                           """, "_registry.Fill();", "_registry.Set();"),
                            ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-slice-against-another-cell"] = (Registry(CELLS + """
                                                                public void Fill() => _cells.AsMemory().Slice(1, 2).Span[0] = 1;
                                                                public void Set() => _cells[0] = 2;
                                                                """, "_registry.Fill();", "_registry.Set();"), []),
        ["span-index-inside-the-window"] = (Registry(CELLS + """
                                                           public int Index;
                                                           public void Fill() => _cells.AsMemory(0, 2).Span[Index] = 1;
                                                           public void Set() => _cells[1] = 2;
                                                           """, "_registry.Fill();", "_registry.Set();"),
                                            ["DCA1001 unprotected _cells.[0..2]: Write Registry.Fill | Write Registry.Set"]),
        ["span-index-outside-the-window"] = (Registry(CELLS + """
                                                            public int Index;
                                                            public void Fill() => _cells.AsMemory(0, 2).Span[Index] = 1;
                                                            public void Set() => _cells[3] = 2;
                                                            """, "_registry.Fill();", "_registry.Set();"), []),
        ["span-read"] = (Registry(CELLS + """
                                        public int Get() => _cells.AsMemory(0, 2).Span[0];
                                        public void Set() => _cells[0] = 2;
                                        """, "_registry.Get();", "_registry.Set();"),
                         ["DCA1001 unprotected _cells.[0]: Read Registry.Get | Write Registry.Set"]),
        ["read-only-memory-constructor"] = (Registry(CELLS + """
                                                           public int Get() => new ReadOnlyMemory<int>(_cells, 1, 2).Span[0];
                                                           public void Set() => _cells[1] = 2;
                                                           """, "_registry.Get();", "_registry.Set();"),
                                            ["DCA1001 unprotected _cells.[1]: Read Registry.Get | Write Registry.Set"]),
        ["read-only-memory-from-a-memory"] = (Registry(CELLS + """
                                                             public int Get() { ReadOnlyMemory<int> window = _cells.AsMemory(1); return window.Span[0]; }
                                                             public void Set() => _cells[1] = 2;
                                                             """, "_registry.Get();", "_registry.Set();"),
                                              ["DCA1001 unprotected _cells.[1]: Read Registry.Get | Write Registry.Set"]),
        ["memory-constructor-over-the-array"] = (Registry(CELLS + """
                                                                public void Fill() => new Memory<int>(_cells).Span[0] = 1;
                                                                public void Set() => _cells[0] = 2;
                                                                """, "_registry.Fill();", "_registry.Set();"),
                                                 ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-constructor-window"] = (Registry(CELLS + """
                                                        public void Fill() => new Memory<int>(_cells, 1, 2).Span[0] = 1;
                                                        public void Set() => _cells[1] = 2;
                                                        """, "_registry.Fill();", "_registry.Set();"),
                                         ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-constructor-window-against-another-cell"] = (Registry(CELLS + """
                                                                             public void Fill() => new Memory<int>(_cells, 1, 2).Span[0] = 1;
                                                                             public void Set() => _cells[0] = 2;
                                                                             """, "_registry.Fill();", "_registry.Set();"), []),
        ["span-constructor-window"] = (Registry(CELLS + """
                                                      public void Fill() => new Span<int>(_cells, 1, 2)[0] = 1;
                                                      public void Set() => _cells[1] = 2;
                                                      """, "_registry.Fill();", "_registry.Set();"),
                                       ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["implicit-conversion-of-the-array"] = (Registry(CELLS + """
                                                               public void Fill() { Memory<int> window = _cells; window.Span[0] = 1; }
                                                               public void Set() => _cells[0] = 2;
                                                               """, "_registry.Fill();", "_registry.Set();"),
                                                ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["implicit-conversion-of-a-segment"] = (Registry(CELLS + """
                                                               public void Fill() { Memory<int> window = new ArraySegment<int>(_cells, 1, 2); window.Span[0] = 1; }
                                                               public void Set() => _cells[1] = 2;
                                                               """, "_registry.Fill();", "_registry.Set();"),
                                                ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-in-a-local"] = (Registry(CELLS + """
                                                public void Fill() { var window = _cells.AsMemory(0, 2); window.Span[0] = 1; }
                                                public void Set() => _cells[0] = 2;
                                                """, "_registry.Fill();", "_registry.Set();"),
                                 ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["span-of-a-memory-in-a-local"] = (Registry(CELLS + """
                                                          public void Fill() { var span = _cells.AsMemory(0, 2).Span; span[0] = 1; }
                                                          public void Set() => _cells[0] = 2;
                                                          """, "_registry.Fill();", "_registry.Set();"),
                                           ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-in-a-field"] = (Registry(CELLS + """
                                                private readonly Memory<int> _window;
                                                public Registry() => _window = _cells.AsMemory(0, 2);
                                                public void Fill() => /*@fill*/_window.Span[0] = 1;
                                                public void Set() => _cells[0] = 2;
                                                """, "_registry.Fill();", "_registry.Set();"),
                                 ["DCA1001 unprotected _cells.[0]: Write Registry.Fill @fill | Write Registry.Set"]),
        ["memory-handed-to-a-callee"] = (Registry(CELLS + """
                                                        public void Fill() => Write(_cells.AsMemory(1, 2));
                                                        private static void Write(Memory<int> window) => window.Span[0] = 1;
                                                        public void Set() => _cells[1] = 2;
                                                        """, "_registry.Fill();", "_registry.Set();"),
                                         ["DCA1001 unprotected _cells.[1]: Write Registry.Set | Write Registry.Write"]),
        ["memory-handed-to-a-callee-against-another-cell"] = (Registry(CELLS + """
                                                                             public void Fill() => Write(_cells.AsMemory(1, 2));
                                                                             private static void Write(Memory<int> window) => window.Span[0] = 1;
                                                                             public void Set() => _cells[0] = 2;
                                                                             """, "_registry.Fill();", "_registry.Set();"), []),
        ["string-as-memory"] = (Registry("""
                                         private string _text = "ab";
                                         public char Get() => _text.AsMemory().Span[0];
                                         public void Set() => _text = "cd";
                                         """, "_registry.Get();", "_registry.Set();"),
                                ["DCA1001 unprotected _text: Read Registry.Get | Write Registry.Set"]),
        ["slices-that-overlap"] = (Registry(CELLS + """
                                                  public void A() => _cells.AsMemory(0, 2).Span[1] = 1;
                                                  public void B() => _cells.AsMemory(1, 2).Span[0] = 2;
                                                  """, "_registry.A();", "_registry.B();"),
                                   ["DCA1001 unprotected _cells.[1]: Write Registry.A | Write Registry.B"]),
        ["slices-that-do-not-meet"] = (Registry(CELLS + """
                                                      public void A() => _cells.AsMemory(0, 2).Span[0] = 1;
                                                      public void B() => _cells.AsMemory(2, 2).Span[0] = 2;
                                                      """, "_registry.A();", "_registry.B();"), []),
        ["slice-start-from-a-parameter-against-an-index"] = (Registry(CELLS + """
                                                                            public void Fill(int start) => _cells.AsMemory(start, 2).Span[0] = 1;
                                                                            public void Set() => _cells[1] = 2;
                                                                            """, "_registry.Fill(1);", "_registry.Set();"),
                                                             ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["slice-start-from-a-parameter-against-another-cell"] = (Registry(CELLS + """
                                                                                public void Fill(int start) => _cells.AsMemory(start, 2).Span[0] = 1;
                                                                                public void Set() => _cells[3] = 2;
                                                                                """, "_registry.Fill(1);", "_registry.Set();"), []),
        ["slice-start-from-a-parameter-both-sides"] = (Registry(CELLS + """
                                                                      public void Fill(int start) => _cells.AsMemory(start, 2).Span[0] = 1;
                                                                      """, "_registry.Fill(1);", "_registry.Fill(1);"),
                                                       ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Fill"]),
        ["slice-of-a-window-from-a-parameter"] = (Registry(CELLS + """
                                                                 public void Fill(int start) => _cells.AsMemory(start).Slice(1).Span[0] = 1;
                                                                 """, "_registry.Fill(0);", "_registry.Fill(1);"), []),
        ["span-slices-from-a-parameter"] = (Registry(CELLS + """
                                                           public void Fill(int start) => _cells.AsSpan(start, 2)[0] = 1;
                                                           """, "_registry.Fill(0);", "_registry.Fill(2);"), []),
        ["slice-start-from-a-field"] = (Registry(CELLS + """
                                                       public int Start;
                                                       public void Fill() => _cells.AsMemory(Start).Span[0] = 1;
                                                       public void Set() => _cells[3] = 2;
                                                       """, "_registry.Fill();", "_registry.Set();"),
                                        ["DCA1001 unprotected _cells.[3]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-constructors-merged"] = (Registry(CELLS + """
                                                         public bool Choose;
                                                         public void Fill() { Memory<int> window = Choose ? new Memory<int>(_cells, 1, 2) : new Memory<int>(_cells, 2, 2); window.Span[0] = 1; }
                                                         public void Set() => _cells[1] = 2;
                                                         """, "_registry.Fill();", "_registry.Set();"),
                                          ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["conversion-and-as-memory-merged"] = (Registry(CELLS + """
                                                              public bool Choose;
                                                              public void Fill() { Memory<int> window = Choose ? (Memory<int>)_cells : _cells.AsMemory(1, 2); window.Span[0] = 1; }
                                                              public void Set() => _cells[1] = 2;
                                                              """, "_registry.Fill();", "_registry.Set();"),
                                               ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["span-slices-merged"] = (Registry(CELLS + """
                                                 public bool Choose;
                                                 public void Fill() { Span<int> window = Choose ? _cells.AsSpan(1, 2) : _cells.AsSpan(2, 2); window[0] = 1; }
                                                 public void Set() => _cells[1] = 2;
                                                 """, "_registry.Fill();", "_registry.Set();"),
                                  ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["span-slices-of-two-arrays-merged"] = (Registry(CELLS + """
                                                               private readonly int[] _other = new int[4];
                                                               public bool Choose;
                                                               public void Fill() { Span<int> window = Choose ? _cells.AsSpan() : _other.AsSpan(); window[0] = 1; }
                                                               public void Set() => _cells[0] = 2;
                                                               """, "_registry.Fill();", "_registry.Set();"),
                                                ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-constructors-of-two-arrays-merged"] = (Registry(CELLS + """
                                                                       private readonly int[] _other = new int[4];
                                                                       public bool Choose;
                                                                       public void Fill() { Memory<int> window = Choose ? new Memory<int>(_cells) : new Memory<int>(_other); window.Span[0] = 1; }
                                                                       public void Set() => _cells[0] = 2;
                                                                       """, "_registry.Fill();", "_registry.Set();"),
                                                        ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["window-index-moving-with-the-iteration"] = (Registry(CELLS + """
                                                                     public void Fill() => Parallel.For(0, 2, i => _cells.AsMemory(1 - i, 2).Span[i] = 1);
                                                                     """, "_registry.Fill();", "_ = 0;"),
                                                      ["DCA1001 unprotected _cells.[?]: Write Registry.Fill | Write Registry.Fill"]),
        ["window-index-of-the-iteration"] = (Registry(CELLS + """
                                                            public void Fill() => Parallel.For(0, 2, i => _cells.AsSpan(1, 2)[i] = 1);
                                                            """, "_registry.Fill();", "_ = 0;"), []),
        ["windows-at-two-starts-in-one-iteration"] = (Registry(CELLS + """
                                                                     public void Fill() => Parallel.For(0, 2, i => { _cells.AsMemory(0, 2).Span[i] = 1; _cells.AsMemory(1, 2).Span[i] = 2; });
                                                                     """, "_registry.Fill();", "_ = 0;"),
                                                      ["DCA1001 unprotected _cells.[0..2]: Write Registry.Fill | Write Registry.Fill"]),
        ["array-index-and-window-in-one-iteration"] = (Registry(CELLS + """
                                                                      public void Fill() => Parallel.For(0, 2, i => { _cells[i] = 1; _cells.AsSpan(1, 2)[i] = 2; });
                                                                      """, "_registry.Fill();", "_ = 0;"),
                                                       ["DCA1001 unprotected _cells.[1..3]: Write Registry.Fill | Write Registry.Fill"]),
        ["windows-of-two-lengths-in-the-iteration"] = (Registry(CELLS + """
                                                                      public bool Choose;
                                                                      public void Fill() => Parallel.For(0, 2, i => { Memory<int> window = Choose ? _cells.AsMemory(1, 2) : _cells.AsMemory(1, 3); window.Span[i] = 1; });
                                                                      """, "_registry.Fill();", "_ = 0;"), []),
        ["windows-of-two-lengths-merged"] = (Registry(CELLS + """
                                                            public bool Choose;
                                                            public void Fill() { Memory<int> window = Choose ? _cells.AsMemory(1, 2) : _cells.AsMemory(1, 3); window.Span[0] = 1; }
                                                            public void Set() => _cells[0] = 2;
                                                            """, "_registry.Fill();", "_registry.Set();"), []),
        ["window-re-sliced-in-a-loop"] = (Registry(CELLS + """
                                                         public void Fill(int count) { var window = _cells.AsMemory(); for (var i = 0; i < count; i++) window = window.Slice(1); window.Span[0] = 1; }
                                                         public void Set() => _cells[1] = 2;
                                                         """, "_registry.Fill(1);", "_registry.Set();"),
                                          ["DCA1001 unprotected _cells.[1]: Write Registry.Fill | Write Registry.Set"]),
        ["object-read-through-a-memory"] = (Registry(BOXES + """
                                                           public void Fill() { var box = _boxes.AsMemory().Span[0]; box.Value = 1; }
                                                           public void Set() => _boxes[0].Value = 2;
                                                           """, "_registry.Fill();", "_registry.Set();"),
                                            ["DCA1001 unprotected Value: Write Registry.Fill | Write Registry.Set"]),
        ["object-read-through-a-span"] = (Registry(BOXES + """
                                                         public void Fill() { var box = _boxes.AsSpan()[0]; box.Value = 1; }
                                                         public void Set() => _boxes[0].Value = 2;
                                                         """, "_registry.Fill();", "_registry.Set();"),
                                          ["DCA1001 unprotected Value: Write Registry.Fill | Write Registry.Set"]),
        ["object-read-through-a-memory-constructor"] = (Registry(BOXES + """
                                                                       public void Fill() => new Memory<Box>(_boxes).Span[0].Value = 1;
                                                                       public void Set() => _boxes[0].Value = 2;
                                                                       """, "_registry.Fill();", "_registry.Set();"),
                                                        ["DCA1001 unprotected Value: Write Registry.Fill | Write Registry.Set"]),
        ["window-cut-at-a-parameter-handed-to-a-callee"] = (Registry(CELLS + """
                                                                           public void Fill(int start) => Write(_cells.AsMemory(start, 2));
                                                                           private static void Write(Memory<int> window) => window.Span[0] = 1;
                                                                           public void Set() => _cells[3] = 2;
                                                                           """, "_registry.Fill(1);", "_registry.Set();"), []),
        ["span-cut-at-a-parameter-handed-to-a-callee"] = (Registry(CELLS + """
                                                                         public void Fill(int start) => Write(_cells.AsSpan(start, 2));
                                                                         private static void Write(Span<int> window) => window[0] = 1;
                                                                         public void Set() => _cells[3] = 2;
                                                                         """, "_registry.Fill(1);", "_registry.Set();"), []),
        ["memory-field-from-a-conversion"] = (Registry(CELLS + """
                                                             private readonly Memory<int> _window;
                                                             public Registry() => _window = _cells;
                                                             public void Fill() => _window.Span[0] = 1;
                                                             public void Set() => _cells[0] = 2;
                                                             """, "_registry.Fill();", "_registry.Set();"),
                                              ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-field-from-a-conversion-handed-to-a-callee"] = (Registry(CELLS + """
                                                                                private readonly Memory<int> _window;
                                                                                public Registry() => _window = _cells;
                                                                                public void Fill() => Write(_window);
                                                                                private static void Write(Memory<int> window) => window.Span[0] = 1;
                                                                                public void Set() => _cells[0] = 2;
                                                                                """, "_registry.Fill();", "_registry.Set();"),
                                                                 ["DCA1001 unprotected _cells.[0]: Write Registry.Set | Write Registry.Write"]),
        ["memory-over-an-array-a-method-returns"] = (Registry(CELLS + """
                                                                    private int[] Cells() => _cells;
                                                                    public void Fill() { Memory<int> window = Cells(); window.Span[0] = 1; }
                                                                    public void Set() => _cells[0] = 2;
                                                                    """, "_registry.Fill();", "_registry.Set();"),
                                                     ["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"]),
        ["memory-over-an-array-parameter"] = (Registry(CELLS + """
                                                             public void Fill() => Write(_cells);
                                                             private static void Write(int[] cells) { Memory<int> window = cells; window.Span[0] = 1; }
                                                             public void Set() => _cells[0] = 2;
                                                             """, "_registry.Fill();", "_registry.Set();"),
                                              ["DCA1001 unprotected _cells.[0]: Write Registry.Set | Write Registry.Write"])
    };

    /// <summary>Why a window cut at an <c>Index</c> from the end or at a <c>Range</c> meets every cell of the array.</summary>
    private const string INDEX_OFFSET_UNKNOWN =
        "issue 543: MethodSummaryBuilder.Window reads the start of AsMemory and AsSpan as a number only where it is an int: an Index " +
        "from the end (^n, an IrComputeOperation Hat whose meaning needs the array's length) and a Range (an unsupported operation, " +
        "issue 446) leave the offset unknown, so the cell widens to every cell of the array and pairs with a cell run time keeps apart";

    /// <summary>Why a window an <c>ArraySegment&lt;T&gt;</c> converts to reads as an unknown effect.</summary>
    private const string SEGMENT_UNMODELLED =
        "issue 544: ArraySegment<T> is none of the SpanTypes, so its constructor is an unresolved call whose unknown effect reaches " +
        "the array it is handed, every cell and the structure, and the window Slice unwinds stops at the segment, which no field holds";

    /// <summary>Why a write through the span of a memory kept in a field misses the array's cell.</summary>
    private const string MEMORY_IN_A_FIELD =
        "issue 545: MethodSummaryBuilder.Cell and Collection name the cell after the field the window was read from, _window.[0], " +
        "although its cells are those of the array it was cut from: where the heap knows the field holds the array (a conversion " +
        "of the array) the pair with _cells is found under that name, and where it does not (the memory AsMemory makes points to " +
        "no region, issue 552) no access to _cells meets it";

    /// <summary>Why a window a <c>Memory&lt;T&gt;</c> constructor makes keeps an unknown effect on the whole array beside its cell.</summary>
    private const string MEMORY_CONSTRUCTOR_UNRESOLVED =
        "issue 552: IrLowering.IsRecognized leaves the Memory and ReadOnlyMemory constructors unresolved calls, whose unknown effect " +
        "reaches the structure and every cell of the array they are handed, beside the cell MethodSummaryBuilder.Slice names: the " +
        "window is an allocation of its own to the heap, and where windows over two arrays are merged that effect is all that " +
        "reaches them";

    /// <summary>Why two iterations of a window that starts past the first cell pair with each other.</summary>
    private const string ITERATION_SHIFT =
        "issue 554: the shortcut that skips a pair of two iterations (InterproceduralAccesses.IsDisjointIteration) compares the " +
        "iteration index alone, so MethodSummaryBuilder.IndexParameter gives it only an index that is the cell itself, in a window " +
        "that starts at the first cell; a window cut at a start past it, whose cell is the index plus that start in every iteration, " +
        "has no shortcut, and its two iterations pair";

    /// <summary>Why a write through windows over two arrays merged into one makes no access.</summary>
    private const string WINDOWS_OF_TWO_ARRAYS =
        "issue 552: MethodSummaryBuilder.Merged makes a merge one collection only where every value it joins is a load of one field " +
        "of one object, so the write is a standing element on what the merge points to, and the window AsSpan cuts points to no " +
        "region in the heap";

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why, listed by
    /// hand.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["as-memory-index-from-the-end-against-another-cell"] = new(["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"],
                                                                    INDEX_OFFSET_UNKNOWN),
        ["as-memory-range-against-another-cell"] = new(["DCA1001 unprotected _cells.[0]: Write Registry.Fill | Write Registry.Set"], INDEX_OFFSET_UNKNOWN),
        ["implicit-conversion-of-a-segment"] = new(["DCA1001 unprotected _cells.[1]: UnknownEffect Registry.Fill | Write Registry.Set"], SEGMENT_UNMODELLED),
        ["memory-in-a-field"] = new([], MEMORY_IN_A_FIELD),
        ["memory-field-from-a-conversion"] = new(["DCA1001 unprotected _window.[0]: Write Registry.Fill | Write Registry.Set"], MEMORY_IN_A_FIELD),
        ["memory-field-from-a-conversion-handed-to-a-callee"] = new(["DCA1001 unprotected _window.[0]: Write Registry.Set | Write Registry.Write"],
                                                                    MEMORY_IN_A_FIELD),
        ["memory-constructor-over-the-array"] = new([.. Probes["memory-constructor-over-the-array"].Truth,
                                                     "DCA1001 unprotected _cells.[0]: UnknownEffect Registry.Fill | Write Registry.Set"],
                                                    MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["memory-constructor-window"] = new([.. Probes["memory-constructor-window"].Truth,
                                             "DCA1001 unprotected _cells.[1]: UnknownEffect Registry.Fill | Write Registry.Set"],
                                            MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["memory-constructor-window-against-another-cell"] = new(["DCA1001 unprotected _cells.[0]: UnknownEffect Registry.Fill | Write Registry.Set"],
                                                                 MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["read-only-memory-constructor"] = new([.. Probes["read-only-memory-constructor"].Truth,
                                                "DCA1001 unprotected _cells.[1]: UnknownEffect Registry.Get | Write Registry.Set"],
                                               MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["memory-constructors-merged"] = new([.. Probes["memory-constructors-merged"].Truth,
                                              "DCA1001 unprotected _cells.[1]: UnknownEffect Registry.Fill | Write Registry.Set"],
                                             MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["memory-constructors-of-two-arrays-merged"] = new(["DCA1001 unprotected _cells.[0]: UnknownEffect Registry.Fill | Write Registry.Set"],
                                                           MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["object-read-through-a-memory-constructor"] = new([.. Probes["object-read-through-a-memory-constructor"].Truth,
                                                            "DCA1001 unprotected Value: UnknownEffect Registry.Fill | Write Registry.Set",
                                                            "DCA1001 unprotected _boxes.[0]: Read Registry.Set | UnknownEffect Registry.Fill"],
                                                           MEMORY_CONSTRUCTOR_UNRESOLVED),
        ["span-slices-of-two-arrays-merged"] = new([], WINDOWS_OF_TWO_ARRAYS),
        ["window-index-of-the-iteration"] = new(["DCA1001 unprotected _cells.[1..3]: Write Registry.Fill | Write Registry.Fill"], ITERATION_SHIFT),
        ["windows-at-two-starts-in-one-iteration"] = new([.. Probes["windows-at-two-starts-in-one-iteration"].Truth,
                                                           "DCA1001 unprotected _cells.[1..3]: Write Registry.Fill | Write Registry.Fill"], ITERATION_SHIFT),
        ["windows-of-two-lengths-in-the-iteration"] = new(["DCA1001 unprotected _cells.[?]: Write Registry.Fill | Write Registry.Fill"], ITERATION_SHIFT)
    };

    /// <summary>What a probe's marked site collected, exactly: where the claim is that a write misses the array, the write the
    /// marked site holds is on the field the window was read from.</summary>
    private static readonly Dictionary<string, (string Marker, string[] Accesses)> Sites = new(StringComparer.Ordinal)
    {
        ["memory-in-a-field"] = ("@fill", ["Read _window Registry.Fill @fill", "Write _window.[0] Registry.Fill @fill"])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (probe, truth) = Probes[name];

        var findings = await probe.FindingsAsync();

        HostedServiceProbe.AssertAnswer(findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why; the differences number
    /// what the record counts, reason by reason.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth()
    {
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
        Assert.Equal(55, Probes.Count);
        Assert.Equal(2, Differences.Count(difference => difference.Value.Reason == INDEX_OFFSET_UNKNOWN));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == SEGMENT_UNMODELLED));
        Assert.Equal(3, Differences.Count(difference => difference.Value.Reason == MEMORY_IN_A_FIELD));
        Assert.Equal(7, Differences.Count(difference => difference.Value.Reason == MEMORY_CONSTRUCTOR_UNRESOLVED));
        Assert.Equal(1, Differences.Count(difference => difference.Value.Reason == WINDOWS_OF_TWO_ARRAYS));
        Assert.Equal(3, Differences.Count(difference => difference.Value.Reason == ITERATION_SHIFT));
        Assert.Equal(17, Differences.Count);
    }

    /// <summary>The names of the probes whose marked site the record describes access by access.</summary>
    public static TheoryData<string> SiteNames() => new(Sites.Keys);

    /// <summary>A probe's marked site collected exactly the accesses the record names.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task Marked_site_collected_what_the_record_names(string name)
    {
        var (marker, accesses) = Sites[name];

        var answer = await Probes[name].Probe.AnalyzeAsync(new ProbeInputs());

        Assert.Equal(accesses.Order(StringComparer.Ordinal), answer.Accesses.Where(access => access.EndsWith(" " + marker, StringComparison.Ordinal)));
    }

    /// <summary>Two windows a caller cuts at different places from a parameter are bound to their cells where each call stands, and
    /// so is a window handed to a callee, so their empty answer is a proof of disjoint cells, for a memory and for a span alike.</summary>
    /// <param name="name">The probe's name.</param>
    /// <param name="cells">The cells the two calls write.</param>
    [Theory]
    [InlineData("slice-of-a-window-from-a-parameter", new[] { "Write _cells.[1] Registry.Fill", "Write _cells.[2] Registry.Fill" })]
    [InlineData("span-slices-from-a-parameter", new[] { "Write _cells.[0] Registry.Fill", "Write _cells.[2] Registry.Fill" })]
    [InlineData("window-cut-at-a-parameter-handed-to-a-callee", new[] { "Write _cells.[1] Registry.Write", "Write _cells.[3] Registry.Set" })]
    [InlineData("span-cut-at-a-parameter-handed-to-a-callee", new[] { "Write _cells.[1] Registry.Write", "Write _cells.[3] Registry.Set" })]
    public async Task Windows_cut_at_a_parameter_are_collected_on_their_cells(string name, string[] cells) =>
        HostedServiceProbe.AssertAccessClaims(await Probes[name].Probe.AnalyzeAsync(new ProbeInputs()), cells, []);

    /// <summary>The characters a string's read-only window reads are read, never written.</summary>
    [Fact]
    public async Task A_string_window_is_only_read()
    {
        var answer = await Probes["string-as-memory"].Probe.AnalyzeAsync(new ProbeInputs());

        Assert.Contains("Read _text.[0] Registry.Get", answer.Accesses);
        Assert.DoesNotContain(answer.Accesses, access => access.StartsWith("Write _text.[", StringComparison.Ordinal));
    }

    /// <summary>A probe of a <c>Registry</c> with the given members, its two hosted services running one statement each.</summary>
    /// <param name="members">The members of <c>Registry</c>.</param>
    /// <param name="first">The statement the first hosted service runs.</param>
    /// <param name="second">The statement the second hosted service runs.</param>
    private static HostedServiceProbe Registry(string members, string first, string second) => new("Registry", $$"""
        public sealed class Registry
        {
            {{members}}
        }
        """, first, second);
}
