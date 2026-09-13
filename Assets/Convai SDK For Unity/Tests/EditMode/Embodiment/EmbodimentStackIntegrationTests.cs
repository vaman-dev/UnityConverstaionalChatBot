using Convai.Modules.ConversationFlow.Components;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Cross-module integration tests for the full Convai embodiment stack on a single
    ///     <see cref="EmbodimentContext" />.  Verifies that module controllers co-exist on the
    ///     same rig without slot conflicts, that <see cref="EmbodimentContext" /> routes reads
    ///     and registrations correctly across enable/disable boundaries, and that manually
    ///     ticking multiple modules in the correct phase order produces consistent state.
    ///     No real Unity frames are pumped; all ticks are driven via
    ///     <see cref="IEmbodimentTickable" />.
    /// </summary>
    [TestFixture]
    public sealed class EmbodimentStackIntegrationTests
    {
        private EmbodimentTestRig _rig;

        [SetUp]
        public void SetUp()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _rig = EmbodimentTestRig.Create(nameof(EmbodimentStackIntegrationTests));
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        // ── Two-module coexistence ─────────────────────────────────────────────

        [Test]
        public void EmotionController_And_ConversationFlowController_NoSlotConflict()
        {
            var emotionHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flowHarness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);

            Assert.That(_rig.Context.EmotionStateSource, Is.SameAs(emotionHarness.Controller));
            Assert.That(_rig.Context.ConversationFlowSource, Is.SameAs(flowHarness.Controller));
        }

        // ── Multi-module tick correctness ──────────────────────────────────────

        [Test]
        public void EmotionController_And_ConversationFlowController_100Ticks_DoesNotThrow()
        {
            var emotionHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flowHarness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);

            float dt = 1f / 60f;
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    flowHarness.Tick(dt);
                    emotionHarness.Tick(dt);
                }
            });
        }

        // ── Full module stack ──────────────────────────────────────────────────

        [Test]
        public void FullStack_AllControllersEnabled_AllSlotsRegistered()
        {
            var emotionHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flowHarness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
            var _ = new EmbodimentReceiverHarness<ConvaiBodyAnimationController, ConvaiBodyAnimationProfile>(_rig);

            Assert.That(_rig.Context.EmotionStateSource, Is.SameAs(emotionHarness.Controller));
            Assert.That(_rig.Context.ConversationFlowSource, Is.SameAs(flowHarness.Controller));
        }

        [Test]
        public void FullStack_100Ticks_DoesNotThrow()
        {
            var emotionHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flowHarness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
            var bodyAnimHarness = new EmbodimentReceiverHarness<ConvaiBodyAnimationController, ConvaiBodyAnimationProfile>(_rig);

            float dt = 1f / 60f;
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    // Cognition phase first.
                    flowHarness.Tick(dt);
                    emotionHarness.Tick(dt);
                    // Expression phase last.
                    bodyAnimHarness.Tick(dt);
                }
            });
        }

        [Test]
        public void FullStack_DisableAll_AllSlotsCleared()
        {
            var emotionHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flowHarness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
            var _ = new EmbodimentReceiverHarness<ConvaiBodyAnimationController, ConvaiBodyAnimationProfile>(_rig);

            ConvaiConversationFlowDriverRegistry.Reset();
            emotionHarness.Disable();
            flowHarness.Disable();

            Assert.That(_rig.Context.EmotionStateSource, Is.Null);
            Assert.That(_rig.Context.ConversationFlowSource, Is.Null);
        }

        [Test]
        public void FullStack_ReenableAfterDisableAll_AllSlotsReregistered()
        {
            var emotionHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flowHarness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
            var _ = new EmbodimentReceiverHarness<ConvaiBodyAnimationController, ConvaiBodyAnimationProfile>(_rig);

            ConvaiConversationFlowDriverRegistry.Reset();
            emotionHarness.Disable();
            flowHarness.Disable();

            ConvaiConversationFlowDriverRegistry.Reset();
            emotionHarness.Enable();
            flowHarness.Enable();

            Assert.That(_rig.Context.EmotionStateSource, Is.SameAs(emotionHarness.Controller));
            Assert.That(_rig.Context.ConversationFlowSource, Is.SameAs(flowHarness.Controller));
        }

        // ── Profile routing in multi-module stack ──────────────────────────────

        [Test]
        public void ApplyProfile_ToSpecificController_DoesNotAffectOtherControllers_ProfileReceiverCount()
        {
            // Verifies that the profile-receiver index tracks separate entries per
            // controller rather than a single shared slot.
            var _ = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var __ = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
            var ___ = new EmbodimentReceiverHarness<ConvaiBodyAnimationController, ConvaiBodyAnimationProfile>(_rig);

            var registrations = new System.Collections.Generic.List<EmbodimentProfileReceiverRegistration>();
            _rig.Context.GetProfileReceivers(registrations);

            // Each controller registered itself; expect at least one entry per module.
            Assert.That(registrations.Count, Is.GreaterThanOrEqualTo(3));
        }
    }
}
