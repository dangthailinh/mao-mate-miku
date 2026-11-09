using UnityEngine;

namespace MaoIntegration
{
    public readonly struct MaoGuitarGripPair
    {
        public MaoGuitarGripPair(Vector3 left, Vector3 right)
        {
            Left = left;
            Right = right;
        }

        public Vector3 Left { get; }
        public Vector3 Right { get; }
    }

    public static class MaoGuitarPoseLogic
    {
        public static Vector3 BuildRaisedNeckAxis(
            Vector3 directionTowardFrettingHand,
            Vector3 characterUp,
            Vector3 fallbackHorizontal,
            float neckRaiseDegrees)
        {
            Vector3 up = characterUp.sqrMagnitude > 0.0001f
                ? characterUp.normalized
                : Vector3.up;
            Vector3 horizontal = Vector3.ProjectOnPlane(directionTowardFrettingHand, up);
            if (horizontal.sqrMagnitude < 0.0001f)
                horizontal = Vector3.ProjectOnPlane(fallbackHorizontal, up);
            if (horizontal.sqrMagnitude < 0.0001f)
                horizontal = Vector3.Cross(up, Vector3.forward);

            horizontal.Normalize();
            float radians = Mathf.Clamp(neckRaiseDegrees, 0f, 45f) * Mathf.Deg2Rad;
            return (horizontal * Mathf.Cos(radians) + up * Mathf.Sin(radians)).normalized;
        }

        public static MaoGuitarGripPair BuildGripPair(
            float gripSpan,
            float stringLineHeight,
            float stringLineDepth)
        {
            float halfSpan = Mathf.Max(0f, gripSpan) * 0.5f;
            Vector3 stringOffset = new Vector3(0f, stringLineHeight, stringLineDepth);
            return new MaoGuitarGripPair(
                stringOffset + Vector3.right * halfSpan,
                stringOffset - Vector3.right * halfSpan);
        }
    }
}
