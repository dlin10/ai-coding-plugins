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
/// Bounded runner for vendor processes: output cap, timeout, kill-tree. Unlike the old
/// ProcessExecution it hands back stdout line by line, because vendors emit JSONL as they work.
/// </summary>
internal static class StreamingProcess
{
    private const int MAX_OUTPUT_BYTES = 8 * 1024 * 1024;
    private const string SOURCE = "process";

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

    /// <param name="exitDrain">
    /// Overrides <see cref="_exitDrain"/> for this call, which is how the drain is tested: a
    /// per-call argument rather than a settable static, because this suite runs processes in
    /// parallel and a narrowed window is exactly what drops another test's output.
    /// </param>
    public static async IAsyncEnumerable<string> RunAsync(ProcessSpec spec,
                                                          TimeSpan timeout,
                                                          [EnumeratorCancellation] CancellationToken ct,
                                                          TimeSpan? exitDrain = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;

        using var process = new Process();
        process.StartInfo = Build(spec);

        // The launch record is the single most useful line in the log: an argument list nobody can
        // see is how a model id the vendor rejects reads as an unexplained timeout.
        var log = RunLog.Current;
        log?.Write("info", SOURCE, "process.start",
            ("exec", spec.FileName),
            ("args", string.Join(' ', spec.Arguments)),
            ("cwd", spec.WorkingDirectory),
            ("timeout", timeout.ToString()));

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
        try
        {
            // Inside the try because a vendor that never drains its stdin blocks this write, and
            // blocking outside it would leave the process alive and the log with nothing but a
            // launch line to explain the wait.
            await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();

            while (await NextLineAsync(process.StandardOutput, exited, spec.FileName,
                                       exitDrain ?? _exitDrain, token).ConfigureAwait(false) is { } line)
            {
                seen += line.Length;
                if (seen > MAX_OUTPUT_BYTES)
                {
                    capped = true;
                    throw new VendorException($"{spec.FileName} exceeded {MAX_OUTPUT_BYTES} bytes of output");
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
                    ("stderrTail", killed.Length == 0 ? null : RunLog.Tail(killed)));
            }
        }

        await exited.ConfigureAwait(false);

        var error = await DrainAsync(stderr).ConfigureAwait(false);
        log?.Write(process.ExitCode == 0 ? "info" : "error", SOURCE, "process.exit",
            ("exec", spec.FileName),
            ("pid", Pid(process)),
            ("exitCode", process.ExitCode.ToString()),
            ("stderrTail", error.Length == 0 ? null : RunLog.Tail(error)));

        if (process.ExitCode != 0)
            throw new VendorException($"{spec.FileName} exited {process.ExitCode}: {error}", process.ExitCode);
    }

    public static async Task<IReadOnlyList<string>> CollectAsync(ProcessSpec spec,
                                                                 TimeSpan timeout,
                                                                 CancellationToken ct,
                                                                 TimeSpan? exitDrain = null)
    {
        var lines = new List<string>();
        await foreach (var line in RunAsync(spec, timeout, ct, exitDrain).ConfigureAwait(false))
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
    private static async Task<string?> NextLineAsync(StreamReader stdout,
                                                     Task exited,
                                                     string exec,
                                                     TimeSpan drain,
                                                     CancellationToken ct)
    {
        var read = stdout.ReadLineAsync(ct).AsTask();
        if (await Task.WhenAny(read, exited).ConfigureAwait(false) == read)
            return await read.ConfigureAwait(false);

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

    /// <summary>
    /// Reads stderr without letting the read decide the outcome. On a kill the stream is cancelled
    /// rather than closed, and the tail we wanted is the reason we were killing — losing it to the
    /// same cancellation would leave the log saying only that something stopped. The bound is the
    /// same kind <see cref="NextLineAsync"/> applies to stdout, and for the same reason: an
    /// inherited handle can outlive the process whose output we came for. Losing a stderr tail
    /// costs a log line rather than an answer, so this bound is the shorter one and its expiry
    /// stays silent.
    /// </summary>
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
}
