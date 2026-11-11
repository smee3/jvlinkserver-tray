using System;

namespace JVLinkServerTray.Tests
{
    public static class HarnessTests
    {
        [Test] public static void Harness_equal_passes() { Assert.Equal(3, 1 + 2, "sum"); }

        [Test] public static void Harness_equal_failureNamesBothValues()
        {
            try { Assert.Equal("a", "b", "x"); }
            catch (AssertionException e) { Assert.True(e.Message.Contains("<a>") && e.Message.Contains("<b>"), "message: " + e.Message); return; }
            throw new Exception("AssertionException expected");
        }
    }
}
