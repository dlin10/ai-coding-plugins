using System.Globalization;
using PlanForge.Orchestration;

namespace PlanForge.Tests;

/// <summary>
/// The test assembly's own entry point, run as <c>dotnet exec PlanForge.Tests.dll &lt;mode&gt;</c> when a
/// test needs a process of its own: one to place in a job object, or one to register and end. The test
/// runner never calls it.
/// </summary>
internal static class TestHelperProgram
{
    /// <summary>
    /// <c>wait</c> lives until its standard input closes. <c>start-collector &lt;port&gt; &lt;grace&gt;
    /// &lt;program&gt; [arguments…]</c> waits for a line, starts a collector through the starter, prints
    /// its outcome, its process id and whether it runs inside the helper's job object, and then lives until its standard input closes.
    /// </summary>
    /// <param name="args">The mode and its arguments.</param>
    public static int Main(string[] args)
    {
        switch (args)
        {
            case ["wait"]:
                Console.In.ReadToEnd();
                return 0;
            case ["start-collector", var port, var grace, var program, .. var arguments]:
                Console.In.ReadLine();
                var start = CollectorStarter.Start(int.Parse(port, CultureInfo.InvariantCulture), new CollectorCommand(program, arguments),
                                                   TimeSpan.FromSeconds(int.Parse(grace, CultureInfo.InvariantCulture)));
                Console.Out.WriteLine($"{start.Outcome} {start.ProcessId} {start.InsideJob}");
                Console.Out.Flush();
                Console.In.ReadToEnd();
                return 0;
            default:
                return 2;
        }
    }
}
