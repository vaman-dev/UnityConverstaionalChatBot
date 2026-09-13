using System;
using Convai.Domain.Logging;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    /// <summary>
    ///     The difference between a throttle and a mute, pinned.
    /// </summary>
    /// <remarks>
    ///     The action-response handler had a mute for a long time and it read as a throttle: a set of
    ///     signatures already reported, never cleared, for the whole connection. Every assertion here
    ///     is one of the things that made it undiagnosable — the second attempt answered with
    ///     silence, and a single line standing for forty occurrences.
    /// </remarks>
    [TestFixture]
    public sealed class RepeatedMessageThrottleTests
    {
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Test]
        public void TheFirstOccurrenceIsAlwaysSaid()
        {
            var throttle = new RepeatedMessageThrottle(30d);

            Assert.That(throttle.ShouldSay("a", Start, out int suppressed), Is.True);
            Assert.That(suppressed, Is.Zero, "Nothing was held back before the first time.");
        }

        [Test]
        public void ARepeatInsideTheIntervalIsHeldBack()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("a", Start.AddSeconds(29), out _), Is.False,
                "A fault the Convai Character repeats every turn must not be said every turn.");
        }

        /// <summary>
        ///     The assertion the old mute could never satisfy.
        /// </summary>
        [Test]
        public void TheSameFaultIsSaidAgainOnceTheIntervalHasPassed()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("a", Start.AddSeconds(30), out _), Is.True,
                "Somebody changed something and tried again. Silence reads as success.");
        }

        [Test]
        public void HowManyWereHeldBackIsReportedWithTheNextLine()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);
            throttle.ShouldSay("a", Start.AddSeconds(1), out _);
            throttle.ShouldSay("a", Start.AddSeconds(2), out _);

            Assert.That(throttle.ShouldSay("a", Start.AddSeconds(31), out int suppressed), Is.True);
            Assert.That(suppressed, Is.EqualTo(2),
                "One line standing for three occurrences without saying so is the difference " +
                "between a fluke and the thing to fix next.");
        }

        [Test]
        public void TheCountDescribesTheGapThatJustEndedRatherThanEveryGap()
        {
            var throttle = new RepeatedMessageThrottle(10d);
            throttle.ShouldSay("a", Start, out _);
            throttle.ShouldSay("a", Start.AddSeconds(1), out _);
            throttle.ShouldSay("a", Start.AddSeconds(10), out int firstGap);
            throttle.ShouldSay("a", Start.AddSeconds(20), out int secondGap);

            Assert.That(firstGap, Is.EqualTo(1));
            Assert.That(secondGap, Is.Zero, "Nothing was held back during the second gap.");
        }

        [Test]
        public void DistinctFaultsAreThrottledIndependently()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("b", Start, out _), Is.True,
                "Two different faults in one batch are two different things to fix.");
        }

        [Test]
        public void ResetMakesEveryFaultSpeakAgain()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);
            throttle.Reset();

            Assert.That(throttle.ShouldSay("a", Start.AddSeconds(1), out int suppressed), Is.True);
            Assert.That(suppressed, Is.Zero, "A reset forgets the gap as well as the fault.");
        }

        [Test]
        public void AZeroIntervalSaysEveryOccurrence()
        {
            var throttle = new RepeatedMessageThrottle(0d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("a", Start, out _), Is.True);
        }

        [Test]
        public void ANegativeIntervalIsTreatedAsZeroRatherThanAsForever()
        {
            var throttle = new RepeatedMessageThrottle(-5d);

            Assert.That(throttle.IntervalSeconds, Is.Zero,
                "Clamping upward would be a mute reachable by a typo.");
        }

        [Test]
        public void AnEmptyKeyIsNeverSaid()
        {
            var throttle = new RepeatedMessageThrottle(30d);

            Assert.That(throttle.ShouldSay(null, Start, out _), Is.False);
            Assert.That(throttle.ShouldSay(string.Empty, Start, out _), Is.False);
        }
    }
}
