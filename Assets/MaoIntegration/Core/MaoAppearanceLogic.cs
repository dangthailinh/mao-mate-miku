using System;
using UnityEngine;

namespace MaoIntegration
{
    public enum MaoAppearancePart { Hair = 0, HairColor = 1, Outfit = 2, Face = 3, EyeColor = 4, CatEars = 5, CatTail = 6 }

    [Serializable]
    public sealed class MaoAppearanceProfile
    {
        public int version = 1;
        public int hair, hairColor, outfit, face, eyeColor;
        // Additive fields default to off when loading an older profile.
        public int catEars, catTail;
        public bool IsOriginal => hair == 0 && hairColor == 0 && outfit == 0 && face == 0 && eyeColor == 0 &&
                                  catEars == 0 && catTail == 0;

        public static int Count(MaoAppearancePart part)
        {
            switch (part)
            {
                case MaoAppearancePart.CatEars:
                case MaoAppearancePart.CatTail: return 2;
                case MaoAppearancePart.Outfit: return 4;
                case MaoAppearancePart.Hair:
                case MaoAppearancePart.HairColor:
                case MaoAppearancePart.Face:
                case MaoAppearancePart.EyeColor: return 3;
                default: return 0;
            }
        }
        public int Get(MaoAppearancePart part)
        {
            switch (part)
            {
                case MaoAppearancePart.Hair: return hair;
                case MaoAppearancePart.HairColor: return hairColor;
                case MaoAppearancePart.Outfit: return outfit;
                case MaoAppearancePart.Face: return face;
                case MaoAppearancePart.EyeColor: return eyeColor;
                case MaoAppearancePart.CatEars: return catEars;
                case MaoAppearancePart.CatTail: return catTail;
                default: return 0;
            }
        }
        public bool TrySet(MaoAppearancePart part, int id)
        {
            if (id < 0 || id >= Count(part)) return false;
            switch (part)
            {
                case MaoAppearancePart.Hair: hair = id; break;
                case MaoAppearancePart.HairColor: hairColor = id; break;
                case MaoAppearancePart.Outfit: outfit = id; break;
                case MaoAppearancePart.Face: face = id; break;
                case MaoAppearancePart.EyeColor: eyeColor = id; break;
                case MaoAppearancePart.CatEars: catEars = id; break;
                case MaoAppearancePart.CatTail: catTail = id; break;
            }
            return true;
        }
        public void Normalize()
        {
            foreach (MaoAppearancePart part in Enum.GetValues(typeof(MaoAppearancePart)))
                if (Get(part) < 0 || Get(part) >= Count(part)) TrySet(part, 0);
        }
        public void Reset() { hair = hairColor = outfit = face = eyeColor = catEars = catTail = 0; }
        public MaoAppearanceProfile Copy() => JsonUtility.FromJson<MaoAppearanceProfile>(JsonUtility.ToJson(this));
    }

    public static class MaoAppearanceGeometry
    {
        public static Vector3 ShortenTail(Vector3 point, Vector3 anchor, float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale)) return point;
            return anchor + (point - anchor) * Mathf.Clamp(scale, 0.25f, 1f);
        }
        public static Vector3 ShapeFace(Vector3 point, Vector3 head, int style)
        {
            if (style != 1 && style != 2) return point;
            Vector3 local = point - head;
            if (local.y < -0.04f || local.y > 0.13f) return point;
            float cheek = Mathf.InverseLerp(0.018f, 0.055f, Mathf.Abs(local.x));
            float vertical = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.04f, 0f, local.y)) *
                             (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.045f, 0.11f, local.y)));
            local.x *= Mathf.Lerp(1f, style == 1 ? 1.08f : 0.93f, cheek * vertical);
            return head + local;
        }
    }
}
