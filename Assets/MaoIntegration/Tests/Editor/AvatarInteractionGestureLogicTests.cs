using NUnit.Framework;
using UnityEngine;

namespace MaoIntegration.Tests
{
    public sealed class AvatarInteractionGestureLogicTests
    {
        [Test]
        public void SingleClick_WaitsUntilDoubleClickWindowExpires()
        {
            Assert.IsFalse(AvatarInteractionGestureLogic.ShouldResolveSingleClick(10f, 10.20f, 0.30f));
            Assert.IsTrue(AvatarInteractionGestureLogic.ShouldResolveSingleClick(10f, 10.31f, 0.30f));
        }

        [Test]
        public void DoubleClick_OnlyMatchesClicksInsideConfiguredInterval()
        {
            Assert.IsTrue(AvatarInteractionGestureLogic.IsDoubleClick(5f, 5.25f, 0.30f));
            Assert.IsFalse(AvatarInteractionGestureLogic.IsDoubleClick(5f, 5.31f, 0.30f));
        }

        [Test]
        public void Drag_RequiresConfiguredPointerMovement()
        {
            Vector2 start = new Vector2(100f, 100f);

            Assert.IsFalse(AvatarInteractionGestureLogic.HasReachedDragThreshold(start, new Vector2(104f, 103f), 8f));
            Assert.IsTrue(AvatarInteractionGestureLogic.HasReachedDragThreshold(start, new Vector2(109f, 100f), 8f));
        }

        [Test]
        public void HigherPriorityInteraction_PreemptsLowerPriorityButNotConversely()
        {
            Assert.IsTrue(AvatarInteractionGestureLogic.CanPreempt(10, 60));
            Assert.IsFalse(AvatarInteractionGestureLogic.CanPreempt(60, 30));
        }

        [Test]
        public void HeadPat_IgnoresTinyMovementAndAcceptsDeliberateWiggle()
        {
            var detector = new AvatarPatGestureDetector();
            Vector2 center = Vector2.zero;

            Assert.IsFalse(detector.AddSample(center, new Vector2(20f, 0f), 0f, 540f, 12f, 0.6f, 35f, 2, 4f));
            Assert.IsFalse(detector.AddSample(center, new Vector2(21f, 0f), 0.1f, 540f, 12f, 0.6f, 35f, 2, 4f));
            Assert.IsFalse(detector.AddSample(center, new Vector2(35f, 0f), 0.2f, 540f, 12f, 0.6f, 35f, 2, 4f));
            Assert.IsFalse(detector.AddSample(center, new Vector2(15f, 0f), 0.3f, 540f, 12f, 0.6f, 35f, 2, 4f));
            Assert.IsTrue(detector.AddSample(center, new Vector2(35f, 0f), 0.4f, 540f, 12f, 0.6f, 35f, 2, 4f));
        }

        [Test]
        public void GuitarNeckAxis_RaisesTowardFrettingHandAtConfiguredAngle()
        {
            Vector3 axis = MaoGuitarPoseLogic.BuildRaisedNeckAxis(
                new Vector3(1f, 0.01f, 0f),
                Vector3.up,
                Vector3.right,
                18f);

            Assert.Greater(Vector3.Dot(axis, Vector3.up), 0.30f);
            Assert.Greater(Vector3.Dot(axis, Vector3.right), 0.94f);
        }

        [Test]
        public void GuitarGripPair_SpansTheNeckAxisAndSitsOnTheStringLine()
        {
            MaoGuitarGripPair grips = MaoGuitarPoseLogic.BuildGripPair(0.20f, 0.04f, -0.01f);

            Assert.Less(Vector3.Distance(new Vector3(0.10f, 0.04f, -0.01f), grips.Left), 0.0001f);
            Assert.Less(Vector3.Distance(new Vector3(-0.10f, 0.04f, -0.01f), grips.Right), 0.0001f);
        }
    }
}
