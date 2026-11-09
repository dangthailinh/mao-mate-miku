using System;
using System.Globalization;

namespace MaoIntegration
{
    [Serializable]
    public sealed class MaoEmotionalState
    {
        public int SchemaVersion = 2;
        public float Valence = 0.5f;
        public float Arousal = 0.35f;
        public float Dominance = 0.5f;
        public float Trust = 0.35f;
        public float Curiosity = 0.55f;
        public float Fatigue = 0.15f;
        // Bounded social-affect signals used only to shape tone and timing.  They
        // are deliberately not framed as human needs or a demand for attention.
        public float Irritation = 0.05f;
        public float Loneliness = 0.15f;
        // Confidence in Miku's internal mood estimate; it is never a claim about
        // Linh's mental state and is intentionally exposed only as style metadata.
        public float Confidence = 0.35f;
        public string LastEmotion = "neutral";
        public string UpdatedUtc;

        public static MaoEmotionalState CreateDefault(DateTime nowUtc)
        {
            return new MaoEmotionalState { UpdatedUtc = nowUtc.ToUniversalTime().ToString("o") };
        }
    }

    /// <summary>
    /// Conversation events are intentionally small nudges.  They provide
    /// continuity between turns without turning a transient chat moment into a
    /// permanent mood or allowing trust/arousal to grow without bounds.
    /// </summary>
    public enum MaoConversationEmotionEvent
    {
        UserMessage,
        MeaningfulReply,
        UserFollowUp,
        UserCorrection,
        TaskCompleted,
        TaskFailed,
        UserQuiet
    }

    public static class MaoEmotionEngine
    {
        private static readonly string[] AllowedEmotions =
        {
            "neutral", "happy", "playful", "curious", "shy", "surprised", "concerned",
            "angry", "pout", "sad", "sleepy", "embarrassed"
        };

        /// <summary>
        /// Normalizes state loaded from disk before it can affect prompts or
        /// expressions.  Old/corrupt state files are user-writable data: NaN,
        /// infinity, future timestamps, and unknown labels must not leak into
        /// runtime decisions or make the mood stick indefinitely.
        /// </summary>
        public static bool NormalizeLoadedState(MaoEmotionalState state, DateTime nowUtc)
        {
            if (state == null) return false;
            bool changed = false;
            if (state.SchemaVersion != 2) { state.SchemaVersion = 2; changed = true; }

            changed |= NormalizeField(ref state.Valence, 0.5f);
            changed |= NormalizeField(ref state.Arousal, 0.35f);
            changed |= NormalizeField(ref state.Dominance, 0.5f);
            changed |= NormalizeField(ref state.Trust, 0.35f);
            changed |= NormalizeField(ref state.Curiosity, 0.55f);
            changed |= NormalizeField(ref state.Fatigue, 0.15f);
            changed |= NormalizeField(ref state.Irritation, 0.05f);
            changed |= NormalizeField(ref state.Loneliness, 0.15f);
            changed |= NormalizeField(ref state.Confidence, 0.35f);

            string emotion = (state.LastEmotion ?? string.Empty).Trim().ToLowerInvariant();
            if (!Array.Exists(AllowedEmotions, item => item == emotion))
            {
                state.LastEmotion = "neutral";
                changed = true;
            }
            else if (!string.Equals(state.LastEmotion, emotion, StringComparison.Ordinal))
            {
                state.LastEmotion = emotion;
                changed = true;
            }

            DateTime parsed;
            DateTime now = nowUtc.ToUniversalTime();
            if (!DateTime.TryParse(state.UpdatedUtc, null, DateTimeStyles.RoundtripKind, out parsed) ||
                parsed.ToUniversalTime() > now)
            {
                state.UpdatedUtc = now.ToString("o");
                changed = true;
            }

            Clamp(state);
            return changed;
        }

        public static void ApplyReaction(MaoEmotionalState state, string emotion, DateTime nowUtc)
        {
            if (state == null) return;
            Decay(state, nowUtc);
            switch ((emotion ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "happy": Adjust(state, 0.14f, 0.08f, 0.02f, 0.015f, 0.01f, -0.02f); break;
                case "playful": Adjust(state, 0.10f, 0.12f, 0.04f, 0.01f, 0.05f, 0f); break;
                case "curious": Adjust(state, 0.02f, 0.05f, 0f, 0f, 0.12f, 0.01f); break;
                case "shy": Adjust(state, 0.03f, 0.06f, -0.06f, 0.02f, 0.01f, 0.01f); break;
                case "surprised": Adjust(state, 0f, 0.18f, -0.02f, 0f, 0.05f, 0.02f); break;
                case "concerned": Adjust(state, -0.08f, 0.08f, -0.02f, 0.015f, 0.02f, 0.03f); break;
                case "angry": Adjust(state, -0.05f, 0.16f, 0.08f, -0.005f, 0.01f, 0.02f); break;
                case "pout": Adjust(state, -0.025f, 0.08f, 0.01f, 0.005f, 0.02f, 0.01f); break;
                case "sad": Adjust(state, -0.12f, -0.02f, -0.04f, 0.01f, 0.01f, 0.04f); break;
                case "sleepy": Adjust(state, 0f, -0.10f, -0.02f, 0f, 0f, 0.06f); break;
                case "embarrassed": Adjust(state, 0.01f, 0.07f, -0.05f, 0.01f, 0.02f, 0.01f); break;
                default: state.LastEmotion = "neutral"; state.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o"); return;
            }
            state.LastEmotion = (emotion ?? "neutral").Trim().ToLowerInvariant();
            state.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
        }

        public static void ApplyPositiveInteraction(MaoEmotionalState state, DateTime nowUtc)
        {
            if (state == null) return;
            Decay(state, nowUtc);
            Adjust(state, 0.08f, 0.03f, 0.015f, 0.025f, 0.025f, -0.025f);
            state.LastEmotion = "happy";
            state.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
        }

        public static void ApplyConversationEvent(
            MaoEmotionalState state,
            MaoConversationEmotionEvent emotionEvent,
            DateTime nowUtc)
        {
            if (state == null) return;
            Decay(state, nowUtc);
            switch (emotionEvent)
            {
                case MaoConversationEmotionEvent.UserMessage:
                    // A turn is interesting, but does not imply closeness by itself.
                    Adjust(state, 0.015f, 0.015f, 0f, 0.004f, 0.012f, 0f);
                    state.Irritation = MoveToward(state.Irritation, 0.05f, 0.18f);
                    state.Loneliness = MoveToward(state.Loneliness, 0.05f, 0.22f);
                    state.LastEmotion = "curious";
                    break;
                case MaoConversationEmotionEvent.MeaningfulReply:
                    Adjust(state, 0.055f, 0.018f, 0.008f, 0.014f, 0.016f, -0.01f);
                    state.LastEmotion = "happy";
                    break;
                case MaoConversationEmotionEvent.UserFollowUp:
                    Adjust(state, 0.045f, 0.025f, 0.005f, 0.018f, 0.022f, -0.012f);
                    state.Irritation = MoveToward(state.Irritation, 0.03f, 0.28f);
                    state.Loneliness = MoveToward(state.Loneliness, 0.03f, 0.32f);
                    state.LastEmotion = "playful";
                    break;
                case MaoConversationEmotionEvent.UserCorrection:
                    // Repair should make Miku attentive, never defensive.
                    Adjust(state, -0.01f, 0.02f, -0.01f, 0.008f, 0.04f, 0.005f);
                    state.LastEmotion = "concerned";
                    break;
                case MaoConversationEmotionEvent.TaskCompleted:
                    Adjust(state, 0.04f, 0.015f, 0.01f, 0.01f, 0.01f, -0.015f);
                    state.LastEmotion = "happy";
                    break;
                case MaoConversationEmotionEvent.TaskFailed:
                    Adjust(state, -0.035f, 0.03f, -0.01f, 0f, 0.025f, 0.015f);
                    state.LastEmotion = "concerned";
                    break;
                case MaoConversationEmotionEvent.UserQuiet:
                    // Quiet is not rejection.  It simply lowers activation slowly.
                    Adjust(state, -0.005f, -0.01f, 0f, 0f, 0.005f, 0.004f);
                    state.Irritation = MoveToward(state.Irritation, 0.05f, 0.04f);
                    state.LastEmotion = "neutral";
                    break;
            }
            state.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
        }

        /// <summary>
        /// Advances the quiet-time affect with a bounded curve.  The result is
        /// intentionally subtle: it may make a proactive line a little pouty,
        /// but never turns silence into guilt, pressure, or repeated spam.
        /// </summary>
        public static void AdvanceSilence(MaoEmotionalState state, DateTime lastInteractionUtc, DateTime nowUtc)
        {
            if (state == null) return;
            DateTime last = lastInteractionUtc.ToUniversalTime();
            DateTime now = nowUtc.ToUniversalTime();
            double hours = Math.Max(0d, Math.Min(24d * 30d, (now - last).TotalHours));
            float targetLoneliness = 0.05f + (float)(0.62d * (1d - Math.Exp(-hours / 30d)));
            float targetIrritation = 0.05f + (float)(0.22d * (1d - Math.Exp(-hours / 48d)));
            state.Loneliness = MoveToward(state.Loneliness, targetLoneliness, 0.20f);
            state.Irritation = MoveToward(state.Irritation, targetIrritation, 0.14f);
            Clamp(state);
            state.UpdatedUtc = now.ToString("o");
        }

        public static void Decay(MaoEmotionalState state, DateTime nowUtc)
        {
            if (state == null) return;
            DateTime previous = ParseDate(state.UpdatedUtc, nowUtc);
            double hours = Math.Max(0d, Math.Min(24d * 30d, (nowUtc.ToUniversalTime() - previous).TotalHours));
            if (hours <= 0d) return;
            float fast = (float)(1d - Math.Exp(-hours / 8d));
            float slow = (float)(1d - Math.Exp(-hours / 240d));
            state.Valence = MoveToward(state.Valence, 0.5f, fast);
            state.Arousal = MoveToward(state.Arousal, 0.35f, fast);
            state.Dominance = MoveToward(state.Dominance, 0.5f, fast);
            state.Curiosity = MoveToward(state.Curiosity, 0.55f, fast * 0.5f);
            state.Fatigue = MoveToward(state.Fatigue, 0.15f, fast);
            state.Irritation = MoveToward(state.Irritation, 0.05f, fast * 0.35f);
            state.Confidence = MoveToward(state.Confidence, 0.35f, slow);
            state.Trust = MoveToward(state.Trust, 0.35f, slow);
            Clamp(state);
            state.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
        }

        public static string BuildPromptContext(MaoEmotionalState state)
        {
            if (state == null) return string.Empty;
            return "Persistent mood: " + Mood(state.Valence) + ", " + Energy(state.Arousal) +
                   "; trust=" + Level(state.Trust) + ", curiosity=" + Level(state.Curiosity) +
                   ", fatigue=" + Level(state.Fatigue) +
                   ", quiet_affect=" + Level(state.Loneliness) +
                   ", irritation=" + Level(state.Irritation) +
                   ", confidence=" + Level(state.Confidence) +
                   ". Let this subtly influence tone and response length; never claim human consciousness, infer Linh's feelings as fact, or use emotion to pressure the user.";
        }

        public static string GetExpression(MaoEmotionalState state)
        {
            if (state == null) return "neutral";
            string last = (state.LastEmotion ?? string.Empty).Trim().ToLowerInvariant();
            if (last == "angry" || last == "pout" || last == "sad" || last == "sleepy" || last == "embarrassed")
                return last;
            if (state.Valence >= 0.68f && state.Arousal >= 0.5f) return "playful";
            if (state.Valence >= 0.62f) return "happy";
            if (state.Valence <= 0.38f) return "concerned";
            if (state.Curiosity >= 0.72f) return "curious";
            return "neutral";
        }

        private static void Adjust(MaoEmotionalState state, float valence, float arousal, float dominance, float trust, float curiosity, float fatigue)
        {
            state.Valence += valence;
            state.Arousal += arousal;
            state.Dominance += dominance;
            state.Trust += trust;
            state.Curiosity += curiosity;
            state.Fatigue += fatigue;
            state.Confidence += 0.01f;
            Clamp(state);
        }

        private static void Clamp(MaoEmotionalState state)
        {
            state.Valence = Clamp01(state.Valence);
            state.Arousal = Clamp01(state.Arousal);
            state.Dominance = Clamp01(state.Dominance);
            state.Trust = Clamp01(state.Trust);
            state.Curiosity = Clamp01(state.Curiosity);
            state.Fatigue = Clamp01(state.Fatigue);
            state.Irritation = Clamp01(state.Irritation);
            state.Loneliness = Clamp01(state.Loneliness);
            state.Confidence = Clamp01(state.Confidence);
        }

        private static float MoveToward(float current, float baseline, float amount)
        {
            return current + (baseline - current) * Clamp01(amount);
        }

        private static bool NormalizeField(ref float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = fallback;
                return true;
            }
            float clamped = Clamp01(value);
            if (Math.Abs(clamped - value) <= 0.000001f) return false;
            value = clamped;
            return true;
        }

        private static string Mood(float value) { return value >= 0.65f ? "warm and upbeat" : value <= 0.4f ? "slightly concerned" : "content"; }
        private static string Energy(float value) { return value >= 0.65f ? "energetic" : value <= 0.3f ? "calm" : "gently alert"; }
        private static string Level(float value) { return value >= 0.7f ? "high" : value <= 0.35f ? "low" : "moderate"; }
        private static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime()
                : fallback.ToUniversalTime();
        }
    }
}
