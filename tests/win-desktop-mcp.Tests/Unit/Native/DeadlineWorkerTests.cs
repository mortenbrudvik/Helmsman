namespace WinDesktopMcp.Tests.Unit.Native;

/// <summary>
/// Unit tests for <see cref="DeadlineWorker"/>, which the shell, virtual desktop, Applications folder and
/// Windows.Graphics.Capture wrappers run their blocking calls on. The calls here are plain delegates that block on an event:
/// nothing native runs. A call that must miss its deadline gets a deadline of zero, so no test waits one out, and a call that
/// must make it gets a deadline far above a thread's start-up on a loaded machine (TESTING.md), which an answer ends at once.
/// </summary>
public class DeadlineWorkerTests
{
    private static readonly TimeSpan AnswerDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(ApartmentState.STA)]
    [InlineData(ApartmentState.MTA)]
    public void Run_ReturnsTheResult_FromANamedBackgroundThreadOfTheApartment(ApartmentState apartment)
    {
        var sut = new DeadlineWorker("Test read", apartment, AnswerDeadline);
        var caller = Environment.CurrentManagedThreadId;

        var (threadId, name, isBackground, state) = sut.Run(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.Name, Thread.CurrentThread.IsBackground,
                Thread.CurrentThread.GetApartmentState()),
            fallback: default);

        threadId.Should().NotBe(caller);
        name.Should().Be("Test read", "diagnostics show the thread's name");
        isBackground.Should().BeTrue("a blocked call must not keep the server process alive");
        state.Should().Be(apartment);
        sut.AbandonedCalls.Should().Be(0);
    }

    [Fact]
    public void Run_ACallThatThrows_ReturnsTheFallback_AndIsNotCounted()
    {
        var sut = new DeadlineWorker("Test read", ApartmentState.MTA, AnswerDeadline);

        var result = sut.Run<string?>(() => throw new InvalidOperationException("no shell"), fallback: "fallback");

        result.Should().Be("fallback");
        sut.AbandonedCalls.Should().Be(0);
        sut.Run(() => "answer", fallback: "fallback").Should().Be("answer", "a call that failed leaves nothing stuck");
    }

    [Fact]
    public void Run_AThreadThatCannotBeSetUp_ReturnsTheFallback_WithoutRunningTheCall()
    {
        // An apartment state that does not exist: SetApartmentState refuses it, so no thread starts
        var sut = new DeadlineWorker("Test read", (ApartmentState)42, AnswerDeadline);
        var called = false;

        var result = sut.Run(() => called = true, fallback: false);

        result.Should().BeFalse();
        called.Should().BeFalse();
        sut.AbandonedCalls.Should().Be(0);
    }

    [Fact]
    public void Run_ACallThatMissesTheDeadline_ReturnsTheFallback_AndIsCountedUntilItReturns()
    {
        // Arrange - the call blocks until the test releases it, as a hung shell would; its cleanup runs on its own thread.
        // The events it uses are not disposed: its thread waits on them, and the finally always releases it
        var sut = new DeadlineWorker("Test read", ApartmentState.STA, TimeSpan.Zero);
        var release = new ManualResetEventSlim();
        var cleanedUp = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        try
        {
            // Act
            var result = sut.Run(() =>
            {
                try
                {
                    started.Set();
                    release.Wait();
                    return "late answer";
                }
                finally
                {
                    cleanedUp.Set();
                }
            }, fallback: "fallback");

            // Assert - abandoned: the caller has the fallback, and the call counts as stuck
            result.Should().Be("fallback");
            sut.AbandonedCalls.Should().Be(1);

            // When it returns, its cleanup runs, its result is dropped, and it stops counting
            started.Wait(Long).Should().BeTrue();
            release.Set();
            cleanedUp.Wait(Long).Should().BeTrue();
            SpinWait.SpinUntil(() => sut.AbandonedCalls == 0, Long).Should().BeTrue();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Run_WhileACallIsStuck_ReturnsTheFallbackAtOnce_WithoutStartingAnother_ThenRunsAgainOnceItReturns()
    {
        // Arrange - one call missed its deadline and stays blocked until the test releases it: it has no timeout of its
        // own, so it cannot return while the test checks the refusal, however slow the machine. Not disposed: the call's
        // thread waits on it, and the finally always releases it
        var stuck = new DeadlineWorker("Test read", ApartmentState.MTA, TimeSpan.Zero);
        var release = new ManualResetEventSlim();
        try
        {
            stuck.Run(() =>
            {
                release.Wait();
                return true;
            }, fallback: false).Should().BeFalse();
            var called = false;

            // Act - a call made meanwhile, with all the time it needs
            var refused = stuck.Run(() => called = true, fallback: false);

            // Assert - refused without a thread of its own
            refused.Should().BeFalse();
            called.Should().BeFalse();
            stuck.AbandonedCalls.Should().Be(1);

            // Another worker's calls go on: the count is the instance's own
            new DeadlineWorker("Other read", ApartmentState.MTA, AnswerDeadline).Run(() => "answer", fallback: "fallback").Should().Be("answer");

            // Once the stuck call returns, the next call is started again (on this worker's zero deadline it is abandoned
            // in turn, so the test only waits for it to run)
            release.Set();
            SpinWait.SpinUntil(() => stuck.AbandonedCalls == 0, Long).Should().BeTrue();
            using var ran = new ManualResetEventSlim();
            stuck.Run(() =>
            {
                ran.Set();
                return true;
            }, fallback: false);
            ran.Wait(Long).Should().BeTrue();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Run_ACallThatThrowsAfterItWasAbandoned_StopsCounting_AndNothingLeavesItsThread()
    {
        // Arrange - an exception that left the call's thread would end the test host; the handler sees it if one does
        var sut = new DeadlineWorker("Test read", ApartmentState.MTA, TimeSpan.Zero);
        var release = new ManualResetEventSlim();
        Exception? escaped = null;
        UnhandledExceptionEventHandler onUnhandled = (_, e) => escaped = e.ExceptionObject as Exception;
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        try
        {
            // Act - abandoned at once, then it fails once released
            var result = sut.Run<string>(() =>
            {
                release.Wait();
                throw new InvalidOperationException("the shell went away");
            }, fallback: "fallback");

            // Assert
            result.Should().Be("fallback");
            sut.AbandonedCalls.Should().Be(1);

            release.Set();
            SpinWait.SpinUntil(() => sut.AbandonedCalls == 0, Long).Should().BeTrue("a call that throws still returns");
            escaped.Should().BeNull();
        }
        finally
        {
            release.Set();
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
        }
    }

    [Fact]
    public async Task Run_CallersAtTheSameTime_EachGetTheirOwnThreadAndResult()
    {
        // Arrange - each call waits until both are running, so they can only finish if they run at once
        var sut = new DeadlineWorker("Test read", ApartmentState.MTA, AnswerDeadline);
        using var bothRunning = new CountdownEvent(2);
        (int Thread, string Value) fallback = (0, "fallback");

        (int Thread, string Value) Call(string value)
        {
            bothRunning.Signal();
            bothRunning.Wait(Long);
            return (Environment.CurrentManagedThreadId, value);
        }

        // Act
        var first = Task.Factory.StartNew(() => sut.Run(() => Call("first"), fallback), TaskCreationOptions.LongRunning);
        var second = Task.Factory.StartNew(() => sut.Run(() => Call("second"), fallback), TaskCreationOptions.LongRunning);
        var results = await Task.WhenAll(first, second).WaitAsync(Long);

        // Assert
        results[0].Value.Should().Be("first");
        results[1].Value.Should().Be("second");
        results[0].Thread.Should().NotBe(results[1].Thread);
        sut.AbandonedCalls.Should().Be(0);
    }

    [Fact]
    public void Constructor_RequiresAThreadName()
    {
        var act = () => new DeadlineWorker(" ", ApartmentState.MTA, AnswerDeadline);

        act.Should().Throw<ArgumentException>();
    }
}
