using System;
using System.Collections.Generic;

namespace MaoIntegration
{
    public enum MaoGravityLandingMode
    {
        HardLanding,
        FullFallRecovery
    }

    public readonly struct MaoGravitySurface
    {
        public MaoGravitySurface(float left, float right, float top, long id, bool isDesktopFloor = false)
        {
            Left = Math.Min(left, right);
            Right = Math.Max(left, right);
            Top = top;
            Id = id;
            IsDesktopFloor = isDesktopFloor;
        }

        public float Left { get; }
        public float Right { get; }
        public float Top { get; }
        public long Id { get; }
        public bool IsDesktopFloor { get; }
        public bool ContainsX(float x) => x >= Left && x <= Right;
    }

    public readonly struct MaoGravityMotionStep
    {
        public MaoGravityMotionStep(float deltaY, float velocity, bool landed)
        {
            DeltaY = deltaY;
            Velocity = velocity;
            Landed = landed;
        }

        public float DeltaY { get; }
        public float Velocity { get; }
        public bool Landed { get; }
    }

    public static class MaoGravityLandingLogic
    {
        private const float SurfaceTolerancePixels = 2f;

        public static MaoGravitySurface SelectSurface(
            float contactX,
            float contactY,
            float desktopFloorTop,
            IReadOnlyList<MaoGravitySurface> surfaces)
        {
            MaoGravitySurface selected = new MaoGravitySurface(
                float.NegativeInfinity,
                float.PositiveInfinity,
                desktopFloorTop,
                0,
                true);

            if (surfaces == null) return selected;
            for (int index = 0; index < surfaces.Count; index++)
            {
                MaoGravitySurface candidate = surfaces[index];
                if (!candidate.ContainsX(contactX)) continue;
                if (candidate.Top < contactY - SurfaceTolerancePixels) continue;
                if (candidate.Top >= selected.Top) continue;
                selected = candidate;
            }
            return selected;
        }

        public static MaoGravityMotionStep Advance(
            float contactY,
            float targetTop,
            float velocity,
            float acceleration,
            float maximumSpeed,
            float deltaTime)
        {
            float remaining = Math.Max(0f, targetTop - contactY);
            if (remaining <= SurfaceTolerancePixels)
                return new MaoGravityMotionStep(remaining, 0f, true);

            float nextVelocity = Math.Min(
                Math.Max(0f, maximumSpeed),
                Math.Max(0f, velocity) + Math.Max(0f, acceleration) * Math.Max(0f, deltaTime));
            float delta = Math.Min(remaining, nextVelocity * Math.Max(0f, deltaTime));
            bool landed = remaining - delta <= SurfaceTolerancePixels;
            if (landed)
                return new MaoGravityMotionStep(remaining, 0f, true);
            return new MaoGravityMotionStep(delta, nextVelocity, false);
        }
    }

    public static class MaoGravityInteractionLogic
    {
        public static float SelectBodyContactY(
            float rendererContactY,
            IReadOnlyList<float> humanoidContactYs,
            float surfacePaddingPixels)
        {
            bool foundHumanoidContact = false;
            float lowestHumanoidContact = float.NegativeInfinity;
            if (humanoidContactYs != null)
            {
                for (int index = 0; index < humanoidContactYs.Count; index++)
                {
                    float candidate = humanoidContactYs[index];
                    if (float.IsNaN(candidate) || float.IsInfinity(candidate)) continue;
                    foundHumanoidContact = true;
                    lowestHumanoidContact = Math.Max(lowestHumanoidContact, candidate);
                }
            }

            return foundHumanoidContact
                ? lowestHumanoidContact + Math.Max(0f, surfacePaddingPixels)
                : rendererContactY;
        }

        public static bool ShouldBlockUserDrag(bool gravityEnabled, bool isFalling)
        {
            return gravityEnabled && isFalling;
        }

        public static MaoGravityLandingMode SelectLandingMode(
            float dropDistancePixels,
            float fullFallThresholdPixels)
        {
            return Math.Max(0f, dropDistancePixels) >= Math.Max(0f, fullFallThresholdPixels)
                ? MaoGravityLandingMode.FullFallRecovery
                : MaoGravityLandingMode.HardLanding;
        }

        public static bool ShouldPreferTaskbarSitting(
            float avatarContactY,
            float taskbarTop,
            float snapDistancePixels)
        {
            return Math.Abs(avatarContactY - taskbarTop) <= Math.Max(0f, snapDistancePixels);
        }

        public static int SelectTaskbarSitPose(
            int previousIndex,
            int poseCount,
            int randomCandidate)
        {
            if (poseCount <= 0) return -1;
            int candidate = ((randomCandidate % poseCount) + poseCount) % poseCount;
            return poseCount > 1 && candidate == previousIndex
                ? (candidate + 1) % poseCount
                : candidate;
        }

        public static float AlignWindowTopToSeat(
            float currentWindowTop,
            float currentSeatY,
            float targetSeatY)
        {
            return currentWindowTop + targetSeatY - currentSeatY;
        }
    }
}
