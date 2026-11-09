using NUnit.Framework;

using System;
using UnityEngine;

namespace MaoIntegration.Tests
{
    public sealed class MaoAutonomyLogicTests
    {
        [TestCase(false, false, false, false, false, false, false, true)]
        [TestCase(true, false, false, false, false, false, false, false)]
        [TestCase(false, true, false, false, false, false, false, false)]
        [TestCase(false, false, true, false, false, false, false, false)]
        [TestCase(false, false, false, true, false, false, false, false)]
        [TestCase(false, false, false, false, true, false, false, false)]
        [TestCase(false, false, false, false, false, true, false, false)]
        [TestCase(false, false, false, false, false, false, true, false)]
        public void IdleActionPolicy_AllowsOnlyStandingUnrestrictedAvatar(
            bool isSitting,
            bool isSleeping,
            bool isWindowSitting,
            bool isTaskbarSitting,
            bool isLying,
            bool isMounted,
            bool isTransitioning,
            bool expected)
        {
            Assert.AreEqual(expected, MaoIdleActionPolicy.CanPlayAutonomous(
                isSitting,
                isSleeping,
                isWindowSitting,
                isTaskbarSitting,
                isLying,
                isMounted,
                isTransitioning));
        }

        [Test]
        public void Personality_DefaultsMatchMikuProfile()
        {
            MaoPersonalityProfile profile = MaoPersonalityProfile.Default;

            Assert.AreEqual(0.35f, profile.Talkativeness, 0.001f);
            Assert.AreEqual(0.75f, profile.Curiosity, 0.001f);
            Assert.AreEqual(0.55f, profile.Shyness, 0.001f);
            Assert.AreEqual(0.70f, profile.Playfulness, 0.001f);
            Assert.AreEqual(0.70f, profile.Patience, 0.001f);
            Assert.AreEqual(0.60f, profile.Energy, 0.001f);
        }

        [Test]
        public void AnimationOwnership_HigherPriorityInterruptsLowerPriority()
        {
            var ownership = new MaoAnimationOwnership();

            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Locomotion, 10f, float.PositiveInfinity));
            Assert.IsFalse(ownership.TryAcquire(MaoAnimationOwner.Interaction, 10f, 2f));
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Guitar, 10f, float.PositiveInfinity));
            Assert.AreEqual(MaoAnimationOwner.Guitar, ownership.CurrentOwner);
        }

        [Test]
        public void AnimationOwnership_LeaseExpiresAndCanBeReplaced()
        {
            var ownership = new MaoAnimationOwnership();

            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Interaction, 4f, 1.5f));
            Assert.AreEqual(MaoAnimationOwner.Interaction, ownership.CurrentOwner);
            Assert.AreEqual(0f, ownership.Remaining(5.5f), 0.001f);
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Locomotion, 5.5f, float.PositiveInfinity));
            Assert.AreEqual(float.PositiveInfinity, ownership.Remaining(10f));
        }

        [Test]
        public void AnimationOwnership_ReleaseRestoresIdleArbiter()
        {
            var ownership = new MaoAnimationOwnership();

            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Pose, 0f, float.PositiveInfinity));
            Assert.IsTrue(ownership.Release(MaoAnimationOwner.Pose));
            Assert.IsFalse(ownership.IsOwnedBy(MaoAnimationOwner.Pose, 0f));
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Idle, 0f, 2f));
        }

        [Test]
        public void AnimationOwnership_ExplicitModesKeepDeterministicPriority()
        {
            var ownership = new MaoAnimationOwnership();
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Locomotion, 0f, float.PositiveInfinity));
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Seating, 0f, float.PositiveInfinity));
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.CustomDance, 0f, 1f));
            Assert.IsTrue(ownership.TryAcquire(MaoAnimationOwner.Gravity, 0f, float.PositiveInfinity));
            Assert.AreEqual(MaoAnimationOwner.Gravity, ownership.CurrentOwner);
        }

        [Test]
        public void EmotionalState_SilenceIsBoundedAndUserTurnReducesQuietAffect()
        {
            DateTime start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            MaoEmotionalState state = MaoEmotionalState.CreateDefault(start);
            MaoEmotionEngine.AdvanceSilence(state, start, start.AddDays(30));
            Assert.LessOrEqual(state.Loneliness, 1f);
            Assert.LessOrEqual(state.Irritation, 1f);
            float before = state.Loneliness;
            MaoEmotionEngine.ApplyConversationEvent(state, MaoConversationEmotionEvent.UserMessage, start.AddDays(30));
            Assert.Less(state.Loneliness, before);
        }

        [Test]
        public void Initiative_LowSalienceUsuallyChoosesNoAction()
        {
            MaoInitiativeDecision decision = MaoInitiativeLogic.Evaluate(new MaoInitiativeInput
            {
                IdleSeconds = 8f,
                UserIsActive = true,
                FocusProtection = 0.9f,
                Novelty = 0.05f,
                Curiosity = 0.1f,
                SecondsSinceReaction = 20f,
                StimulationNeed = 0.1f,
                Salience = 0.05f,
                Personality = MaoPersonalityProfile.Default
            });

            Assert.AreEqual(MaoInitiativeChannel.None, decision.Channel);
            Assert.Less(decision.SpeechScore, 0.55f);
        }

        [Test]
        public void Initiative_CuriosityPromotesPhysicalBeforeSpeech()
        {
            MaoInitiativeDecision decision = MaoInitiativeLogic.Evaluate(new MaoInitiativeInput
            {
                IdleSeconds = 95f,
                Novelty = 0.78f,
                Curiosity = 0.86f,
                SecondsSinceReaction = 240f,
                SecondsSinceSpeech = 420f,
                StimulationNeed = 0.65f,
                Salience = 0.55f,
                Personality = MaoPersonalityProfile.Default
            });

            Assert.AreEqual(MaoInitiativeChannel.Physical, decision.Channel);
            Assert.Greater(decision.PhysicalScore, decision.SpeechScore);
        }

        [Test]
        public void Initiative_BusyConversationSuppressesAutonomousSpeechAndVision()
        {
            MaoInitiativeDecision decision = MaoInitiativeLogic.Evaluate(new MaoInitiativeInput
            {
                IdleSeconds = 600f,
                Novelty = 1f,
                Curiosity = 1f,
                SecondsSinceReaction = 600f,
                SecondsSinceSpeech = 600f,
                StimulationNeed = 1f,
                Salience = 1f,
                ConversationBusy = true,
                Personality = MaoPersonalityProfile.Default
            });

            Assert.Less(decision.SpeechScore, 0.2f);
            Assert.Less(decision.VisionScore, 0.2f);
        }

        [Test]
        public void VisionInitiative_RequiresAnActualScreenContextChange()
        {
            var input = new MaoInitiativeInput
            {
                IdleSeconds = 30f,
                UserIsActive = true,
                ScreenChanged = false,
                Novelty = 1f,
                Curiosity = 1f,
                FocusProtection = 0.8f,
                SecondsSinceReaction = 0f,
                SecondsSinceVision = 600f,
                Salience = 1f,
                Personality = MaoPersonalityProfile.Default
            };

            Assert.AreNotEqual(MaoInitiativeChannel.Vision, MaoInitiativeLogic.Evaluate(input).Channel);
            input.ScreenChanged = true;
            Assert.AreEqual(MaoInitiativeChannel.Vision, MaoInitiativeLogic.Evaluate(input).Channel);
        }

        [Test]
        public void ScreenChangeDetector_TinyChangesStayBelowMeaningfulThreshold()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.02f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(3f);
            detector.MarkVisionAccepted(3f);
            byte[] tinyChange = new byte[100];
            for (int index = 0; index < 4; index++) tinyChange[index] = 255;

            MaoScreenChangeResult result = detector.Observe(tinyChange, 70f);

            Assert.AreEqual(0.04f, result.Difference, 0.001f);
            Assert.IsFalse(result.ShouldRequestVision);
            Assert.AreEqual(MaoScreenChangeReason.Unchanged, result.Reason);
        }

        [Test]
        public void ScreenChangeDetector_LabelsInitialBaselineRequestAsInitializing()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.02f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];

            MaoScreenChangeResult waiting = detector.Observe(baseline, 0f);
            MaoScreenChangeResult ready = detector.Observe(baseline, 3f);

            Assert.AreEqual(MaoScreenChangeReason.Initializing, waiting.Reason);
            Assert.IsFalse(waiting.ShouldRequestVision);
            Assert.AreEqual(MaoScreenChangeReason.Initializing, ready.Reason);
            Assert.IsTrue(ready.ShouldRequestVision);
        }

        [Test]
        public void ScreenChangeDetector_ZeroStabilityThresholdDoesNotTreatIdenticalFramesAsMotion()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0f, 3f, 0f, 900f);
            byte[] baseline = new byte[100];
            byte[] changed = FilledSample(100, 255);
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(3f);
            detector.MarkVisionAccepted(3f);

            detector.Observe(changed, 10f);
            MaoScreenChangeResult stable = detector.Observe(changed, 13f);

            Assert.AreEqual(MaoScreenChangeReason.MeaningfulChange, stable.Reason);
            Assert.IsTrue(stable.ShouldRequestVision);
        }

        [Test]
        public void ScreenChangeDetector_LargeChangeWaitsUntilScreenIsStable()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.02f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(0f);
            detector.MarkVisionAccepted(0f);
            byte[] changed = FilledSample(100, 255);

            MaoScreenChangeResult moving = detector.Observe(changed, 60f);
            MaoScreenChangeResult nearlyStable = detector.Observe(changed, 62.9f);
            MaoScreenChangeResult stable = detector.Observe(changed, 63f);

            Assert.AreEqual(MaoScreenChangeReason.WaitingForStability, moving.Reason);
            Assert.IsFalse(nearlyStable.ShouldRequestVision);
            Assert.IsTrue(stable.ShouldRequestVision);
            Assert.AreEqual(MaoScreenChangeReason.MeaningfulChange, stable.Reason);
        }

        [Test]
        public void ScreenChangeDetector_VisionCooldownBlocksRepeatedRequests()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.02f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(0f);
            detector.MarkVisionAccepted(0f);
            byte[] changed = FilledSample(100, 255);

            detector.Observe(changed, 10f);
            MaoScreenChangeResult cooldown = detector.Observe(changed, 13f);
            MaoScreenChangeResult ready = detector.Observe(changed, 60f);

            Assert.AreEqual(MaoScreenChangeReason.Cooldown, cooldown.Reason);
            Assert.Greater(cooldown.CooldownRemainingSeconds, 46f);
            Assert.IsTrue(ready.ShouldRequestVision);
        }

        [Test]
        public void ScreenChangeDetector_ToleratesSmallAvatarMotionWhileDebouncingLargeChange()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.04f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(0f);
            detector.MarkVisionAccepted(0f);
            byte[] changed = FilledSample(100, 255);
            byte[] smallAvatarMotion = FilledSample(100, 255);
            for (int index = 0; index < 3; index++) smallAvatarMotion[index] = 0;

            detector.Observe(changed, 60f);
            MaoScreenChangeResult stable = detector.Observe(smallAvatarMotion, 63f);

            Assert.AreEqual(0.03f, stable.MotionDifference, 0.001f);
            Assert.IsTrue(stable.ShouldRequestVision);
            Assert.AreEqual(MaoScreenChangeReason.MeaningfulChange, stable.Reason);
        }

        [Test]
        public void ScreenChangeDetector_MaximumSilenceForcesAnUnchangedObservation()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.02f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(0f);
            detector.MarkVisionAccepted(0f);

            MaoScreenChangeResult beforeFallback = detector.Observe(baseline, 899f);
            MaoScreenChangeResult fallback = detector.Observe(baseline, 900f);

            Assert.IsFalse(beforeFallback.ShouldRequestVision);
            Assert.IsTrue(fallback.ShouldRequestVision);
            Assert.AreEqual(MaoScreenChangeReason.MaximumSilence, fallback.Reason);
        }

        [Test]
        public void ScreenChangeDetector_OnlySuccessfulVisionUpdatesAcceptedBaseline()
        {
            var detector = new MaoScreenChangeDetector(0.08f, 0.02f, 3f, 60f, 900f);
            byte[] baseline = new byte[100];
            byte[] changed = FilledSample(100, 255);
            detector.Observe(baseline, 0f);
            detector.MarkVisionRequested(0f);
            detector.MarkVisionAccepted(0f);
            detector.Observe(changed, 60f);
            detector.Observe(changed, 63f);
            detector.MarkVisionRequested(63f);
            detector.MarkVisionFailed();

            MaoScreenChangeResult retry = detector.Observe(changed, 123f);
            detector.MarkVisionRequested(123f);
            detector.MarkVisionAccepted(123f);
            MaoScreenChangeResult afterSuccess = detector.Observe(changed, 124f);

            Assert.IsTrue(retry.ShouldRequestVision);
            Assert.AreEqual(1f, retry.Difference, 0.001f);
            Assert.IsFalse(afterSuccess.ShouldRequestVision);
            Assert.AreEqual(0f, afterSuccess.Difference, 0.001f);
        }

        private static byte[] FilledSample(int length, byte value)
        {
            var sample = new byte[length];
            for (int index = 0; index < sample.Length; index++) sample[index] = value;
            return sample;
        }

        [Test]
        public void Curiosity_DecaysAndRisesOnNovelContext()
        {
            var curiosity = new MaoCuriosityState(0.7f);
            curiosity.Advance(30f, 0f, false, false, 0.02f);
            float decayed = curiosity.Value;
            curiosity.Advance(1f, 0.9f, true, true, 0.02f);

            Assert.Less(decayed, 0.7f);
            Assert.Greater(curiosity.Value, decayed);
            Assert.LessOrEqual(curiosity.Value, 1f);
        }

        [Test]
        public void Novelty_RepeatedContextBecomesFamiliarAndTrackerStaysBounded()
        {
            var tracker = new MaoContextNoveltyTracker(3);
            float first = tracker.Observe("code|project-a", 0f);
            float repeated = tracker.Observe("code|project-a", 10f);
            tracker.Observe("browser|docs", 20f);
            tracker.Observe("video|music", 30f);
            tracker.Observe("game|sandbox", 40f);

            Assert.Greater(first, repeated);
            Assert.AreEqual(3, tracker.Count);
        }

        [Test]
        public void ReactionHistory_BlocksRecentDuplicateButAllowsDifferentChannel()
        {
            var history = new MaoReactionHistory(8);
            history.Record(MaoInitiativeChannel.Speech, "question", "coding", "code", "hash-a", 100f);

            Assert.IsFalse(history.CanUse(MaoInitiativeChannel.Speech, "question", "coding", "code", "hash-a", 120f, 90f));
            Assert.IsTrue(history.CanUse(MaoInitiativeChannel.Physical, "question", "coding", "code", "hash-a", 120f, 90f));
            Assert.IsTrue(history.CanUse(MaoInitiativeChannel.Speech, "question", "coding", "code", "hash-a", 200f, 90f));
        }

        [Test]
        public void SpeechBudget_IsCappedAndRegeneratesOverTime()
        {
            var budget = new MaoSpeechBudget(5f, 180f, 0f);
            for (int index = 0; index < 5; index++) Assert.IsTrue(budget.TrySpend(index));

            Assert.IsFalse(budget.TrySpend(5f));
            Assert.IsTrue(budget.TrySpend(185f));
            Assert.LessOrEqual(budget.Available, 5f);
        }

        [TestCase("[emotion=happy;gesture=wave;attention=screen] Hello!", "Hello!", "happy", "wave", "screen")]
        [TestCase("[emotion=angry;gesture=pout;attention=user] Hừm.", "Hừm.", "angry", "pout", "user")]
        [TestCase("[emotion=sleepy;gesture=yawn;attention=away] Buồn ngủ quá.", "Buồn ngủ quá.", "sleepy", "yawn", "away")]
        [TestCase("[emotion=embarrassed;gesture=look_away;attention=user] Ơ kìa.", "Ơ kìa.", "embarrassed", "look_away", "user")]
        [TestCase("[emotion=sad;gesture=confused;attention=screen] Hmm.", "Hmm.", "sad", "confused", "screen")]
        [TestCase("[emotion=system;gesture=delete;attention=shell] Hello!", "Hello!", "neutral", "none", "user")]
        [TestCase("ordinary reply", "ordinary reply", "neutral", "none", "user")]
        public void SemanticTags_UseOnlyLocalAllowlist(
            string raw, string text, string emotion, string gesture, string attention)
        {
            MaoSemanticReaction reaction = MaoSemanticReactionParser.Parse(raw);

            Assert.AreEqual(text, reaction.DisplayText);
            Assert.AreEqual(emotion, reaction.Emotion);
            Assert.AreEqual(gesture, reaction.Gesture);
            Assert.AreEqual(attention, reaction.Attention);
        }

        [Test]
        public void SemanticTags_AddPoutForClearTeasingOnlyWhenModelStayedNeutral()
        {
            MaoSemanticReaction neutral = MaoSemanticReactionParser.Parse("Ừ, sao vậy?");
            MaoSemanticReaction inferred = MaoSemanticReactionParser.ApplyUserTone("Miku bị trêu nè =))", neutral);
            Assert.AreEqual("pout", inferred.Emotion);
            Assert.AreEqual("pout", inferred.Gesture);

            MaoSemanticReaction explicitHappy = MaoSemanticReactionParser.Parse(
                "[emotion=happy;gesture=wave;attention=user] Ok =))");
            MaoSemanticReaction preserved = MaoSemanticReactionParser.ApplyUserTone("Miku bị trêu", explicitHappy);
            Assert.AreEqual("happy", preserved.Emotion);
            Assert.AreEqual("wave", preserved.Gesture);
        }

        [TestCase("miku di chuyển tự do đi")]
        [TestCase("Miku đi theo con trỏ chuột nhé")]
        [TestCase("miku di chuyen tu do di")]
        [TestCase("/move follow")]
        public void MovementCommand_RecognizesCursorFollowRequests(string message)
        {
            MaoMovementCommand command;

            Assert.IsTrue(MaoMovementCommandParser.TryParse(message, out command));
            Assert.AreEqual(MaoMovementCommandAction.FollowCursor, command.Action);
        }

        [TestCase("miku dừng di chuyển đi")]
        [TestCase("Miku đứng yên nhé")]
        [TestCase("/move stop")]
        public void MovementCommand_RecognizesStopRequests(string message)
        {
            MaoMovementCommand command;

            Assert.IsTrue(MaoMovementCommandParser.TryParse(message, out command));
            Assert.AreEqual(MaoMovementCommandAction.Stop, command.Action);
        }

        [TestCase("miku chạy đi")]
        [TestCase("Miku chạy theo con trỏ nhé")]
        [TestCase("miku chay di")]
        [TestCase("miku chay ngang di")]
        [TestCase("/move run")]
        public void MovementCommand_RecognizesRunRequests(string message)
        {
            MaoMovementCommand command;

            Assert.IsTrue(MaoMovementCommandParser.TryParse(message, out command));
            Assert.AreEqual(MaoMovementCommandAction.RunFollowCursor, command.Action);
        }

        [TestCase("miku nhảy đi")]
        [TestCase("Miku nhay len nao")]
        [TestCase("/jump")]
        [TestCase("/move jump")]
        public void MovementCommand_DoesNotInterceptDisabledJumpRequests(string message)
        {
            MaoMovementCommand command;

            Assert.IsFalse(MaoMovementCommandParser.TryParse(message, out command));
            Assert.IsNull(command);
        }

        [Test]
        public void MovementCommand_DoesNotHijackOrdinaryConversation()
        {
            MaoMovementCommand command;

            Assert.IsFalse(MaoMovementCommandParser.TryParse("Hôm nay mình đi bộ ngoài công viên", out command));
            Assert.IsFalse(MaoMovementCommandParser.TryParse("con trỏ trong C# là gì?", out command));
            Assert.IsFalse(MaoMovementCommandParser.TryParse("mình vừa chạy bộ xong", out command));
            Assert.IsFalse(MaoMovementCommandParser.TryParse("game này có nút nhảy không?", out command));
        }

        [Test]
        public void MovementCommand_RecognizesGuitarPlayCommand()
        {
            MaoMovementCommand command;

            Assert.IsTrue(MaoMovementCommandParser.TryParse("/guitarplay", out command));
            Assert.AreEqual(MaoMovementCommandAction.PlayGuitar, command.Action);
        }

        [Test]
        public void MovementCommand_RecognizesGuitarStopCommand()
        {
            MaoMovementCommand command;

            Assert.IsTrue(MaoMovementCommandParser.TryParse("/guitarstop", out command));
            Assert.AreEqual(MaoMovementCommandAction.StopGuitar, command.Action);
        }

        [TestCase("guitarplay")]
        [TestCase("please /guitarplay now")]
        [TestCase("/guitar play")]
        public void MovementCommand_GuitarCommandsRequireExactSlashSyntax(string message)
        {
            MaoMovementCommand command;

            Assert.IsFalse(MaoMovementCommandParser.TryParse(message, out command));
            Assert.IsNull(command);
        }

        [TestCase("/angry", MaoMovementCommandAction.Angry)]
        [TestCase("/jabcross", MaoMovementCommandAction.JabCross)]
        [TestCase("/boxjump", MaoMovementCommandAction.BoxJump)]
        [TestCase("/jogcircle", MaoMovementCommandAction.JogInCircle)]
        [TestCase("/flyingkick", MaoMovementCommandAction.FlyingKick)]
        public void MovementCommand_ParsesIdleActionSlashCommands(string message, MaoMovementCommandAction expected)
        {
            Assert.IsTrue(MaoMovementCommandParser.TryParse(message, out MaoMovementCommand command));
            Assert.AreEqual(expected, command.Action);
        }

        [TestCase("Miku tức giận đi", MaoMovementCommandAction.Angry)]
        [TestCase("Miku đấm combo đi", MaoMovementCommandAction.JabCross)]
        [TestCase("Miku nhảy lên hộp đi", MaoMovementCommandAction.BoxJump)]
        [TestCase("Miku chạy vòng tròn đi", MaoMovementCommandAction.JogInCircle)]
        [TestCase("Miku đá bay đi", MaoMovementCommandAction.FlyingKick)]
        public void MovementCommand_ParsesVietnameseIdleActionRequests(string message, MaoMovementCommandAction expected)
        {
            Assert.IsTrue(MaoMovementCommandParser.TryParse(message, out MaoMovementCommand command));
            Assert.AreEqual(expected, command.Action);
        }

        [TestCase("/gravity", MaoMovementCommandAction.ToggleGravity)]
        [TestCase("/gravity on", MaoMovementCommandAction.EnableGravity)]
        [TestCase("/gravity off", MaoMovementCommandAction.DisableGravity)]
        [TestCase("/gravity status", MaoMovementCommandAction.GravityStatus)]
        [TestCase("Miku bật trọng lực đi", MaoMovementCommandAction.EnableGravity)]
        [TestCase("Miku tắt trọng lực đi", MaoMovementCommandAction.DisableGravity)]
        public void MovementCommand_ParsesGravityControls(string message, MaoMovementCommandAction expected)
        {
            Assert.IsTrue(MaoMovementCommandParser.TryParse(message, out MaoMovementCommand command));
            Assert.AreEqual(expected, command.Action);
        }

        [Test]
        public void GravityLanding_SelectsNearestWindowTopBelowTheAvatar()
        {
            var surfaces = new[]
            {
                new MaoGravitySurface(100f, 900f, 700f, 1),
                new MaoGravitySurface(100f, 900f, 420f, 2),
                new MaoGravitySurface(700f, 1100f, 360f, 3),
                new MaoGravitySurface(100f, 900f, 200f, 4)
            };

            MaoGravitySurface selected = MaoGravityLandingLogic.SelectSurface(
                500f, 300f, 1040f, surfaces);

            Assert.AreEqual(2, selected.Id);
            Assert.AreEqual(420f, selected.Top, 0.001f);
            Assert.IsFalse(selected.IsDesktopFloor);
        }

        [Test]
        public void GravityLanding_FallsBackToDesktopWorkAreaBottom()
        {
            var surfaces = new[] { new MaoGravitySurface(700f, 900f, 400f, 7) };

            MaoGravitySurface selected = MaoGravityLandingLogic.SelectSurface(
                300f, 250f, 1040f, surfaces);

            Assert.IsTrue(selected.IsDesktopFloor);
            Assert.AreEqual(1040f, selected.Top, 0.001f);
        }

        [Test]
        public void GravityMotion_AcceleratesWithoutOvershootingLandingSurface()
        {
            MaoGravityMotionStep first = MaoGravityLandingLogic.Advance(
                300f, 500f, 0f, 1800f, 2200f, 0.1f);
            MaoGravityMotionStep landing = MaoGravityLandingLogic.Advance(
                495f, 500f, 900f, 1800f, 2200f, 0.1f);

            Assert.Greater(first.DeltaY, 0f);
            Assert.Greater(first.Velocity, 0f);
            Assert.IsFalse(first.Landed);
            Assert.AreEqual(5f, landing.DeltaY, 0.001f);
            Assert.AreEqual(0f, landing.Velocity, 0.001f);
            Assert.IsTrue(landing.Landed);
        }

        [Test]
        public void GravityContact_UsesVisibleHumanoidBonesInsteadOfOversizedRendererBounds()
        {
            float contactY = MaoGravityInteractionLogic.SelectBodyContactY(
                800f,
                new[] { 210f, 240f, 230f },
                8f);

            Assert.AreEqual(248f, contactY, 0.001f);
        }

        [Test]
        public void GravityContact_FallsBackToRendererWhenHumanoidSamplesAreUnavailable()
        {
            float contactY = MaoGravityInteractionLogic.SelectBodyContactY(
                315f,
                new float[0],
                8f);

            Assert.AreEqual(315f, contactY, 0.001f);
        }

        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        public void GravityDragLock_OnlyBlocksInputWhileActivelyFalling(
            bool gravityEnabled,
            bool isFalling,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                MaoGravityInteractionLogic.ShouldBlockUserDrag(gravityEnabled, isFalling));
        }

        [TestCase(0f, 320f, MaoGravityLandingMode.HardLanding)]
        [TestCase(319.9f, 320f, MaoGravityLandingMode.HardLanding)]
        [TestCase(320f, 320f, MaoGravityLandingMode.FullFallRecovery)]
        [TestCase(480f, 320f, MaoGravityLandingMode.FullFallRecovery)]
        public void GravityLandingMode_UsesFullFallOnlyAtConfiguredHeight(
            float dropDistancePixels,
            float fullFallThresholdPixels,
            MaoGravityLandingMode expected)
        {
            Assert.AreEqual(
                expected,
                MaoGravityInteractionLogic.SelectLandingMode(
                    dropDistancePixels,
                    fullFallThresholdPixels));
        }

        [TestCase(976f, 1024f, 48f, true)]
        [TestCase(1024f, 1024f, 48f, true)]
        [TestCase(1072f, 1024f, 48f, true)]
        [TestCase(975.9f, 1024f, 48f, false)]
        [TestCase(1072.1f, 1024f, 48f, false)]
        public void GravityTaskbarSitting_UsesAvatarContactInsteadOfTransparentWindowBottom(
            float avatarContactY,
            float taskbarTop,
            float snapDistancePixels,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                MaoGravityInteractionLogic.ShouldPreferTaskbarSitting(
                    avatarContactY,
                    taskbarTop,
                    snapDistancePixels));
        }

        [TestCase(-1, 4, 2, 2)]
        [TestCase(2, 4, 2, 3)]
        [TestCase(3, 4, 3, 0)]
        [TestCase(0, 1, 0, 0)]
        [TestCase(0, 0, 0, -1)]
        public void GravityTaskbarSitPose_AvoidsImmediateRepeat(
            int previousIndex,
            int poseCount,
            int randomCandidate,
            int expected)
        {
            Assert.AreEqual(
                expected,
                MaoGravityInteractionLogic.SelectTaskbarSitPose(
                    previousIndex,
                    poseCount,
                    randomCandidate));
        }

        [TestCase(100f, 620f, 760f, 240f)]
        [TestCase(100f, 760f, 760f, 100f)]
        [TestCase(100f, 820f, 760f, 40f)]
        public void GravityTaskbarSeatAlignment_MovesWindowBySeatError(
            float currentWindowTop,
            float currentSeatY,
            float targetSeatY,
            float expectedWindowTop)
        {
            Assert.AreEqual(
                expectedWindowTop,
                MaoGravityInteractionLogic.AlignWindowTopToSeat(
                    currentWindowTop,
                    currentSeatY,
                    targetSeatY),
                0.001f);
        }

        [Test]
        public void CursorFollow_StopsInsideComfortBandAndApproachesFromTheSide()
        {
            MaoCursorFollowDecision close = MaoCursorFollowLogic.Decide(500f, 620f, 130f, 35f, 360f);
            MaoCursorFollowDecision farRight = MaoCursorFollowLogic.Decide(200f, 900f, 130f, 35f, 360f);
            MaoCursorFollowDecision farLeft = MaoCursorFollowLogic.Decide(900f, 200f, 130f, 35f, 360f);

            Assert.AreEqual(0, close.Direction);
            Assert.AreEqual(1, farRight.Direction);
            Assert.AreEqual(360f, farRight.Distance, 0.001f);
            Assert.AreEqual(570f, farRight.TargetDistance, 0.001f);
            Assert.AreEqual(-1, farLeft.Direction);
        }

        [TestCase(false, 420f, 180f, 1040f, MaoCursorFlightIntent.Fly)]
        [TestCase(false, 420f, 350f, 1040f, MaoCursorFlightIntent.GroundFollow)]
        [TestCase(true, 420f, 620f, 1040f, MaoCursorFlightIntent.Fly)]
        [TestCase(true, 420f, 970f, 1040f, MaoCursorFlightIntent.Land)]
        public void MacaronFlight_TakesOffForHighCursorAndStaysAirborneAcrossHorizontalMovement(
            bool airborne,
            float avatarHeadY,
            float cursorY,
            float taskbarTopY,
            MaoCursorFlightIntent expected)
        {
            MaoCursorFlightDecision decision = MaoCursorFlightLogic.Decide(
                airborne,
                avatarHeadY,
                cursorY,
                taskbarTopY,
                180f,
                110f);

            Assert.AreEqual(expected, decision.Intent);
        }

        [TestCase(100f, 300f, 400f, 500f, 700f, 200f, 0f, 80f, 700f, 400f, 0.25f, 200f, 200f)]
        [TestCase(300f, 650f, 300f, 900f, 500f, 900f, 0f, 80f, 700f, 400f, 1f, 500f, 700f)]
        public void MacaronFlight_FollowsCursorOnBothAxesWithoutPassingGround(
            float windowLeft,
            float windowTop,
            float avatarAnchorX,
            float avatarHeadY,
            float cursorX,
            float cursorY,
            float horizontalOffset,
            float headOffset,
            float groundWindowTop,
            float speed,
            float deltaTime,
            float expectedLeft,
            float expectedTop)
        {
            MaoCursorFlightPosition next = MaoCursorFlightLogic.StepWindowPosition(
                windowLeft,
                windowTop,
                avatarAnchorX,
                avatarHeadY,
                cursorX,
                cursorY,
                horizontalOffset,
                headOffset,
                groundWindowTop,
                speed,
                deltaTime);

            Assert.AreEqual(expectedLeft, next.Left, 0.001f);
            Assert.AreEqual(expectedTop, next.Top, 0.001f);
        }

        [TestCase(0, 500f, MaoLocomotionGait.Idle, 0f)]
        [TestCase(1, 180f, MaoLocomotionGait.Walk, -90f)]
        [TestCase(-1, 180f, MaoLocomotionGait.Walk, 90f)]
        [TestCase(1, 320f, MaoLocomotionGait.Run, -90f)]
        [TestCase(-1, 320f, MaoLocomotionGait.Run, 90f)]
        public void AdaptiveLocomotion_SelectsGaitAndSideFacingFromDistance(
            int direction,
            float targetDistance,
            MaoLocomotionGait expectedGait,
            float expectedYaw)
        {
            MaoAdaptiveLocomotionDecision decision = MaoAdaptiveLocomotionLogic.Decide(
                direction,
                targetDistance,
                240f,
                -90f,
                90f);

            Assert.AreEqual(expectedGait, decision.Gait);
            Assert.AreEqual(expectedYaw, decision.FacingYawDegrees, 0.001f);
        }

        [TestCase(0f, 1f, 1f, 0f, 90f)]
        [TestCase(0f, 1f, -1f, 0f, -90f)]
        [TestCase(0f, -1f, 1f, 0f, -90f)]
        [TestCase(1f, 0f, 1f, 0f, 0f)]
        public void FacingCalibration_UsesMeasuredAxesInsteadOfAssumingUnityForward(
            float forwardX,
            float forwardZ,
            float targetX,
            float targetZ,
            float expectedYaw)
        {
            float yaw = MaoFacingLogic.CalculateYawDegrees(forwardX, forwardZ, targetX, targetZ);

            Assert.AreEqual(expectedYaw, yaw, 0.001f);
        }

        [Test]
        public void FacingCalibration_AccountsForTheSceneParentsOneEightyDegreeRotation()
        {
            // In the scene's rotated Model parent, camera-right is local -X while
            // the avatar's neutral visual-forward is local +Z.
            float rightYaw = MaoFacingLogic.CalculateYawDegrees(0f, 1f, -1f, 0f);
            float leftYaw = MaoFacingLogic.CalculateYawDegrees(0f, 1f, 1f, 0f);

            Assert.AreEqual(-90f, rightYaw, 0.001f);
            Assert.AreEqual(90f, leftYaw, 0.001f);
        }

        [TestCase(170f, -170f, 25f, true)]
        [TestCase(-170f, 170f, 25f, true)]
        [TestCase(-90f, 90f, 8f, false)]
        [TestCase(84f, 90f, 8f, true)]
        public void FacingAlignment_UsesShortestWrappedAngle(
            float currentYaw,
            float targetYaw,
            float tolerance,
            bool expected)
        {
            Assert.AreEqual(expected, MaoFacingLogic.IsAligned(currentYaw, targetYaw, tolerance));
        }

        [TestCase(1, 0.75f, true)]
        [TestCase(-1, -0.75f, true)]
        [TestCase(1, -0.75f, false)]
        [TestCase(-1, 0.75f, false)]
        [TestCase(0, 0.75f, true)]
        public void FacingValidation_DetectsMoonwalkContradictions(
            int movementDirection,
            float visualForwardScreenX,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                MaoFacingLogic.DirectionsAgree(movementDirection, visualForwardScreenX, 0.15f));
        }

        [Test]
        public void RiggingLook_ClampsScreenTargetsAndInterest()
        {
            MaoRiggingLookPose pose = MaoRiggingLookLogic.Calculate(
                new Vector2(5000f, -2000f), 1000, 1000, 2f, 30f, 20f);

            Assert.IsTrue(pose.Valid);
            Assert.AreEqual(1f, pose.NormalizedX, 0.001f);
            Assert.AreEqual(-1f, pose.NormalizedY, 0.001f);
            Assert.AreEqual(30f, pose.Yaw, 0.001f);
            Assert.AreEqual(-20f, pose.Pitch, 0.001f);
            Assert.AreEqual(1f, pose.Weight, 0.001f);
        }

        [Test]
        public void RiggingLook_RejectsInvalidViewportOrCoordinates()
        {
            MaoRiggingLookPose invalidViewport = MaoRiggingLookLogic.Calculate(
                new Vector2(10f, 10f), 0, 1080, 1f, 30f, 20f);
            MaoRiggingLookPose invalidCoordinate = MaoRiggingLookLogic.Calculate(
                new Vector2(float.NaN, 10f), 1920, 1080, 1f, 30f, 20f);

            Assert.IsFalse(invalidViewport.Valid);
            Assert.IsFalse(invalidCoordinate.Valid);
            Assert.AreEqual(0f, invalidViewport.Weight, 0.001f);
            Assert.AreEqual(0f, invalidCoordinate.Weight, 0.001f);
        }

        [Test]
        public void DirectionChange_MustTurnBeforeTranslationResumes()
        {
            Assert.IsFalse(MaoFacingLogic.CanTranslate(-90f, 90f, 8f));
            Assert.IsTrue(MaoFacingLogic.CanTranslate(84f, 90f, 8f));
        }
    }
}
