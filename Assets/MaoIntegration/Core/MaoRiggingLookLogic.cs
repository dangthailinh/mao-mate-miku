using System;
using UnityEngine;

/// <summary>
/// Pure, bounded look-target math shared by runtime rigging and editor tests.
/// Screen coordinates are measured from the bottom-left, matching Unity's input
/// and camera APIs. The runtime converts pitch to the model's local rotation.
/// </summary>
public readonly struct MaoRiggingLookPose
{
    public readonly bool Valid;
    public readonly float NormalizedX;
    public readonly float NormalizedY;
    public readonly float Yaw;
    public readonly float Pitch;
    public readonly float Weight;

    public MaoRiggingLookPose(bool valid, float normalizedX, float normalizedY, float yaw, float pitch, float weight)
    {
        Valid = valid;
        NormalizedX = normalizedX;
        NormalizedY = normalizedY;
        Yaw = yaw;
        Pitch = pitch;
        Weight = weight;
    }
}

public static class MaoRiggingLookLogic
{
    public static MaoRiggingLookPose Calculate(
        Vector2 targetScreen,
        int screenWidth,
        int screenHeight,
        float interest,
        float yawLimit,
        float pitchLimit)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || !IsFinite(targetScreen.x) || !IsFinite(targetScreen.y))
            return new MaoRiggingLookPose(false, 0f, 0f, 0f, 0f, 0f);

        float normalizedX = Mathf.Clamp((targetScreen.x / screenWidth - 0.5f) * 2f, -1f, 1f);
        float normalizedY = Mathf.Clamp((targetScreen.y / screenHeight - 0.5f) * 2f, -1f, 1f);
        float safeInterest = Mathf.Clamp01(IsFinite(interest) ? interest : 0f);
        float safeYawLimit = Mathf.Clamp(IsFinite(yawLimit) ? Mathf.Abs(yawLimit) : 0f, 0f, 90f);
        float safePitchLimit = Mathf.Clamp(IsFinite(pitchLimit) ? Mathf.Abs(pitchLimit) : 0f, 0f, 90f);

        return new MaoRiggingLookPose(
            true,
            normalizedX,
            normalizedY,
            normalizedX * safeYawLimit * safeInterest,
            normalizedY * safePitchLimit * safeInterest,
            safeInterest);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
