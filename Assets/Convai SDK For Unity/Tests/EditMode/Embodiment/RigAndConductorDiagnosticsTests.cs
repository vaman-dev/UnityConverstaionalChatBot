using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Domain.Logging;
using Convai.Runtime;
using Convai.Runtime.Animation;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Diagnostics contract for the shared rig and animator infrastructure: a setup gap is
    ///     reported once and then the character goes quiet.
    /// </summary>
    /// <remarks>
    ///     Both subjects sit on write paths that embodiment modules call every frame. A report that
    ///     is not deduplicated does not just fill the console — it builds an interpolated string on
    ///     every frame it repeats, which breaks the zero-steady-state-allocation rule for anything
    ///     on a per-frame path.
    /// </remarks>
    public sealed class RigAndConductorDiagnosticsTests
    {
        private readonly List<GameObject> _spawned = new();

        private ConvaiSettings _settings;
        private LogLevel _originalGlobalLevel;
        private LogLevelOverride[] _originalCategoryOverrides;

        // These tests assert that a diagnostic reaches the console exactly once, so the logger has to
        // be able to emit in the first place. Verbosity is project state, so it is forced here and
        // restored afterwards rather than assumed.
        [SetUp]
        public void SetUp()
        {
            _settings = ConvaiSettings.Instance;
            if (_settings == null) return;

            _originalGlobalLevel = _settings.GlobalLogLevel;
            _originalCategoryOverrides = CloneOverrides(_settings.CategoryOverrides);
            _settings.SetGlobalLogLevel(LogLevel.Trace);
            _settings.SetCategoryOverrides(System.Array.Empty<LogLevelOverride>());
            LoggingConfig.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();

            if (_sink != null)
            {
                ConvaiLogger.UnregisterSink(_sink);
                _sink = null;
            }

            if (_settings == null) return;

            _settings.SetGlobalLogLevel(_originalGlobalLevel);
            _settings.SetCategoryOverrides(CloneOverrides(_originalCategoryOverrides));
            LoggingConfig.InvalidateCache();
        }

        private static LogLevelOverride[] CloneOverrides(LogLevelOverride[] source)
        {
            if (source == null) return System.Array.Empty<LogLevelOverride>();
            var clone = new LogLevelOverride[source.Length];
            System.Array.Copy(source, clone, source.Length);
            return clone;
        }

        /// <summary>
        ///     Collects what the logger actually emitted, so a test can count reports rather than
        ///     assert that one reached Unity's console.
        /// </summary>
        /// <remarks>
        ///     These tests are about how MANY times something is reported, and the console is the
        ///     wrong instrument for that: it shows the first message and says nothing about the
        ///     ninety-nine behind it. Counting at the sink measures the property under test directly.
        /// </remarks>
        private sealed class CapturingLogSink : ILogSink
        {
            private readonly List<string> _messages = new();

            public string Name => nameof(CapturingLogSink);
            public bool IsEnabled { get; private set; } = true;

            public void Write(LogEntry entry) => _messages.Add(entry.Message ?? string.Empty);
            public void Flush() { }
            public void SetEnabled(bool enabled) => IsEnabled = enabled;
            public void Dispose() { }

            internal int CountContaining(string fragment)
            {
                int count = 0;
                for (int i = 0; i < _messages.Count; i++)
                {
                    if (_messages[i].Contains(fragment)) count++;
                }

                return count;
            }
        }

        private CapturingLogSink _sink;

        private CapturingLogSink StartCapturing()
        {
            _sink = new CapturingLogSink();
            ConvaiLogger.RegisterSink(_sink);
            return _sink;
        }

        private GameObject NewObject(string objectName)
        {
            var go = new GameObject(objectName);
            _spawned.Add(go);
            return go;
        }

        // ── AnimatorConductor: ownership conflicts report once ──────────────────────

        [Test]
        public void ForeignParameterWrite_IsReportedOnceNotEveryFrame()
        {
            GameObject host = NewObject("Character");
            host.AddComponent<Animator>();
            var conductor = host.AddComponent<AnimatorConductor>();
            conductor.RefreshAnimator();

            GameObject ownerObject = NewObject("Owner");
            GameObject intruderObject = NewObject("Intruder");

            Assert.IsTrue(
                conductor.RegisterParameter(ownerObject, "Speed", AnimatorParameterType.Float),
                "The owner should be able to claim an unclaimed parameter.");

            CapturingLogSink sink = StartCapturing();

            // Sixty-one frames of the same mistake, which is what a misconfigured character does.
            for (int frame = 0; frame < 61; frame++)
                conductor.WriteFloat(intruderObject, "Speed", 1f);

            Assert.AreEqual(
                1,
                sink.CountContaining("Intruder"),
                "A conflicting write must be reported once, not on every frame that repeats it.");
        }

        [Test]
        public void ReRegisteringAParameter_LetsANewConflictBeReportedAgain()
        {
            GameObject host = NewObject("Character");
            host.AddComponent<Animator>();
            var conductor = host.AddComponent<AnimatorConductor>();
            conductor.RefreshAnimator();

            GameObject firstOwner = NewObject("FirstOwner");
            GameObject secondOwner = NewObject("SecondOwner");
            GameObject intruderObject = NewObject("Intruder");

            conductor.RegisterParameter(firstOwner, "Speed", AnimatorParameterType.Float);

            CapturingLogSink sink = StartCapturing();
            conductor.WriteFloat(intruderObject, "Speed", 1f);
            Assert.AreEqual(1, sink.CountContaining("Intruder"), "The first conflict must be reported.");

            // Ownership changed, so the previous report is spent and a genuine new conflict against
            // the new owner must not be swallowed by the once-per-parameter guard.
            conductor.UnregisterParameter(firstOwner, "Speed");
            conductor.RegisterParameter(secondOwner, "Speed", AnimatorParameterType.Float);

            conductor.WriteFloat(intruderObject, "Speed", 1f);
            Assert.AreEqual(
                2,
                sink.CountContaining("Intruder"),
                "After the parameter changed hands the guard must be able to report again, or it " +
                "silences a real conflict against the new owner for the rest of the session.");
        }

        // ── StandardRigBinding: an unresolvable bone or blendshape says so, once ─────

        [Test]
        public void MissingBone_IsReportedOnceAndThenResolvesFromCache()
        {
            GameObject host = NewObject("Character");
            var binding = host.AddComponent<StandardRigBinding>();
            binding.Rebuild();

            CapturingLogSink sink = StartCapturing();

            Assert.IsFalse(
                binding.TryGetBone(StandardBone.Head, out Transform bone),
                "A bare GameObject has no humanoid rig, so the head bone cannot resolve.");
            Assert.IsNull(bone);

            // Every later lookup answers from the cached miss without reporting again.
            for (int frame = 0; frame < 30; frame++)
                binding.TryGetBone(StandardBone.Head, out _);

            Assert.AreEqual(
                1,
                sink.CountContaining("Head"),
                "A missing bone must name itself once, then stay quiet however often it is asked for.");
        }

        [Test]
        public void MissingBlendshape_IsReportedOnceAndThenResolvesFromCache()
        {
            GameObject host = NewObject("Character");
            var binding = host.AddComponent<StandardRigBinding>();
            binding.Rebuild();

            CapturingLogSink sink = StartCapturing();

            Assert.IsFalse(
                binding.TryGetBlendshape(StandardBlendshape.JawOpen, out SkinnedMeshRenderer mesh, out int index),
                "A character with no face mesh cannot resolve a blendshape.");
            Assert.IsNull(mesh);
            Assert.AreEqual(-1, index);

            for (int frame = 0; frame < 30; frame++)
                binding.TryGetBlendshape(StandardBlendshape.JawOpen, out _, out _);

            Assert.AreEqual(
                1,
                sink.CountContaining("JawOpen"),
                "A missing blendshape must name itself once, then answer from the cached miss.");
        }

        // ── the assumption AnimatedAdditivePoseGuard rests on ───────────────────────

        /// <summary>
        ///     Records what a transform write/read round-trip actually guarantees, because the
        ///     additive-pose guard depends on it and the answer is not what it looks like.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <see cref="AnimatedAdditivePoseGuard" /> decides whether a bone still holds the
        ///         value it last wrote by comparing components for exact equality. Exact is the right
        ///         instrument for that question — "is this exactly what I wrote", not "is this
        ///         approximately mine" — because a tolerance would make the guard miss small but
        ///         genuine writes from another system, which is the opposite of its job.
        ///     </para>
        ///     <para>
        ///         It only works if writing <c>localRotation</c> and reading it back returns the same
        ///         components. <b>It does not, in general.</b> Unity renormalizes a quaternion on
        ///         assignment, so a value that is not already exactly unit length comes back changed —
        ///         measured here at roughly one unit in the last place. Positions round-trip exactly,
        ///         and so do rotations built by <see cref="Quaternion.Euler(float,float,float)" />,
        ///         which is why this has not shown up as an obvious failure.
        ///     </para>
        ///     <para>
        ///         The consequence for the guard is that a bone can read as "written by someone else"
        ///         immediately after the guard itself wrote it. This test exists to state the measured
        ///         behavior rather than the assumed one; the guard's own handling of it is recorded as
        ///         a separate finding.
        ///     </para>
        /// </remarks>
        [Test]
        public void TransformRoundTrip_IsExactForPositionsAndEulerRotations_ButNotForEveryQuaternion()
        {
            Transform bone = NewObject("Bone").transform;

            // Positions round-trip exactly.
            var positions = new[]
            {
                Vector3.zero,
                new Vector3(0.125f, -2.5f, 13.75f),
                new Vector3(1e-5f, -1e-5f, 1e-5f)
            };

            foreach (Vector3 written in positions)
            {
                bone.localPosition = written;
                Vector3 read = bone.localPosition;

                Assert.AreEqual(written.x, read.x, $"x drifted for {written}");
                Assert.AreEqual(written.y, read.y, $"y drifted for {written}");
                Assert.AreEqual(written.z, read.z, $"z drifted for {written}");
            }

            // Euler-built rotations round-trip exactly.
            var exactRotations = new[]
            {
                Quaternion.identity,
                Quaternion.Euler(12.5f, -47.25f, 3.125f),
                Quaternion.Euler(0.0001f, 179.9999f, -0.0001f)
            };

            foreach (Quaternion written in exactRotations)
            {
                bone.localRotation = written;
                Quaternion read = bone.localRotation;

                Assert.AreEqual(written.x, read.x, $"x drifted for {written}");
                Assert.AreEqual(written.y, read.y, $"y drifted for {written}");
                Assert.AreEqual(written.z, read.z, $"z drifted for {written}");
                Assert.AreEqual(written.w, read.w, $"w drifted for {written}");
            }

            // But an axis-angle rotation about a normalized axis does not. This is the case that
            // breaks an exact-equality ownership check, and it is asserted rather than described so
            // that an engine change in either direction is noticed here.
            Quaternion offUnit = Quaternion.AngleAxis(23.7f, new Vector3(0.3f, -0.8f, 0.51f).normalized);
            bone.localRotation = offUnit;
            Quaternion readBack = bone.localRotation;

            Assert.AreNotEqual(
                offUnit.y,
                readBack.y,
                "Unity renormalizes on assignment, so this rotation is expected NOT to round-trip " +
                "exactly. If this now passes, the engine changed and the additive-pose guard's " +
                "exact comparison became safe — revisit the recorded finding.");

            Assert.That(
                Mathf.Abs(offUnit.y - readBack.y),
                Is.LessThan(1e-6f),
                "The drift is expected to be around one unit in the last place, not a real rotation " +
                "change. Anything larger is a different problem.");
        }
    }
}
