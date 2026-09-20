using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PlanForge.Diagnostics;
using PlanForge.Vendors;

namespace PlanForge.Infrastructure;

internal sealed record ProcessSpec(string FileName,
                                   IReadOnlyList<string> Arguments,
                                   string? WorkingDirectory,
                                   string StandardInput,
                                   IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// How much of a process's stdout reaches the consumer, and what happens to the rest. A vendor turn
/// that outgrows the first bound is trimmed rather than killed: the head is streamed as it arrives,
/// the tail is held back and delivered at the end behind an elision marker, and only a runaway far
/// past either is still fatal.
/// </summary>
/// <remarks>
/// The killing cap is what run 20260919-160244-77fd30 paid twice, both times on `forge.review.fix`
/// with claude: the builder had written 92 files and the turn died on the way to reporting them, so
/// the work was on disk and the summary, verification, file list and gate run were not. The output
/// was ordinary — `dotnet test -v n` prints the whole csc command line once per run, and a fix pass
/// is a long row of mutation checks — which is the point: 8 MB is a size a working turn reaches, so
/// it cannot be the size at which a turn is destroyed. What the tail buffer protects is exactly the
/// part that was lost: the structured result is the last thing a vendor writes.
/// </remarks>
/// <param name="ElideAfterBytes">Where the head ends and the stream starts being held back.</param>
/// <param name="TailChars">How much of the end is kept and delivered after the marker. Whole lines
/// only, and never fewer than one: a cut line is not JSON, and the line this exists to save is the
/// vendor's own result.</param>
/// <param name="HardCapBytes">The size at which the process is still killed. Far past the other
/// two, because this is no longer a large turn but a process that will not stop.</param>
internal sealed record OutputBounds(long ElideAfterBytes, int TailChars, long HardCapBytes)
{
    public static readonly OutputBounds Default = new(8 * 1024 * 1024, 512 * 1024, 512L * 1024 * 1024);
}

/// <summary>
/// Bounded runner for vendor processes: output bounds, timeout, kill-tree. Unlike the old
/// ProcessExecution it hands back stdout line by line, because vendors emit JSONL as they work.
/// </summary>
internal static class StreamingProcess
{
    private const string SOURCE = "process";
    private static readonly TimeSpan _workerIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How much of stdout is held back for a failure report. The bound <see cref="RunLog"/> gives a
    /// field, so a tail reaches the log and the exception message without being cut twice.
    /// </summary>
    private const int STDOUT_TAIL_CHARS = 2000;

    // No BOM: a byte-order mark on stdin is a stray character at the head of the prompt.
    private static readonly UTF8Encoding UTF8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// How long a pipe is still read once the process behind it is gone. The bound is wanted for an
    /// inherited handle holding the pipe open, since everything the process wrote is in the buffer
    /// by the time it exits — but it lands on this machine getting round to reading that buffer
    /// just the same, and the number is squeezed from both sides.
    /// </summary>
    /// <remarks>
    /// Too short drops output that was already written: at the two seconds this used to be, CI run
    /// 33916817192 had `git rev-parse HEAD` exit 0 with its one line never delivered, so a baseline
    /// was captured with an empty head. Too long rebuilds the wait a spawned server imposes — the
    /// twenty-minute timeout over a critique delivered in two that ending the stream on the exit
    /// was written to fix, and which <c>DiagnosticLogTests</c> holds to ten seconds. Five sits
    /// between: several times any ordinary scheduling delay, and inside that guard. What makes the
    /// choice survivable rather than lucky is that <see cref="NextLineAsync"/> logs the expiry, so
    /// the next machine slow enough to lose a line says so instead of returning a short stream.
    /// </remarks>
    private static readonly TimeSpan _exitDrain = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The same window for stderr, and deliberately the short one it always was. What expires here
    /// costs a tail in the log; what expires on stdout costs the answer itself, and the two are
    /// worth waiting for in different measure — a child holding both pipes would otherwise add the
    /// stdout window to the end of every process that leaves one behind.
    /// </summary>
    private static readonly TimeSpan _stderrDrain = TimeSpan.FromSeconds(2);

    /// <summary>Runs a non-worker process under a fixed wall-clock timeout.</summary>
    /// <param name="spec">The executable, arguments, working directory, stdin and environment.</param>
    /// <param name="timeout">The maximum wall-clock duration of the process.</param>
    /// <param name="ct">Cancels the process on behalf of the caller.</param>
    /// <param name="exitDrain">Overrides <see cref="_exitDrain"/> for this call, which is how the
    /// drain is tested: a per-call argument rather than a settable static, because this suite runs
    /// processes in parallel and a narrowed window is exactly what drops another test's output.</param>
    /// <param name="bounds">Overrides <see cref="OutputBounds.Default"/> for this call, which is how
    /// the bounds are tested: a call that had to write half a gigabyte to reach the hard cap would
    /// be a test of this machine's disk rather than of the bound.</param>
    public static IAsyncEnumerable<string> RunAsync(ProcessSpec spec,
                                                    TimeSpan timeout,
                                                    CancellationToken ct,
                                                    TimeSpan? exitDrain = null,
                                                    OutputBounds? bounds = null) =>
        RunCoreAsync(spec, timeout, null, ct, exitDrain, bounds);

    public static IAsyncEnumerable<string> RunWorkerAsync(ProcessSpec spec, CancellationToken ct) =>
        RunWorkerAsync(spec, _workerIdleTimeout, ct);

    internal static IAsyncEnumerable<string> RunWorkerAsync(ProcessSpec spec,
                                                            TimeSpan idleTimeout,
                                                            CancellationToken ct,
                                                            OutputBounds? bounds = null) =>
        RunCoreAsync(spec, null, idleTimeout, ct, null, bounds);

    private static async IAsyncEnumerable<string> RunCoreAsync(ProcessSpec spec,
                                                               TimeSpan? timeout,
                                                               TimeSpan? idleTimeout,
                                                               [EnumeratorCancellation] CancellationToken ct,
                                                               TimeSpan? exitDrain,
                                                               OutputBounds? bounds)
    {
        var limits = bounds ?? OutputBounds.Default;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } wallClockTimeout) deadline.CancelAfter(wallClockTimeout);
        var token = deadline.Token;
        var idle = new IdleExpiration();

        using var process = new Process();
        process.StartInfo = Build(spec);

        // The launch record is the single most useful line in the log: an argument list nobody can
        // see is how a model id the vendor rejects reads as an unexplained timeout.
        var log = RunLog.Current;
        log?.Write("info", SOURCE, "process.start",
            ("exec", spec.FileName),
            ("args", string.Join(' ', spec.Arguments)),
            ("cwd", spec.WorkingDirectory),
            ("timeout", timeout?.ToString()),
            ("idleTimeout", idleTimeout?.ToString()));

        if (!process.Start())
        {
            log?.Write("error", SOURCE, "process.start.failed", ("exec", spec.FileName));
            throw new VendorException($"could not start {spec.FileName}");
        }

        log?.Write("info", SOURCE, "process.started", ("exec", spec.FileName), ("pid", Pid(process)));

        var stderr = process.StandardError.ReadToEndAsync(token);
        var exited = process.WaitForExitAsync(token);

        var seen = 0L;
        var capped = false;
        var stdout = new OutputTail();
        var elision = new Elision(limits.TailChars);
        try
        {
            // Inside the try because a vendor that never drains its stdin blocks this write, and
            // blocking outside it would leave the process alive and the log with nothing but a
            // launch line to explain the wait.
            await WithIdleTimeoutAsync(process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), token),
                                       idleTimeout, idle, spec.FileName, token).ConfigureAwait(false);
            process.StandardInput.Close();

            while (await NextLineAsync(process.StandardOutput, exited, spec.FileName,
                                       exitDrain ?? _exitDrain, idleTimeout, idle, token).ConfigureAwait(false) is { } line)
            {
                idle.RecordOutput();
                WorkerActivity.RecordOutput();
                seen += line.Length;
                stdout.Add(line);

                if (seen > limits.HardCapBytes)
                {
                    capped = true;
                    throw new VendorException($"{spec.FileName} exceeded {limits.HardCapBytes} bytes of output");
                }

                if (seen > limits.ElideAfterBytes)
                {
                    if (elision.Begin())
                    {
                        log?.Write("warn", SOURCE, "process.output.eliding",
                            ("exec", spec.FileName),
                            ("after", limits.ElideAfterBytes.ToString()),
                            ("tail", limits.TailChars.ToString()));
                    }

                    elision.Add(line);
                    continue;
                }

                yield return line;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                // Which cancellation fired decides how the failure reads: the caller walking away
                // is not the same event as the vendor outstaying its timeout. This finally also
                // runs when the consumer of the stream faults or stops early — the iterator is
                // disposed with the process still alive — and only the flag separates that from a
                // genuine cap breach, so a consumer's own exception is not billed to the vendor.
                var reason = ct.IsCancellationRequested ? "cancelled"
                    : idle.Expired ? "idle"
                    : deadline.IsCancellationRequested ? "timeout"
                    : capped ? "output-cap" : "abandoned";

                // Kill before draining. On the output-cap path nothing has cancelled the stderr
                // read, so a live process would keep it open and the drain would spend its whole
                // bound waiting on the very process we came here to end.
                var pid = Pid(process);
                try
                {
                    process.Kill(entireProcessTree: true);
                } 
                catch (InvalidOperationException) { }

                var killed = await DrainAsync(stderr).ConfigureAwait(false);
                log?.Write("warn", SOURCE, "process.kill",
                    ("exec", spec.FileName),
                    ("pid", pid),
                    ("reason", reason),
                    ("stderrTail", killed.Length == 0 ? null : RunLog.Tail(killed)),
                    ("stdoutTail", stdout.Tail()));
            }
        }

        // After the finally, so nothing is delivered for a stream that ended in a kill, and before
        // the exit code is judged, because the held-back end of stdout is the part the caller came
        // for. A stream that stayed inside its head yields nothing here.
        foreach (var held in elision.Drain())
        {
            yield return held;
        }

        await exited.ConfigureAwait(false);

        var error = await DrainAsync(stderr).ConfigureAwait(false);
        log?.Write(process.ExitCode == 0 ? "info" : "error", SOURCE, "process.exit",
            ("exec", spec.FileName),
            ("pid", Pid(process)),
            ("exitCode", process.ExitCode.ToString()),
            ("stderrTail", error.Length == 0 ? null : RunLog.Tail(error)),
            ("stdoutTail", process.ExitCode == 0 ? null : stdout.Tail()));

        if (process.ExitCode != 0)
            throw new VendorException(Failure(spec.FileName, process.ExitCode, error, stdout), process.ExitCode);
    }

    public static async Task<IReadOnlyList<string>> CollectAsync(ProcessSpec spec,
                                                                 TimeSpan timeout,
                                                                 CancellationToken ct,
                                                                 TimeSpan? exitDrain = null,
                                                                 OutputBounds? bounds = null)
    {
        var lines = new List<string>();
        await foreach (var line in RunAsync(spec, timeout, ct, exitDrain, bounds).ConfigureAwait(false))
        {
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// The next line, with the process's own exit — not EOF on the pipe — as the end of the stream.
    /// EOF is not the vendor's alone to give: a server it spawns inherits the handle and can hold
    /// the pipe open long after the vendor is gone, and a run that waited for it read a critique
    /// delivered in two minutes as a twenty-minute timeout. Once the process has exited, only what
    /// it already wrote can still arrive, so a bounded drain finishes the stream.
    /// </summary>
    /// <remarks>
    /// An expired drain is a truncation: what had not been read is dropped, and the caller is
    /// handed a short stream that looks exactly like a complete one. That is how an empty
    /// `git rev-parse HEAD` reached a baseline as an answer, so the expiry is logged. On the
    /// handle-holding path it is the ordinary end of a stream and nothing was lost; on a starved
    /// machine it is the line to grep for.
    /// </remarks>
    /// <param name="stdout">The process's redirected standard output.</param>
    /// <param name="exited">Completes when the process exits.</param>
    /// <param name="exec">The executable name used in diagnostics.</param>
    /// <param name="drain">How long to drain stdout after the process exits.</param>
    /// <param name="idleTimeout">The worker's maximum silence, or <see langword="null"/> for none.</param>
    /// <param name="idle">Tracks the worker's last stdout line and whether its idle window expired.</param>
    /// <param name="ct">Cancels the read on behalf of the caller or wall-clock deadline.</param>
    private static async Task<string?> NextLineAsync(StreamReader stdout,
                                                     Task exited,
                                                     string exec,
                                                     TimeSpan drain,
                                                     TimeSpan? idleTimeout,
                                                     IdleExpiration idle,
                                                     CancellationToken ct)
    {
        var read = stdout.ReadLineAsync(ct).AsTask();
        using var idleDeadline = idleTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        var idleWait = idleTimeout is { } silence
            ? Task.Delay(idle.Remaining(silence), idleDeadline!.Token)
            : null;
        Task completed;
        try
        {
            completed = idleWait is null
                ? await Task.WhenAny(read, exited).ConfigureAwait(false)
                : await Task.WhenAny(read, exited, idleWait).ConfigureAwait(false);
        }
        finally
        {
            idleDeadline?.Cancel();
        }

        if (completed == read)
            return await read.ConfigureAwait(false);

        if (completed == idleWait)
        {
            await completed.ConfigureAwait(false);
            idle.Expire();
            Observe(read);
            throw IdleFailure(exec, idleTimeout!.Value);
        }

        try
        {
            return await read.WaitAsync(drain, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RunLog.Current?.Write("warn", SOURCE, "process.drain.timeout",
                ("exec", exec),
                ("drain", drain.ToString()));

            // Nothing more is coming, and nothing may read this stream again: a StreamReader
            // refuses a second read while one is pending. Observing the abandoned one keeps it from
            // resurfacing as an unobserved task exception.
            _ = read.ContinueWith(static abandoned => _ = abandoned.Exception, TaskScheduler.Default);
            return null;
        }
    }

    private static async Task WithIdleTimeoutAsync(Task operation,
                                                   TimeSpan? idleTimeout,
                                                   IdleExpiration idle,
                                                   string exec,
                                                   CancellationToken ct)
    {
        try
        {
            if (idleTimeout is { } silence)
                await operation.WaitAsync(idle.Remaining(silence), ct).ConfigureAwait(false);
            else
                await operation.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            idle.Expire();
            Observe(operation);
            throw IdleFailure(exec, idleTimeout!.Value);
        }
    }

    private static VendorException IdleFailure(string exec, TimeSpan idleTimeout) =>
        new($"{exec} produced no stdout for {idleTimeout.TotalMinutes:0.##} minutes");

    private static void Observe(Task abandoned) =>
        _ = abandoned.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

    /// <summary>
    /// Reads stderr without letting the read decide the outcome. On a kill the stream is cancelled
    /// rather than closed, and the tail we wanted is the reason we were killing — losing it to the
    /// same cancellation would leave the log saying only that something stopped. The bound is the
    /// same kind <see cref="NextLineAsync"/> applies to stdout, and for the same reason: an
    /// inherited handle can outlive the process whose output we came for. Losing a stderr tail
    /// costs a log line rather than an answer, so this bound is the shorter one and its expiry
    /// stays silent.
    /// </summary>
    /// <param name="stderr">The pending read of the process's redirected standard error.</param>
    private static async Task<string> DrainAsync(Task<string> stderr)
    {
        try
        {
            return await stderr.WaitAsync(_stderrDrain).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Why the process failed, in the words it left behind. A CLI that says its piece on stdout and
    /// exits with nothing on stderr used to reach the caller as <c>claude.exe exited 1: </c> — an
    /// exit code and an empty colon. That is the shape an individual spend limit takes, and in run
    /// 20260905-144900-e42174 it cost a reproduction of the vendor call by hand to learn what the
    /// CLI had already said.
    /// </summary>
    /// <remarks>
    /// The stdout tail is added only where stderr is blank. Where stderr speaks it is the better
    /// account of a failure, and <see cref="Acts.GateRunner"/> keeps a copy of stdout of its own
    /// that the tail would only repeat. Both tails are cut: what a vendor writes while failing is
    /// not smaller than what it writes while working, and neither belongs in an exception message
    /// whole.
    /// </remarks>
    /// <param name="exec">The executable name used in the failure message.</param>
    /// <param name="exitCode">The process's non-zero exit code.</param>
    /// <param name="stderr">The captured standard error.</param>
    /// <param name="stdout">The bounded tail of standard output.</param>
    private static string Failure(string exec, int exitCode, string stderr, OutputTail stdout)
    {
        if (!string.IsNullOrWhiteSpace(stderr)) return $"{exec} exited {exitCode}: {RunLog.Tail(stderr)}";

        return stdout.Tail() is { } tail
            ? $"{exec} exited {exitCode} with nothing on stderr; last of stdout: {tail}"
            : $"{exec} exited {exitCode} with no output";
    }

    // The process may already be gone by the time we ask, and an unusable pid is not worth a throw.
    private static string? Pid(Process process)
    {
        try
        {
            return process.Id.ToString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static ProcessStartInfo Build(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Every vendor CLI here is a Node process and writes UTF-8 both ways. Left unset these
            // follow the console code page, and a server started by an MCP host has no console to
            // speak of: run 20260902-224201-7bf03b decoded vendor output as CP437, so every em
            // dash reached the run log, the critic's findings and the builder's evidence as three
            // characters of mojibake. ASCII survives that; a plan or a finding written in anything
            // else does not.
            StandardOutputEncoding = UTF8,
            StandardErrorEncoding = UTF8,
            StandardInputEncoding = UTF8
        };

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
            info.WorkingDirectory = spec.WorkingDirectory;

        if (spec.Environment is not null)
        {
            foreach (var pair in spec.Environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    /// The last of stdout, kept for a failure nobody can ask the process to explain a second time.
    /// Bounded in characters rather than in lines, because one line of a vendor's JSONL can be the
    /// size of a file; an over-long line is kept by its own tail, since the end of the output is
    /// the whole point of holding any of it.
    /// </summary>
    private sealed class OutputTail
    {
        private readonly Queue<string> _lines = new();
        private int _held;

        public void Add(string line)
        {
            var kept = line.Length <= STDOUT_TAIL_CHARS ? line : line[^STDOUT_TAIL_CHARS..];
            _lines.Enqueue(kept);
            _held += kept.Length;

            // Never below one line: whatever is left is already inside the budget, and dropping it
            // would leave a failure with nothing at all to show.
            while (_held > STDOUT_TAIL_CHARS && _lines.Count > 1)
            {
                _held -= _lines.Dequeue().Length;
            }
        }

        /// <summary>The tail as one block, or <see langword="null"/> when the process wrote nothing.</summary>
        public string? Tail()
        {
            var text = string.Join('\n', _lines).TrimEnd();
            return text.Length == 0 ? null : RunLog.Tail(text);
        }
    }

    /// <summary>
    /// The end of an over-long stream, held back while the middle is dropped. Whole lines only: the
    /// consumer parses each one as JSON and half a line is not a message. The queue never empties
    /// below one entry, so a single line larger than the whole budget still arrives — that line is
    /// usually the vendor's result, which is the reason any of this is kept.
    /// </summary>
    /// <param name="tailChars">How much of the end to hold.</param>
    private sealed class Elision(int tailChars)
    {
        private readonly Queue<string> _tail = new();
        private bool _started;
        private long _dropped;
        private int _held;

        /// <summary>Marks elision as under way; true the first time, so the log says it once.</summary>
        public bool Begin()
        {
            var first = !_started;
            _started = true;
            return first;
        }

        public void Add(string line)
        {
            _tail.Enqueue(line);
            _held += line.Length;

            while (_held > tailChars && _tail.Count > 1)
            {
                var gone = _tail.Dequeue();
                _held -= gone.Length;
                _dropped += gone.Length;
            }
        }

        /// <summary>The marker and the tail, in order, or nothing when the stream stayed inside its head.</summary>
        public IEnumerable<string> Drain()
        {
            if (!_started) yield break;

            // A marker the consumer cannot mistake for a message: it does not parse as JSON, and
            // every vendor session already logs and skips a line that does not.
            yield return $"[... {_dropped} bytes elided ...]";

            while (_tail.Count > 0)
            {
                yield return _tail.Dequeue();
            }
        }
    }

    private sealed class IdleExpiration
    {
        private long _lastOutput = Stopwatch.GetTimestamp();

        public bool Expired { get; private set; }

        public void RecordOutput() => _lastOutput = Stopwatch.GetTimestamp();

        public TimeSpan Remaining(TimeSpan timeout)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(_lastOutput);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public void Expire() => Expired = true;
    }
}
