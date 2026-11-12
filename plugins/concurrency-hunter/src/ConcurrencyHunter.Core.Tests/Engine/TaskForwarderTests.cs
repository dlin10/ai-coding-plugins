using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class TaskForwarderTests
{
    [Fact]
    public void An_implicit_task_reference_conversion_does_not_leave_work_running_in_a_finally() => Check("", pair: false);

    [Fact]
    public void An_explicit_reference_conversion_before_the_wait_can_leave_work_running_in_a_finally() =>
        Check("object value = stoppingToken.CanBeCanceled ? new object() : \"ok\"; _ = (string)value;", pair: true);

    [Theory]
    [InlineData("await task.ConfigureAwait(false);", false)]
    [InlineData("await Task.WhenAll(task);", true)]
    [InlineData("await Task.WhenAll(task).ConfigureAwait(false);", true)]
    [InlineData("Task.WhenAll(task).Wait();", false)]
    public void Forwarding_preserves_the_wait_forms_exceptional_paths(string wait, bool pair)
    {
        var run = Analyze(Startup("services.AddHostedService<Worker>();") + $$"""
            public sealed class Worker : BackgroundService
            {
                public static int Value;
                private void Write() { Value = 1; }
                private void After() { Value = 2; }
                private async Task<int> WriteAsync() { await Task.Yield(); Write(); return 1; }
                private Task<int> Fwd() => WriteAsync();
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var task = Fwd();
                    try { {{wait}} }
                    catch (Exception) { }
                    After();
                }
            }
            """);
        Assert.Contains(run.Of("Value"), access => access.Symbol == "Worker.Write()");
        Assert.Contains(run.Of("Value"), access => access.Symbol == "Worker.After()");
        Assert.Equal(pair, run.PairsOn("Value").Any(item => item.First.Symbol == "Worker.Write()" && item.Second.Symbol == "Worker.After()" ||
                                                         item.Second.Symbol == "Worker.Write()" && item.First.Symbol == "Worker.After()"));
    }

    private static void Check(string beforeWait, bool pair)
    {
        var run = Analyze(Startup("services.AddHostedService<Worker>();") + $$"""
            public sealed class Worker : BackgroundService
            {
                public static int Value;
                private void Write() { Value = 1; }
                private void After() { Value = 2; }
                private async Task<int> WriteAsync() { await Task.Yield(); Write(); return 1; }
                private Task Fwd() => WriteAsync();
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        try
                        {
                            var task = Fwd();
                            {{beforeWait}}
                            await task.ConfigureAwait(false);
                        }
                        finally { After(); }
                    }
                }
            }
            """);
        Assert.Contains(run.Of("Value"), access => access.Symbol == "Worker.Write()");
        Assert.Contains(run.Of("Value"), access => access.Symbol == "Worker.After()");
        Assert.Equal(pair, run.PairsOn("Value").Any(item => item.First.Symbol == "Worker.Write()" && item.Second.Symbol == "Worker.After()" ||
                                                         item.Second.Symbol == "Worker.Write()" && item.First.Symbol == "Worker.After()"));
    }
}
