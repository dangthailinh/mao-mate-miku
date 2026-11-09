using System;

namespace MaoIntegration
{
    /// <summary>
    /// Logical owner of the avatar's full-body animation channel.  Face and
    /// attention layers remain independent, but only one full-body producer is
    /// allowed to be authoritative at a time.
    /// </summary>
    public enum MaoAnimationOwner
    {
        None = 0,
        Idle = 10,
        Interaction = 20,
        Locomotion = 30,
        CursorFlight = 40,
        Pose = 50,
        Seating = 55,
        Guitar = 60,
        CustomDance = 62,
        BigScreen = 64,
        Sleep = 68,
        Gravity = 70
    }

    /// <summary>
    /// Small deterministic arbiter used by runtime code and editor tests.  It
    /// deliberately has no Unity dependency so ownership decisions are easy to
    /// test and cannot depend on frame rate or Animator implementation details.
    /// </summary>
    public sealed class MaoAnimationOwnership
    {
        private MaoAnimationOwner owner;
        private float expiresAt;

        public MaoAnimationOwner CurrentOwner => owner;

        public float Remaining(float now)
        {
            ExpireIfNeeded(now);
            if (owner == MaoAnimationOwner.None) return 0f;
            if (float.IsPositiveInfinity(expiresAt)) return float.PositiveInfinity;
            return Math.Max(0f, expiresAt - now);
        }

        public bool IsOwnedBy(MaoAnimationOwner requested, float now)
        {
            ExpireIfNeeded(now);
            return requested != MaoAnimationOwner.None && owner == requested;
        }

        public bool CanAcquire(MaoAnimationOwner requested, float now)
        {
            ExpireIfNeeded(now);
            if (requested == MaoAnimationOwner.None) return false;
            if (owner == MaoAnimationOwner.None || owner == requested) return true;
            return PriorityOf(requested) > PriorityOf(owner);
        }

        public bool TryAcquire(MaoAnimationOwner requested, float now, float holdSeconds)
        {
            if (!CanAcquire(requested, now)) return false;

            float duration = NormalizeDuration(holdSeconds);
            if (owner == requested)
            {
                // A repeated request may extend an existing lease, but never
                // shorten an exclusive operation that is already in progress.
                float requestedExpiry = AddDuration(now, duration);
                if (float.IsPositiveInfinity(expiresAt) || requestedExpiry <= expiresAt) return true;
                expiresAt = requestedExpiry;
                return true;
            }

            owner = requested;
            expiresAt = AddDuration(now, duration);
            return true;
        }

        public bool Release(MaoAnimationOwner requested)
        {
            if (requested == MaoAnimationOwner.None || owner != requested) return false;
            owner = MaoAnimationOwner.None;
            expiresAt = 0f;
            return true;
        }

        public void ForceRelease()
        {
            owner = MaoAnimationOwner.None;
            expiresAt = 0f;
        }

        public static int PriorityOf(MaoAnimationOwner value)
        {
            return (int)value;
        }

        private void ExpireIfNeeded(float now)
        {
            if (owner != MaoAnimationOwner.None && !float.IsPositiveInfinity(expiresAt) && now >= expiresAt)
                ForceRelease();
        }

        private static float NormalizeDuration(float seconds)
        {
            if (float.IsPositiveInfinity(seconds)) return float.PositiveInfinity;
            if (float.IsNaN(seconds) || seconds <= 0f) return 0.01f;
            return Math.Min(seconds, 86400f);
        }

        private static float AddDuration(float now, float duration)
        {
            if (float.IsPositiveInfinity(duration)) return float.PositiveInfinity;
            if (float.IsNaN(now) || float.IsInfinity(now)) now = 0f;
            return now + duration;
        }
    }
}
