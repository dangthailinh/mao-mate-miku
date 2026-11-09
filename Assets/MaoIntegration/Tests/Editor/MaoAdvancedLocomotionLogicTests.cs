using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class MaoAdvancedLocomotionLogicTests
    {
        [TestCase(MaoGroundLocomotionState.Idle, 39f, MaoGroundLocomotionState.Idle)]
        [TestCase(MaoGroundLocomotionState.Idle, 56f, MaoGroundLocomotionState.Walking)]
        [TestCase(MaoGroundLocomotionState.Walking, 35f, MaoGroundLocomotionState.Idle)]
        [TestCase(MaoGroundLocomotionState.Walking, 230f, MaoGroundLocomotionState.Walking)]
        [TestCase(MaoGroundLocomotionState.Walking, 301f, MaoGroundLocomotionState.Running)]
        [TestCase(MaoGroundLocomotionState.Running, 221f, MaoGroundLocomotionState.Running)]
        [TestCase(MaoGroundLocomotionState.Running, 209f, MaoGroundLocomotionState.Walking)]
        public void DistanceZones_UseSeparateEnterAndExitThresholds(
            MaoGroundLocomotionState current,
            float distance,
            MaoGroundLocomotionState expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.SelectDistanceState(
                current, distance, 40f, 55f, 210f, 300f));
        }

        [Test]
        public void CursorFiltering_MovesTowardTargetWithoutOvershooting()
        {
            float first = MaoAdvancedLocomotionLogic.FilterTarget(100f, 500f, 8f, 0.05f);
            float second = MaoAdvancedLocomotionLogic.FilterTarget(first, 500f, 8f, 0.05f);

            Assert.Greater(first, 100f);
            Assert.Less(first, 500f);
            Assert.Greater(second, first);
            Assert.Less(second, 500f);
        }

        [TestCase(0f, 400f, 600f, 900f, 0.1f, 60f)]
        [TestCase(300f, 400f, 600f, 900f, 0.1f, 360f)]
        [TestCase(400f, 0f, 600f, 900f, 0.1f, 310f)]
        [TestCase(40f, 0f, 600f, 900f, 0.1f, 0f)]
        public void SpeedStep_UsesAccelerationAndStrongerDeceleration(
            float current,
            float target,
            float acceleration,
            float deceleration,
            float deltaTime,
            float expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.StepSpeed(
                current, target, acceleration, deceleration, deltaTime), 0.001f);
        }

        [TestCase(10f, MaoGroundLocomotionState.Walking, MaoGroundLocomotionState.Walking)]
        [TestCase(-45f, MaoGroundLocomotionState.Walking, MaoGroundLocomotionState.StrafeWalkLeft)]
        [TestCase(55f, MaoGroundLocomotionState.Running, MaoGroundLocomotionState.StrafeRight)]
        [TestCase(-145f, MaoGroundLocomotionState.Running, MaoGroundLocomotionState.TurningLeft)]
        [TestCase(145f, MaoGroundLocomotionState.Walking, MaoGroundLocomotionState.TurningRight)]
        public void DirectionSelection_UsesForwardStrafeAndHardTurnZones(
            float signedAngle,
            MaoGroundLocomotionState gait,
            MaoGroundLocomotionState expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.SelectDirectionalState(
                signedAngle, gait, 32f, 115f));
        }

        [TestCase(false, false, false, false, MaoMovementAuthority.Idle)]
        [TestCase(false, false, false, true, MaoMovementAuthority.Autonomy)]
        [TestCase(false, false, true, true, MaoMovementAuthority.MouseFollow)]
        [TestCase(false, true, true, true, MaoMovementAuthority.UserCommand)]
        [TestCase(true, true, true, true, MaoMovementAuthority.InteractionLock)]
        public void MovementPriority_IsDeterministic(
            bool locked,
            bool userCommand,
            bool mouseFollow,
            bool autonomy,
            MaoMovementAuthority expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.ResolveAuthority(
                locked, userCommand, mouseFollow, autonomy));
        }

        [TestCase(12f, 5f, 6f, true, true, false, true)]
        [TestCase(10.9f, 5f, 6f, true, true, false, false)]
        [TestCase(12f, 5f, 6f, false, true, false, false)]
        [TestCase(12f, 5f, 6f, true, false, false, false)]
        [TestCase(12f, 5f, 6f, true, true, true, false)]
        public void GroundJump_RequiresCooldownGroundMovementAndNoLock(
            float now,
            float lastJump,
            float cooldown,
            bool grounded,
            bool moving,
            bool locked,
            bool expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.CanStartGroundJump(
                now, lastJump, cooldown, grounded, moving, locked));
        }

        [TestCase(90f, false)]
        [TestCase(114.9f, false)]
        [TestCase(115f, true)]
        [TestCase(179f, true)]
        public void HardTurns_DecelerateBeforeFacingReversal(float angle, bool expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.RequiresTurnStop(angle, 115f));
        }

        [TestCase(true, false, false, true)]
        [TestCase(true, false, true, true)]
        [TestCase(true, true, true, false)]
        [TestCase(false, false, true, true)]
        [TestCase(false, false, false, false)]
        public void FollowAuthority_DoesNotWaitForPassiveBaseIdle(
            bool followEnabled,
            bool interactionLocked,
            bool baseIdle,
            bool expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.CanUpdateMovement(
                followEnabled, interactionLocked, baseIdle));
        }

        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        public void AvatarBounds_IgnoreDisabledOrInactiveProps(
            bool rendererEnabled,
            bool activeInHierarchy,
            bool expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.ShouldIncludeRenderer(
                rendererEnabled, activeInHierarchy));
        }

        [TestCase(true, false, false, false, false, true)]
        [TestCase(true, false, false, true, false, false)]
        [TestCase(true, false, false, true, true, true)]
        [TestCase(false, true, false, false, false, true)]
        [TestCase(true, false, true, false, true, false)]
        public void TaskbarVisualPolicy_SeparatesGroundMovementFromMacaronFlight(
            bool nearTaskbar,
            bool externalSit,
            bool externalWindowSit,
            bool groundMovement,
            bool macaronFlight,
            bool expected)
        {
            Assert.AreEqual(expected, MaoAdvancedLocomotionLogic.ShouldUseTaskbarSitting(
                nearTaskbar, externalSit, externalWindowSit, groundMovement, macaronFlight));
        }
    }
}
