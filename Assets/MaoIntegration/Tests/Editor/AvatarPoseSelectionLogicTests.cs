using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class AvatarPoseSelectionLogicTests
    {
        [Test]
        public void NextIndex_SelectsFirstPoseWhenNoneIsActive()
        {
            Assert.AreEqual(0, AvatarPoseSelectionLogic.NextIndex(-1, 12));
        }

        [Test]
        public void NextIndex_WrapsAfterLastPose()
        {
            Assert.AreEqual(0, AvatarPoseSelectionLogic.NextIndex(11, 12));
        }

        [Test]
        public void PreviousIndex_WrapsBeforeFirstPose()
        {
            Assert.AreEqual(11, AvatarPoseSelectionLogic.PreviousIndex(0, 12));
        }

        [Test]
        public void Selection_ReturnsNoneWhenNoPosesExist()
        {
            Assert.AreEqual(-1, AvatarPoseSelectionLogic.NextIndex(4, 0));
            Assert.AreEqual(-1, AvatarPoseSelectionLogic.PreviousIndex(4, 0));
        }

        [Test]
        public void ConsoleCycle_ReturnsToNormalAfterLastPose()
        {
            Assert.AreEqual(0, AvatarPoseSelectionLogic.ConsoleCycleIndex(-1, 12));
            Assert.AreEqual(6, AvatarPoseSelectionLogic.ConsoleCycleIndex(5, 12));
            Assert.AreEqual(-1, AvatarPoseSelectionLogic.ConsoleCycleIndex(11, 12));
        }

        [Test]
        public void ConsoleEntry_IsNeverHiddenByAvatarAnimationState()
        {
            Assert.IsEmpty(AvatarPoseSelectionLogic.ConsoleEntryHiddenAnimatorBools());
            Assert.IsEmpty(AvatarPoseSelectionLogic.ConsoleEntryHiddenAnimatorStates());
        }

        [Test]
        public void PoseLibrary_ListsAllImportedResourceNames()
        {
            CollectionAssert.AreEqual(new[]
            {
                "Cool_01", "Cool_02", "Cool_03",
                "Kawaii_01", "Kawaii_02", "Kawaii_03",
                "Female Standing 1", "Female Standing 2", "Female Crouch",
                "Sit_01", "Sit_02", "Sit_03",
                "Sleep_01", "Sieep_02", "Sieep_03"
            }, AvatarPoseSelectionLogic.PoseResourceNames());
        }

        [Test]
        public void PoseMenu_FirstPageShowsNineStandingChoicesAndSecondPageShowsTheRest()
        {
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
                AvatarPoseSelectionLogic.PoseIndicesForPage(0, 15));
            CollectionAssert.AreEqual(
                new[] { 9, 10, 11, 12, 13, 14 },
                AvatarPoseSelectionLogic.PoseIndicesForPage(1, 15));
            Assert.AreEqual(2, AvatarPoseSelectionLogic.PosePageCount(15));
        }

        [Test]
        public void PoseMenu_UsesCompactReadableLabelsForTheNewFbxPoses()
        {
            Assert.AreEqual("STAND 1", AvatarPoseSelectionLogic.PoseMenuLabel("Female Standing 1"));
            Assert.AreEqual("STAND 2", AvatarPoseSelectionLogic.PoseMenuLabel("Female Standing 2"));
            Assert.AreEqual("CROUCH", AvatarPoseSelectionLogic.PoseMenuLabel("Female Crouch"));
        }

        [Test]
        public void PoseLibrary_DeclaresTheThreeRuntimeFbxResources()
        {
            CollectionAssert.AreEqual(new[]
            {
                "MikuPoses/Fbx/Female Standing 1",
                "MikuPoses/Fbx/Female Standing 2",
                "MikuPoses/Fbx/Female Crouch"
            }, AvatarPoseSelectionLogic.RuntimeFbxPoseResourcePaths());
        }

        [Test]
        public void PoseMuscles_AcceptOnlyFramesMatchingTheHumanoidMuscleCount()
        {
            Assert.IsFalse(AvatarPoseSelectionLogic.HasExpectedMuscleCount(null, 95));
            Assert.IsFalse(AvatarPoseSelectionLogic.HasExpectedMuscleCount(new float[94], 95));
            Assert.IsTrue(AvatarPoseSelectionLogic.HasExpectedMuscleCount(new float[95], 95));
        }

        [Test]
        public void PosePlaybackGate_AllowsACompleteHumanoidPoseRequest()
        {
            Assert.AreEqual(
                PosePlaybackBlockReason.None,
                AvatarPoseSelectionLogic.EvaluatePlayback(
                    6, 15, true, true, true, true, false));
        }

        [Test]
        public void PosePlaybackGate_ReportsTheExactReasonASelectionCannotStart()
        {
            Assert.AreEqual(
                PosePlaybackBlockReason.IndexOutOfRange,
                AvatarPoseSelectionLogic.EvaluatePlayback(
                    15, 15, true, true, true, true, false));
            Assert.AreEqual(
                PosePlaybackBlockReason.AnimatorBusy,
                AvatarPoseSelectionLogic.EvaluatePlayback(
                    6, 15, true, true, true, true, true));
        }

        [Test]
        public void PoseTimeout_DoesNotExpireAManuallySelectedPoseWithoutADeadline()
        {
            Assert.IsFalse(AvatarPoseSelectionLogic.ShouldExpirePose(true, 0f, 120f));
            Assert.IsFalse(AvatarPoseSelectionLogic.ShouldExpirePose(true, 130f, 120f));
            Assert.IsTrue(AvatarPoseSelectionLogic.ShouldExpirePose(true, 110f, 120f));
        }
    }
}
