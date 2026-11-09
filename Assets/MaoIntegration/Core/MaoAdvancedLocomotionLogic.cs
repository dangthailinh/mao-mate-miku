using System;

namespace MaoIntegration
{
    public enum MaoGroundLocomotionState
    {
        Idle,
        Walking,
        Running,
        TurningLeft,
        TurningRight,
        StrafeWalkLeft,
        StrafeWalkRight,
        StrafeLeft,
        StrafeRight,
        Jumping,
        Stopping,
        Dragged,
        InteractionLocked
    }

    public enum MaoMovementAuthority
    {
        Idle,
        Autonomy,
        MouseFollow,
        UserCommand,
        InteractionLock
    }

    public static class MaoAdvancedLocomotionLogic
    {
        public static MaoGroundLocomotionState SelectDistanceState(
            MaoGroundLocomotionState current,
            float distance,
            float stopEnterDistance,
            float stopExitDistance,
            float runExitDistance,
            float runEnterDistance)
        {
            float value = Math.Abs(distance);
            float stopEnter = Math.Max(0f, stopEnterDistance);
            float stopExit = Math.Max(stopEnter, stopExitDistance);
            float runExit = Math.Max(stopExit, runExitDistance);
            float runEnter = Math.Max(runExit, runEnterDistance);

            if (current == MaoGroundLocomotionState.Running)
                return value < runExit
                    ? value <= stopEnter ? MaoGroundLocomotionState.Idle : MaoGroundLocomotionState.Walking
                    : MaoGroundLocomotionState.Running;

            bool wasMoving = IsMovingState(current);
            if (wasMoving && value <= stopEnter) return MaoGroundLocomotionState.Idle;
            if (!wasMoving && value < stopExit) return MaoGroundLocomotionState.Idle;
            if (value >= runEnter) return MaoGroundLocomotionState.Running;
            return MaoGroundLocomotionState.Walking;
        }

        public static float FilterTarget(float current, float target, float sharpness, float deltaTime)
        {
            float rate = Math.Max(0f, sharpness);
            float time = Math.Max(0f, deltaTime);
            if (rate <= 0f || time <= 0f) return current;
            float blend = 1f - (float)Math.Exp(-rate * time);
            return current + ((target - current) * blend);
        }

        public static float StepSpeed(
            float currentSpeed,
            float targetSpeed,
            float acceleration,
            float deceleration,
            float deltaTime)
        {
            float current = Math.Max(0f, currentSpeed);
            float target = Math.Max(0f, targetSpeed);
            float rate = target > current ? Math.Max(0f, acceleration) : Math.Max(0f, deceleration);
            float maximumDelta = rate * Math.Max(0f, deltaTime);
            if (Math.Abs(target - current) <= maximumDelta) return target;
            return current + Math.Sign(target - current) * maximumDelta;
        }

        public static MaoGroundLocomotionState SelectDirectionalState(
            float signedAngleDegrees,
            MaoGroundLocomotionState gait,
            float strafeAngleDegrees,
            float hardTurnAngleDegrees)
        {
            float angle = Math.Abs(WrapAngle(signedAngleDegrees));
            if (angle >= Math.Max(0f, hardTurnAngleDegrees))
                return signedAngleDegrees < 0f
                    ? MaoGroundLocomotionState.TurningLeft
                    : MaoGroundLocomotionState.TurningRight;

            if (angle < Math.Max(0f, strafeAngleDegrees))
                return gait == MaoGroundLocomotionState.Running
                    ? MaoGroundLocomotionState.Running
                    : gait == MaoGroundLocomotionState.Idle
                        ? MaoGroundLocomotionState.Idle
                        : MaoGroundLocomotionState.Walking;

            bool left = signedAngleDegrees < 0f;
            if (gait == MaoGroundLocomotionState.Running)
                return left ? MaoGroundLocomotionState.StrafeLeft : MaoGroundLocomotionState.StrafeRight;
            return left ? MaoGroundLocomotionState.StrafeWalkLeft : MaoGroundLocomotionState.StrafeWalkRight;
        }

        public static MaoMovementAuthority ResolveAuthority(
            bool interactionLocked,
            bool userCommandActive,
            bool mouseFollowActive,
            bool autonomyActive)
        {
            if (interactionLocked) return MaoMovementAuthority.InteractionLock;
            if (userCommandActive) return MaoMovementAuthority.UserCommand;
            if (mouseFollowActive) return MaoMovementAuthority.MouseFollow;
            return autonomyActive ? MaoMovementAuthority.Autonomy : MaoMovementAuthority.Idle;
        }

        public static bool CanStartGroundJump(
            float now,
            float lastJumpAt,
            float cooldown,
            bool grounded,
            bool moving,
            bool interactionLocked)
        {
            return !interactionLocked && grounded && moving &&
                   now - lastJumpAt >= Math.Max(0f, cooldown);
        }

        public static bool RequiresTurnStop(float angleDegrees, float hardTurnAngleDegrees)
        {
            return Math.Abs(WrapAngle(angleDegrees)) >= Math.Max(0f, hardTurnAngleDegrees);
        }

        public static bool CanUpdateMovement(
            bool followEnabled,
            bool interactionLocked,
            bool baseIdle)
        {
            if (interactionLocked) return false;
            return followEnabled || baseIdle;
        }

        public static bool ShouldIncludeRenderer(bool rendererEnabled, bool activeInHierarchy)
        {
            return rendererEnabled && activeInHierarchy;
        }

        public static bool ShouldUseTaskbarSitting(
            bool nearTaskbar,
            bool externalSitRequested,
            bool externalWindowSitRequested,
            bool groundMovementRequested,
            bool macaronFlightRequested)
        {
            if (externalWindowSitRequested) return false;
            if (macaronFlightRequested) return true;
            if (groundMovementRequested) return false;
            return externalSitRequested || nearTaskbar;
        }

        public static bool IsMovingState(MaoGroundLocomotionState state)
        {
            return state == MaoGroundLocomotionState.Walking ||
                   state == MaoGroundLocomotionState.Running ||
                   state == MaoGroundLocomotionState.StrafeWalkLeft ||
                   state == MaoGroundLocomotionState.StrafeWalkRight ||
                   state == MaoGroundLocomotionState.StrafeLeft ||
                   state == MaoGroundLocomotionState.StrafeRight ||
                   state == MaoGroundLocomotionState.Jumping;
        }

        private static float WrapAngle(float degrees)
        {
            float value = degrees % 360f;
            if (value > 180f) value -= 360f;
            if (value < -180f) value += 360f;
            return value;
        }
    }
}
