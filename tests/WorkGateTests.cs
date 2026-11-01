using System;

namespace JVLinkServerTray.Tests
{
    public static class WorkGateTests
    {
        static TimeSpan S(double sec) { return TimeSpan.FromSeconds(sec); }
        static WorkGate G() { return new WorkGate(true, S(80)); }    // ShutdownTimeoutSeconds 20 + 60

        [Test] public static void WorkGate_oneAtATime_latestRequestWins_pollsAreNotQueued() {
            var g = G();
            Assert.Equal((Work?)Work.Startup, g.Request(Work.Startup, S(0)), "startup runs");
            Assert.Null(g.Tick(S(5)), "no poll while busy");
            Assert.Null(g.Request(Work.Stop, S(6)), "waits");
            Assert.Null(g.Request(Work.Rebuild, S(7)), "waits, replaces the stop");
            Assert.Equal((Work?)Work.Rebuild, g.Done(S(8)), "the latest request runs next");
            Assert.Null(g.Done(S(9)), "nothing else queued");
            Assert.Equal((Work?)Work.Poll, g.Tick(S(10)), "poll when idle");
        }
        [Test] public static void WorkGate_rebuildAskedWhileARebuildRuns_isDropped() {
            var g = G();
            Assert.Equal((Work?)Work.Rebuild, g.Request(Work.Rebuild, S(0)), "rebuild runs");
            Assert.Null(g.Request(Work.Rebuild, S(1)), "double click");
            Assert.Null(g.Done(S(30)), "no second rebuild");
            Assert.Equal((Work?)Work.Poll, g.Tick(S(31)), "idle again");
        }
        [Test] public static void WorkGate_stallGuard_tripsOnceAfterTheLimit() {
            var g = G();
            g.Request(Work.Poll, S(0));
            Assert.Null(g.Tick(S(80)), "at the limit: still waiting"); Assert.False(g.Stalled, "not yet");
            Assert.Null(g.Tick(S(81)), "past the limit"); Assert.True(g.Stalled, "stalled");
            Assert.False(g.ShouldFinish, "not exiting: the tray stays");
            Assert.Equal((Work?)Work.Poll, g.Done(S(500)) ?? g.Tick(S(501)), "the item came back: polls again");
            Assert.False(g.Stalled, "recovered");
        }
        [Test] public static void WorkGate_exitBehindAStuckItem_finishesWithoutWaiting() {
            var g = G();
            g.Request(Work.Poll, S(0));                             // hangs
            Assert.Null(g.Request(Work.Exit, S(10)), "終了 waits behind the stuck item");
            Assert.False(g.ShouldFinish, "not yet");
            g.Tick(S(70)); Assert.False(g.ShouldFinish, "within the limit");
            g.Tick(S(81));
            Assert.True(g.Stalled, "stalled"); Assert.True(g.ShouldFinish, "終了 completes without the stuck item");
        }
        [Test] public static void WorkGate_exitWhileStalled_finishesAtOnce() {
            var g = G();
            g.Request(Work.Rebuild, S(0));
            g.Tick(S(100)); Assert.True(g.Stalled, "stalled");
            Assert.Null(g.Request(Work.Exit, S(101)), "nothing to start");
            Assert.True(g.ShouldFinish, "finish now");
        }
        [Test] public static void WorkGate_exitItemReturned_finishes() {
            var g = G();
            Assert.Equal((Work?)Work.Exit, g.Request(Work.Exit, S(0)), "exit runs");
            Assert.Null(g.Request(Work.Rebuild, S(1)), "no more menu work while exiting");
            Assert.Null(g.Done(S(25)), "nothing after exit");
            Assert.True(g.ShouldFinish, "finish");
        }
        [Test] public static void WorkGate_noPollsForTheTrayThatControlsNoServer() {
            var g = new WorkGate(false, S(80));
            g.Request(Work.Startup, S(0)); g.Done(S(1));
            Assert.Null(g.Tick(S(5)), "no poll");
        }
    }
}
