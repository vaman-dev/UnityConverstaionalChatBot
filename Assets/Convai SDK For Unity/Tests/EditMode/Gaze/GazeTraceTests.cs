using System.Collections.Generic;
using Convai.Modules.Gaze.Core.Diagnostics;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Pins the two halves of the diagnostics contract that the release-polish round separated:
    ///     the console gate ships closed, and the in-memory ring buffer stays open behind it so the
    ///     inspector's Live panel still has something to show.
    /// </summary>
    public sealed class GazeTraceTests
    {
        private readonly List<GazeTraceEntry> _entries = new();

        private static GazeTrace NewTrace() => new("TestCharacter", () => 0f);

        [Test]
        public void NewTrace_StartsSilent()
        {
            Assert.That(NewTrace().Verbosity, Is.EqualTo(GazeTraceVerbosity.Off),
                "A trace constructed before its profile resolves must not be briefly chatty.");
        }

        [Test]
        public void StateEntries_AreRecordedEvenWhileTheConsoleGateIsClosed()
        {
            GazeTrace trace = NewTrace();

            trace.State("Target changed to 'Player'.");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(1),
                "A silent console must not also blind the Live panel — the ring buffer is not " +
                "gated by Verbosity.");
            Assert.That(_entries[0].Message, Is.EqualTo("Target changed to 'Player'."));
        }

        [Test]
        public void WarningsAndErrors_AreRecordedWhileSilent()
        {
            GazeTrace trace = NewTrace();

            trace.Warning("Rig binding has no semantic Head mapping.");
            trace.Error("Something is badly wrong.");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(2),
                "Warnings and errors bypass the gate in both directions: logged and recorded.");
        }

        [Test]
        public void Firehose_IsNeverRecorded()
        {
            GazeTrace trace = NewTrace();
            trace.Verbosity = GazeTraceVerbosity.Firehose;

            trace.Firehose("per-tick dump");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(0),
                "Per-tick dumps must never evict the transition history the buffer exists to keep.");
        }

        [Test]
        public void IsEnabled_StillGatesDetailCallSites()
        {
            GazeTrace trace = NewTrace();

            Assert.IsFalse(trace.IsEnabled(GazeTraceVerbosity.Detail),
                "Detail call sites test this before interpolating, so a silent character allocates nothing.");

            trace.Verbosity = GazeTraceVerbosity.Detail;
            Assert.IsTrue(trace.IsEnabled(GazeTraceVerbosity.Detail));
        }

        [Test]
        public void RingBuffer_KeepsTheMostRecentEntries()
        {
            GazeTrace trace = NewTrace();

            for (int i = 0; i < GazeTrace.Capacity + 5; i++)
                trace.State($"entry {i}");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(GazeTrace.Capacity));
            Assert.That(_entries[^1].Message, Is.EqualTo($"entry {GazeTrace.Capacity + 4}"));
            Assert.That(trace.TotalRecorded, Is.EqualTo(GazeTrace.Capacity + 5));
        }
    }
}
