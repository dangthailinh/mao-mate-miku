using UnityEngine;

namespace MaoIntegration
{
    public static class AvatarInteractionGestureLogic
    {
        public static bool ShouldResolveSingleClick(float releasedAt, float now, float doubleClickInterval)
        {
            return now - releasedAt > Mathf.Max(0f, doubleClickInterval);
        }

        public static bool IsDoubleClick(float firstReleasedAt, float secondReleasedAt, float doubleClickInterval)
        {
            float elapsed = secondReleasedAt - firstReleasedAt;
            return elapsed >= 0f && elapsed <= Mathf.Max(0f, doubleClickInterval);
        }

        public static bool HasReachedDragThreshold(Vector2 pointerDown, Vector2 current, float threshold)
        {
            float safeThreshold = Mathf.Max(0f, threshold);
            return (current - pointerDown).sqrMagnitude >= safeThreshold * safeThreshold;
        }

        public static bool CanPreempt(int activePriority, int requestedPriority)
        {
            return requestedPriority >= activePriority;
        }
    }

    public sealed class AvatarPatGestureDetector
    {
        bool hasLast;
        Vector2 lastPosition;
        Vector2 lastMove;
        float lastAngle;
        float circleAccumulation;
        float wiggleDistanceAccumulation;
        int wiggleDirectionChanges;
        float expiresAt;

        public bool AddSample(
            Vector2 center,
            Vector2 pointer,
            float now,
            float requiredCircleDegrees,
            float minimumRadius,
            float resetTime,
            float requiredWiggleDistance,
            int requiredDirectionChanges,
            float minimumSampleDistance)
        {
            if (hasLast && now >= expiresAt) Reset();

            if (!hasLast)
            {
                hasLast = true;
                lastPosition = pointer;
                lastMove = Vector2.zero;
                lastAngle = Angle(center, pointer);
                expiresAt = now + Mathf.Max(0f, resetTime);
                return false;
            }

            Vector2 delta = pointer - lastPosition;
            float safeMinimumMove = Mathf.Max(0f, minimumSampleDistance);
            if (delta.sqrMagnitude < safeMinimumMove * safeMinimumMove) return false;

            float angle = Angle(center, pointer);
            if ((lastPosition - center).magnitude >= minimumRadius || (pointer - center).magnitude >= minimumRadius)
                circleAccumulation += Mathf.Abs(Mathf.DeltaAngle(lastAngle, angle));

            if (lastMove.sqrMagnitude > 0f && Vector2.Dot(delta.normalized, lastMove.normalized) < -0.2f)
                wiggleDirectionChanges++;

            wiggleDistanceAccumulation += delta.magnitude;
            lastPosition = pointer;
            lastMove = delta;
            lastAngle = angle;
            expiresAt = now + Mathf.Max(0f, resetTime);

            bool circleComplete = circleAccumulation >= Mathf.Max(0f, requiredCircleDegrees);
            bool wiggleComplete = wiggleDistanceAccumulation >= Mathf.Max(0f, requiredWiggleDistance)
                                  && wiggleDirectionChanges >= Mathf.Max(0, requiredDirectionChanges);
            if (!circleComplete && !wiggleComplete) return false;

            Reset();
            return true;
        }

        public void Reset()
        {
            hasLast = false;
            lastPosition = Vector2.zero;
            lastMove = Vector2.zero;
            lastAngle = 0f;
            circleAccumulation = 0f;
            wiggleDistanceAccumulation = 0f;
            wiggleDirectionChanges = 0;
            expiresAt = 0f;
        }

        static float Angle(Vector2 center, Vector2 pointer)
        {
            Vector2 offset = pointer - center;
            return Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
        }
    }
}
