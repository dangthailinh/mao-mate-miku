using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MaoIntegration
{
    public static class MaoIdleActionPolicy
    {
        public static bool CanPlayAutonomous(
            bool isSitting,
            bool isSleeping,
            bool isWindowSitting,
            bool isTaskbarSitting,
            bool isLying,
            bool isMounted,
            bool isTransitioning)
        {
            return !isSitting && !isSleeping && !isWindowSitting && !isTaskbarSitting &&
                   !isLying && !isMounted && !isTransitioning;
        }
    }

    public enum MaoInitiativeChannel
    {
        None,
        Physical,
        Speech,
        Vision
    }

    [Serializable]
    public sealed class MaoPersonalityProfile
    {
        public float Talkativeness = 0.35f;
        public float Curiosity = 0.75f;
        public float Shyness = 0.55f;
        public float Playfulness = 0.70f;
        public float Patience = 0.70f;
        public float Energy = 0.60f;

        public static MaoPersonalityProfile Default => new MaoPersonalityProfile();
    }

    public sealed class MaoInitiativeInput
    {
        public float IdleSeconds;
        public bool UserIsActive;
        public bool ScreenChanged;
        public float Novelty;
        public float Curiosity;
        public float FocusProtection;
        public float SecondsSinceReaction;
        public float SecondsSinceSpeech;
        public float SecondsSinceVision;
        public bool ConversationBusy;
        public int IgnoredInitiatives;
        public float StimulationNeed;
        public float Salience;
        public MaoPersonalityProfile Personality = MaoPersonalityProfile.Default;
    }

    public sealed class MaoInitiativeDecision
    {
        public MaoInitiativeDecision(
            MaoInitiativeChannel channel,
            float physicalScore,
            float speechScore,
            float visionScore,
            string reason)
        {
            Channel = channel;
            PhysicalScore = physicalScore;
            SpeechScore = speechScore;
            VisionScore = visionScore;
            Reason = reason ?? string.Empty;
        }

        public MaoInitiativeChannel Channel { get; }
        public float PhysicalScore { get; }
        public float SpeechScore { get; }
        public float VisionScore { get; }
        public string Reason { get; }
    }

    public enum MaoScreenChangeReason
    {
        Initializing,
        Unchanged,
        WaitingForStability,
        Cooldown,
        MeaningfulChange,
        MaximumSilence
    }

    public readonly struct MaoScreenChangeResult
    {
        public MaoScreenChangeResult(
            float difference,
            float motionDifference,
            bool shouldRequestVision,
            MaoScreenChangeReason reason,
            float cooldownRemainingSeconds)
        {
            Difference = difference;
            MotionDifference = motionDifference;
            ShouldRequestVision = shouldRequestVision;
            Reason = reason;
            CooldownRemainingSeconds = cooldownRemainingSeconds;
        }

        public float Difference { get; }
        public float MotionDifference { get; }
        public bool ShouldRequestVision { get; }
        public MaoScreenChangeReason Reason { get; }
        public float CooldownRemainingSeconds { get; }
    }

    public sealed class MaoScreenChangeDetector
    {
        private readonly float meaningfulThreshold;
        private readonly float stabilityThreshold;
        private readonly double stableSeconds;
        private readonly double cooldownSeconds;
        private readonly double maximumSilenceSeconds;
        private byte[] acceptedBaseline;
        private byte[] previousSample;
        private byte[] latestSample;
        private byte[] requestSample;
        private bool hasAcceptedBaseline;
        private bool hasRequestSample;
        private bool pendingMeaningfulChange;
        private double lastMotionAt;
        private double lastRequestAt = double.NegativeInfinity;
        private double lastAcceptedAt = double.NegativeInfinity;

        public MaoScreenChangeDetector(
            float meaningfulThreshold,
            float stabilityThreshold,
            float stableSeconds,
            float cooldownSeconds,
            float maximumSilenceSeconds)
        {
            this.meaningfulThreshold = Clamp01(meaningfulThreshold);
            this.stabilityThreshold = Clamp01(stabilityThreshold);
            this.stableSeconds = Math.Max(0d, stableSeconds);
            this.cooldownSeconds = Math.Max(0d, cooldownSeconds);
            this.maximumSilenceSeconds = Math.Max(1d, maximumSilenceSeconds);
        }

        public MaoScreenChangeResult Observe(byte[] grayscaleSample, double nowSeconds)
        {
            if (grayscaleSample == null || grayscaleSample.Length == 0)
                throw new ArgumentException("A non-empty grayscale sample is required.", nameof(grayscaleSample));
            double now = NormalizeTime(nowSeconds);
            if (acceptedBaseline == null || acceptedBaseline.Length != grayscaleSample.Length)
                Initialize(grayscaleSample, now);

            float difference = CalculateDifference(acceptedBaseline, grayscaleSample);
            float motionDifference = CalculateDifference(previousSample, grayscaleSample);
            CopyInto(ref previousSample, grayscaleSample);
            CopyInto(ref latestSample, grayscaleSample);

            if (motionDifference > 0f && motionDifference >= stabilityThreshold) lastMotionAt = now;
            bool meaningful = difference >= meaningfulThreshold;
            if (meaningful && !pendingMeaningfulChange) lastMotionAt = now;
            pendingMeaningfulChange = meaningful;

            bool stable = now - lastMotionAt >= stableSeconds;
            bool initialObservation = !hasAcceptedBaseline;
            bool maximumSilence = hasAcceptedBaseline &&
                                  now - lastAcceptedAt >= maximumSilenceSeconds;
            double cooldownRemaining = Math.Max(0d, cooldownSeconds - (now - lastRequestAt));

            if (!stable && initialObservation)
                return Result(difference, motionDifference, false,
                    MaoScreenChangeReason.Initializing, cooldownRemaining);
            if (!stable && (meaningful || maximumSilence))
                return Result(difference, motionDifference, false,
                    MaoScreenChangeReason.WaitingForStability, cooldownRemaining);
            if (!meaningful && !maximumSilence && !initialObservation)
                return Result(difference, motionDifference, false,
                    MaoScreenChangeReason.Unchanged, cooldownRemaining);
            if (cooldownRemaining > 0d)
                return Result(difference, motionDifference, false,
                    MaoScreenChangeReason.Cooldown, cooldownRemaining);

            MaoScreenChangeReason readyReason = meaningful
                ? MaoScreenChangeReason.MeaningfulChange
                : initialObservation
                    ? MaoScreenChangeReason.Initializing
                    : MaoScreenChangeReason.MaximumSilence;
            return Result(difference, motionDifference, true, readyReason, 0d);
        }

        public void MarkVisionRequested(double nowSeconds)
        {
            lastRequestAt = NormalizeTime(nowSeconds);
            if (latestSample == null) return;
            CopyInto(ref requestSample, latestSample);
            hasRequestSample = true;
        }

        public void MarkVisionAccepted(double nowSeconds)
        {
            if (!hasRequestSample) return;
            CopyInto(ref acceptedBaseline, requestSample);
            CopyInto(ref previousSample, requestSample);
            CopyInto(ref latestSample, requestSample);
            hasAcceptedBaseline = true;
            hasRequestSample = false;
            pendingMeaningfulChange = false;
            lastAcceptedAt = NormalizeTime(nowSeconds);
            lastMotionAt = lastAcceptedAt;
        }

        public void MarkVisionFailed()
        {
            hasRequestSample = false;
        }

        public static float CalculateDifference(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length == 0 || first.Length != second.Length)
                throw new ArgumentException("Screen samples must be non-empty and have equal lengths.");
            long totalDifference = 0L;
            for (int index = 0; index < first.Length; index++)
                totalDifference += Math.Abs(first[index] - second[index]);
            return (float)(totalDifference / (255d * first.Length));
        }

        private void Initialize(byte[] sample, double now)
        {
            CopyInto(ref acceptedBaseline, sample);
            CopyInto(ref previousSample, sample);
            CopyInto(ref latestSample, sample);
            hasAcceptedBaseline = false;
            hasRequestSample = false;
            pendingMeaningfulChange = false;
            lastMotionAt = now;
            lastRequestAt = double.NegativeInfinity;
            lastAcceptedAt = double.NegativeInfinity;
        }

        private static MaoScreenChangeResult Result(
            float difference,
            float motionDifference,
            bool ready,
            MaoScreenChangeReason reason,
            double cooldownRemaining)
        {
            return new MaoScreenChangeResult(
                difference,
                motionDifference,
                ready,
                reason,
                (float)Math.Max(0d, cooldownRemaining));
        }

        private static void CopyInto(ref byte[] destination, byte[] source)
        {
            if (destination == null || destination.Length != source.Length)
                destination = new byte[source.Length];
            Buffer.BlockCopy(source, 0, destination, 0, source.Length);
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value)) return 0f;
            return Math.Max(0f, Math.Min(1f, value));
        }

        private static double NormalizeTime(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;
        }
    }

    public static class MaoInitiativeLogic
    {
        public const float PhysicalThreshold = 0.45f;
        public const float SpeechThreshold = 0.68f;
        public const float VisionThreshold = 0.55f;

        public static MaoInitiativeDecision Evaluate(MaoInitiativeInput input)
        {
            input = input ?? new MaoInitiativeInput();
            MaoPersonalityProfile personality = input.Personality ?? MaoPersonalityProfile.Default;

            float idle = Saturate(input.IdleSeconds / 180f);
            float reactionRecency = Saturate(input.SecondsSinceReaction / 90f);
            float speechRecency = Saturate(input.SecondsSinceSpeech / 300f);
            float visionRecency = Saturate(input.SecondsSinceVision / 300f);
            float novelty = Saturate(input.Novelty);
            float curiosity = Saturate(input.Curiosity);
            float stimulation = Saturate(input.StimulationNeed);
            float salience = Saturate(input.Salience);
            float focus = Saturate(input.FocusProtection);
            float quietMultiplier = 1f - (focus * 0.78f);
            if (input.UserIsActive) quietMultiplier *= 0.82f;

            float physical =
                curiosity * 0.25f + novelty * 0.15f + idle * 0.20f +
                reactionRecency * 0.15f + stimulation * 0.15f +
                Saturate(personality.Playfulness) * 0.10f;
            physical *= Math.Max(0.15f, quietMultiplier);

            float speech =
                Saturate(personality.Talkativeness) * 0.20f + salience * 0.25f +
                novelty * 0.15f + idle * 0.15f + speechRecency * 0.15f +
                curiosity * 0.10f - Saturate(personality.Shyness) * 0.20f;
            speech *= Math.Max(0.08f, quietMultiplier);
            speech *= 1f / (1f + Math.Max(0, input.IgnoredInitiatives) * 0.35f);

            float vision = curiosity * 0.35f + novelty * 0.35f +
                           visionRecency * 0.15f + salience * 0.10f +
                           (input.ScreenChanged ? 0.15f : 0f);
            // Looking silently is less interruptive than speaking, so focus protection is gentler here.
            float visionQuietMultiplier = 1f - focus * 0.35f;
            if (input.UserIsActive) visionQuietMultiplier *= 0.90f;
            vision *= Math.Max(0.35f, visionQuietMultiplier);

            if (input.ConversationBusy)
            {
                physical *= 0.45f;
                speech *= 0.08f;
                vision *= 0.08f;
            }

            physical = Saturate(physical);
            speech = Saturate(speech);
            vision = Saturate(vision);

            // Curiosity is deliberately embodied before it becomes verbal.
            if (physical >= PhysicalThreshold && input.SecondsSinceReaction >= 30f)
                return new MaoInitiativeDecision(MaoInitiativeChannel.Physical, physical, speech, vision, "curiosity-physical-first");
            if (input.ScreenChanged && vision >= VisionThreshold && input.SecondsSinceVision >= 180f)
                return new MaoInitiativeDecision(MaoInitiativeChannel.Vision, physical, speech, vision, "novel-screen-context");
            if (speech >= SpeechThreshold && input.SecondsSinceSpeech >= 240f)
                return new MaoInitiativeDecision(MaoInitiativeChannel.Speech, physical, speech, vision, "rare-contextual-speech");

            string reason = input.ConversationBusy ? "conversation-busy" :
                focus >= 0.65f ? "protecting-user-focus" : "below-threshold";
            return new MaoInitiativeDecision(MaoInitiativeChannel.None, physical, speech, vision, reason);
        }

        private static float Saturate(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 0f;
            return value >= 1f ? 1f : value;
        }
    }

    public sealed class MaoCuriosityState
    {
        public MaoCuriosityState(float initialValue = 0.25f)
        {
            Value = Clamp01(initialValue);
        }

        public float Value { get; private set; }

        public void Advance(float deltaSeconds, float novelty, bool contextChanged, bool screenChanged, float decayPerSecond)
        {
            float decay = Math.Max(0f, deltaSeconds) * Math.Max(0f, decayPerSecond);
            float stimulus = Clamp01(novelty) * 0.20f;
            if (contextChanged) stimulus += 0.16f;
            if (screenChanged) stimulus += 0.10f;
            Value = Clamp01(Value - decay + stimulus);
        }

        public void Satisfy(float amount)
        {
            Value = Clamp01(Value - Math.Max(0f, amount));
        }

        private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));
    }

    public sealed class MaoContextNoveltyTracker
    {
        private sealed class Entry
        {
            public string Key;
            public float LastSeen;
            public int SeenCount;
        }

        private readonly int capacity;
        private readonly List<Entry> entries = new List<Entry>();

        public MaoContextNoveltyTracker(int capacity = 24)
        {
            this.capacity = Math.Max(1, capacity);
        }

        public int Count => entries.Count;

        public float Observe(string contextKey, float now)
        {
            string key = Normalize(contextKey);
            for (int index = 0; index < entries.Count; index++)
            {
                Entry entry = entries[index];
                if (!string.Equals(entry.Key, key, StringComparison.Ordinal)) continue;
                float elapsed = Math.Max(0f, now - entry.LastSeen);
                entry.LastSeen = now;
                entry.SeenCount++;
                float recovered = Math.Min(0.65f, elapsed / 1800f);
                return Clamp01((0.18f + recovered) / (1f + entry.SeenCount * 0.35f));
            }

            if (entries.Count >= capacity)
            {
                int oldest = 0;
                for (int index = 1; index < entries.Count; index++)
                    if (entries[index].LastSeen < entries[oldest].LastSeen) oldest = index;
                entries.RemoveAt(oldest);
            }
            entries.Add(new Entry { Key = key, LastSeen = now, SeenCount = 1 });
            return 1f;
        }

        private static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length > 160) value = value.Substring(0, 160);
            return value;
        }

        private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));
    }

    public sealed class MaoReactionHistory
    {
        private sealed class Entry
        {
            public MaoInitiativeChannel Channel;
            public string Intent;
            public string Topic;
            public string App;
            public string ContextHash;
            public float Time;
        }

        private readonly int capacity;
        private readonly List<Entry> entries = new List<Entry>();

        public MaoReactionHistory(int capacity = 32)
        {
            this.capacity = Math.Max(1, capacity);
        }

        public int Count => entries.Count;

        public bool CanUse(
            MaoInitiativeChannel channel,
            string intent,
            string topic,
            string app,
            string contextHash,
            float now,
            float cooldown)
        {
            string normalizedIntent = Normalize(intent);
            string normalizedTopic = Normalize(topic);
            string normalizedApp = Normalize(app);
            string normalizedHash = Normalize(contextHash);
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                Entry entry = entries[index];
                if (entry.Channel != channel) continue;
                if (now - entry.Time >= Math.Max(0f, cooldown)) continue;
                if (entry.Intent == normalizedIntent && entry.Topic == normalizedTopic &&
                    entry.App == normalizedApp && entry.ContextHash == normalizedHash)
                    return false;
            }
            return true;
        }

        public void Record(
            MaoInitiativeChannel channel,
            string intent,
            string topic,
            string app,
            string contextHash,
            float now)
        {
            if (entries.Count >= capacity) entries.RemoveAt(0);
            entries.Add(new Entry
            {
                Channel = channel,
                Intent = Normalize(intent),
                Topic = Normalize(topic),
                App = Normalize(app),
                ContextHash = Normalize(contextHash),
                Time = now
            });
        }

        private static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value.Length <= 96 ? value : value.Substring(0, 96);
        }
    }

    public sealed class MaoSpeechBudget
    {
        private readonly float capacity;
        private readonly float secondsPerToken;
        private float available;
        private float lastUpdated;

        public MaoSpeechBudget(float capacity = 5f, float secondsPerToken = 180f, float now = 0f)
        {
            this.capacity = Math.Max(1f, capacity);
            this.secondsPerToken = Math.Max(1f, secondsPerToken);
            available = this.capacity;
            lastUpdated = now;
        }

        public float Available => available;

        public void Update(float now)
        {
            float elapsed = Math.Max(0f, now - lastUpdated);
            available = Math.Min(capacity, available + elapsed / secondsPerToken);
            lastUpdated = Math.Max(lastUpdated, now);
        }

        public bool TrySpend(float now)
        {
            Update(now);
            if (available < 1f) return false;
            available -= 1f;
            return true;
        }
    }

    public sealed class MaoSemanticReaction
    {
        public MaoSemanticReaction(string displayText, string emotion, string gesture, string attention)
        {
            DisplayText = displayText ?? string.Empty;
            Emotion = emotion ?? "neutral";
            Gesture = gesture ?? "none";
            Attention = attention ?? "user";
        }

        public string DisplayText { get; }
        public string Emotion { get; }
        public string Gesture { get; }
        public string Attention { get; }
    }

    public static class MaoSemanticReactionParser
    {
        private static readonly Regex TagPattern = new Regex(
            @"^\s*\[\s*emotion\s*=\s*([^;\]]+)\s*;\s*gesture\s*=\s*([^;\]]+)\s*;\s*attention\s*=\s*([^\]]+)\]\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Emotions = new HashSet<string>(StringComparer.Ordinal)
        {
            "neutral", "happy", "curious", "shy", "surprised", "concerned", "playful",
            "angry", "pout", "sad", "sleepy", "embarrassed"
        };

        private static readonly HashSet<string> Gestures = new HashSet<string>(StringComparer.Ordinal)
        {
            "none", "wave", "nod", "tilt", "ponder", "celebrate", "pout", "cross_arms",
            "look_away", "laugh", "clap", "thinking", "confused", "surprised", "sleepy",
            "yawn", "stretch", "shy"
        };

        private static readonly HashSet<string> AttentionTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "user", "screen", "cursor", "away"
        };

        public static MaoSemanticReaction Parse(string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            Match match = TagPattern.Match(text);
            if (!match.Success) return new MaoSemanticReaction(text, "neutral", "none", "user");

            string emotion = NormalizeAllowed(match.Groups[1].Value, Emotions, "neutral");
            string gesture = NormalizeAllowed(match.Groups[2].Value, Gestures, "none");
            string attention = NormalizeAllowed(match.Groups[3].Value, AttentionTargets, "user");
            string displayText = text.Substring(match.Length).Trim();
            if (displayText.Length > 4000) displayText = displayText.Substring(0, 4000);
            return new MaoSemanticReaction(displayText, emotion, gesture, attention);
        }

        /// <summary>
        /// Adds a small local body-language cue when a user clearly teases Miku
        /// but the model omitted a semantic header.  It only fills a neutral
        /// reaction, so an explicit model tag always wins.
        /// </summary>
        public static MaoSemanticReaction ApplyUserTone(string userText, MaoSemanticReaction reaction)
        {
            if (reaction == null || !string.Equals(reaction.Emotion, "neutral", StringComparison.Ordinal))
                return reaction;
            string normalized = (userText ?? string.Empty).Trim().ToLowerInvariant();
            if (!ContainsAny(normalized, "trêu", "treu", "chọc", "choc", "bựa", "bua", "ngốc", "ngoc", "dở hơi", "do hoi"))
                return reaction;
            return new MaoSemanticReaction(reaction.DisplayText, "pout", "pout", reaction.Attention);
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            for (int index = 0; index < needles.Length; index++)
                if (value.Contains(needles[index])) return true;
            return false;
        }

        private static string NormalizeAllowed(string value, HashSet<string> allowed, string fallback)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return allowed.Contains(normalized) ? normalized : fallback;
        }
    }

    public enum MaoMovementCommandAction
    {
        FollowCursor,
        RunFollowCursor,
        PlayGuitar,
        StopGuitar,
        Angry,
        JabCross,
        BoxJump,
        JogInCircle,
        FlyingKick,
        ToggleGravity,
        EnableGravity,
        DisableGravity,
        GravityStatus,
        Stop,
        Status
    }

    public enum MaoIdleActionKind
    {
        Angry,
        JabCross,
        BoxJump,
        JogInCircle,
        FlyingKick
    }

    public sealed class MaoMovementCommand
    {
        public MaoMovementCommand(MaoMovementCommandAction action)
        {
            Action = action;
        }

        public MaoMovementCommandAction Action { get; }
    }

    public static class MaoMovementCommandParser
    {
        public static bool TryParse(string message, out MaoMovementCommand command)
        {
            command = null;
            string raw = (message ?? string.Empty).Trim();
            if (string.Equals(raw, "/guitarplay", StringComparison.OrdinalIgnoreCase))
            {
                command = new MaoMovementCommand(MaoMovementCommandAction.PlayGuitar);
                return true;
            }
            if (string.Equals(raw, "/guitarstop", StringComparison.OrdinalIgnoreCase))
            {
                command = new MaoMovementCommand(MaoMovementCommandAction.StopGuitar);
                return true;
            }
            if (string.Equals(raw, "/gravity", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.ToggleGravity, out command);
            if (string.Equals(raw, "/gravity on", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.EnableGravity, out command);
            if (string.Equals(raw, "/gravity off", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.DisableGravity, out command);
            if (string.Equals(raw, "/gravity status", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.GravityStatus, out command);
            if (TryParseIdleActionSlashCommand(raw, out command)) return true;
            string text = Normalize(message);
            if (text.Length == 0) return false;

            if (HasMikuAndAny(text, "bat trong luc di", "bat gravity di"))
                return SetAction(MaoMovementCommandAction.EnableGravity, out command);
            if (HasMikuAndAny(text, "tat trong luc di", "tat gravity di"))
                return SetAction(MaoMovementCommandAction.DisableGravity, out command);

            if (HasMikuAndAny(text, "tuc gian di", "noi gian di", "angry di"))
                return SetAction(MaoMovementCommandAction.Angry, out command);
            if (HasMikuAndAny(text, "dam combo di", "dam jab cross", "jab cross di"))
                return SetAction(MaoMovementCommandAction.JabCross, out command);
            if (HasMikuAndAny(text, "nhay len hop di", "box jump di"))
                return SetAction(MaoMovementCommandAction.BoxJump, out command);
            if (HasMikuAndAny(text, "chay vong tron di", "jog vong tron", "jog circle"))
                return SetAction(MaoMovementCommandAction.JogInCircle, out command);
            if (HasMikuAndAny(text, "da bay di", "da tren khong", "flying kick"))
                return SetAction(MaoMovementCommandAction.FlyingKick, out command);

            if (text == "move run" ||
                HasMikuAndAny(text, "chay di", "chay ngang", "chay theo con tro", "chay theo chuot", "chay tu do"))
            {
                command = new MaoMovementCommand(MaoMovementCommandAction.RunFollowCursor);
                return true;
            }

            if (text == "move follow" || text == "move free" ||
                HasMikuAndAny(text, "di chuyen tu do", "di theo con tro", "theo con tro chuot", "theo chuot"))
            {
                command = new MaoMovementCommand(MaoMovementCommandAction.FollowCursor);
                return true;
            }

            if (text == "move stop" ||
                HasMikuAndAny(text, "dung di chuyen", "ngung di chuyen", "dung yen", "dung lai"))
            {
                command = new MaoMovementCommand(MaoMovementCommandAction.Stop);
                return true;
            }

            if (text == "move status" || HasMikuAndAny(text, "trang thai di chuyen", "dang di theo chuot khong"))
            {
                command = new MaoMovementCommand(MaoMovementCommandAction.Status);
                return true;
            }

            return false;
        }

        private static bool TryParseIdleActionSlashCommand(string raw, out MaoMovementCommand command)
        {
            command = null;
            if (string.Equals(raw, "/angry", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.Angry, out command);
            if (string.Equals(raw, "/jabcross", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.JabCross, out command);
            if (string.Equals(raw, "/boxjump", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.BoxJump, out command);
            if (string.Equals(raw, "/jogcircle", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.JogInCircle, out command);
            if (string.Equals(raw, "/flyingkick", StringComparison.OrdinalIgnoreCase))
                return SetAction(MaoMovementCommandAction.FlyingKick, out command);
            return false;
        }

        private static bool SetAction(MaoMovementCommandAction action, out MaoMovementCommand command)
        {
            command = new MaoMovementCommand(action);
            return true;
        }

        private static bool HasMikuAndAny(string text, params string[] phrases)
        {
            if (!Regex.IsMatch(text, @"(^|\s)miku(\s|$)", RegexOptions.CultureInvariant)) return false;
            for (int index = 0; index < phrases.Length; index++)
                if (text.Contains(phrases[index])) return true;
            return false;
        }

        private static string Normalize(string value)
        {
            string decomposed = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace('đ', 'd')
                .Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            for (int index = 0; index < decomposed.Length; index++)
            {
                char character = decomposed[index];
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
            }
            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }
    }

    public enum MaoLocomotionGait
    {
        Idle,
        Walk,
        Run
    }

    public sealed class MaoAdaptiveLocomotionDecision
    {
        public MaoAdaptiveLocomotionDecision(MaoLocomotionGait gait, float facingYawDegrees)
        {
            Gait = gait;
            FacingYawDegrees = facingYawDegrees;
        }

        public MaoLocomotionGait Gait { get; }
        public float FacingYawDegrees { get; }
    }

    public static class MaoAdaptiveLocomotionLogic
    {
        public static MaoAdaptiveLocomotionDecision Decide(
            int direction,
            float targetDistance,
            float runDistance,
            float rightFacingYaw,
            float leftFacingYaw)
        {
            if (direction == 0)
                return new MaoAdaptiveLocomotionDecision(MaoLocomotionGait.Idle, 0f);

            MaoLocomotionGait gait = targetDistance >= Math.Max(0f, runDistance)
                ? MaoLocomotionGait.Run
                : MaoLocomotionGait.Walk;
            float yaw = direction < 0 ? leftFacingYaw : rightFacingYaw;
            return new MaoAdaptiveLocomotionDecision(gait, yaw);
        }

    }

    public static class MaoFacingLogic
    {
        public static float CalculateYawDegrees(
            float forwardX,
            float forwardZ,
            float targetX,
            float targetZ)
        {
            double forwardLength = Math.Sqrt((forwardX * forwardX) + (forwardZ * forwardZ));
            double targetLength = Math.Sqrt((targetX * targetX) + (targetZ * targetZ));
            if (forwardLength <= 0.000001d || targetLength <= 0.000001d) return 0f;

            double normalizedForwardX = forwardX / forwardLength;
            double normalizedForwardZ = forwardZ / forwardLength;
            double normalizedTargetX = targetX / targetLength;
            double normalizedTargetZ = targetZ / targetLength;
            double crossY = (normalizedForwardZ * normalizedTargetX) -
                            (normalizedForwardX * normalizedTargetZ);
            double dot = (normalizedForwardX * normalizedTargetX) +
                         (normalizedForwardZ * normalizedTargetZ);
            return (float)(Math.Atan2(crossY, dot) * (180d / Math.PI));
        }

        public static bool IsAligned(float currentYaw, float targetYaw, float toleranceDegrees)
        {
            return Math.Abs(ShortestAngleDegrees(currentYaw, targetYaw)) <=
                   Math.Max(0f, toleranceDegrees);
        }

        public static bool CanTranslate(float currentYaw, float targetYaw, float toleranceDegrees)
        {
            return IsAligned(currentYaw, targetYaw, toleranceDegrees);
        }

        public static bool DirectionsAgree(
            int movementDirection,
            float visualForwardScreenX,
            float forwardThreshold)
        {
            if (movementDirection == 0) return true;
            float threshold = Math.Max(0f, forwardThreshold);
            if (Math.Abs(visualForwardScreenX) < threshold) return false;
            return movementDirection < 0
                ? visualForwardScreenX < 0f
                : visualForwardScreenX > 0f;
        }

        private static float ShortestAngleDegrees(float fromDegrees, float toDegrees)
        {
            float delta = (toDegrees - fromDegrees) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            return delta;
        }
    }

    public sealed class MaoCursorFollowDecision
    {
        public MaoCursorFollowDecision(int direction, float distance, float targetDistance, float targetCenterX)
        {
            Direction = direction;
            Distance = distance;
            TargetDistance = targetDistance;
            TargetCenterX = targetCenterX;
        }

        public int Direction { get; }
        public float Distance { get; }
        public float TargetDistance { get; }
        public float TargetCenterX { get; }
    }

    public static class MaoCursorFollowLogic
    {
        public static MaoCursorFollowDecision Decide(
            float avatarCenterX,
            float cursorX,
            float sideOffset,
            float stopDistance,
            float maximumSegmentDistance)
        {
            float offset = Math.Max(0f, sideOffset);
            float target = cursorX >= avatarCenterX ? cursorX - offset : cursorX + offset;
            float delta = target - avatarCenterX;
            float distance = Math.Abs(delta);
            if (distance <= Math.Max(0f, stopDistance))
                return new MaoCursorFollowDecision(0, 0f, distance, target);
            float cappedDistance = Math.Min(distance, Math.Max(0f, maximumSegmentDistance));
            return new MaoCursorFollowDecision(delta < 0f ? -1 : 1, cappedDistance, distance, target);
        }
    }

    public enum MaoCursorFlightIntent
    {
        GroundFollow,
        Fly,
        Land
    }

    public sealed class MaoCursorFlightDecision
    {
        public MaoCursorFlightDecision(MaoCursorFlightIntent intent)
        {
            Intent = intent;
        }

        public MaoCursorFlightIntent Intent { get; }
    }

    public static class MaoCursorFlightLogic
    {
        public static MaoCursorFlightDecision Decide(
            bool airborne,
            float avatarHeadY,
            float cursorY,
            float taskbarTopY,
            float minimumTakeoffRise,
            float taskbarLandingDistance)
        {
            bool nearTaskbar = cursorY >= taskbarTopY - Math.Max(0f, taskbarLandingDistance);
            if (airborne) return new MaoCursorFlightDecision(
                nearTaskbar ? MaoCursorFlightIntent.Land : MaoCursorFlightIntent.Fly);

            float rise = avatarHeadY - cursorY;
            return new MaoCursorFlightDecision(!nearTaskbar &&
                                               rise >= Math.Max(0f, minimumTakeoffRise)
                ? MaoCursorFlightIntent.Fly
                : MaoCursorFlightIntent.GroundFollow);
        }

        public static MaoCursorFlightPosition StepWindowPosition(
            float currentWindowLeft,
            float currentWindowTop,
            float avatarAnchorX,
            float avatarHeadY,
            float cursorX,
            float cursorY,
            float horizontalOffset,
            float headOffset,
            float groundWindowTop,
            float speedPixelsPerSecond,
            float deltaTime)
        {
            float targetWindowLeft = currentWindowLeft + cursorX + horizontalOffset - avatarAnchorX;
            float targetWindowTop = currentWindowTop + cursorY + headOffset - avatarHeadY;
            targetWindowTop = Math.Min(targetWindowTop, groundWindowTop);
            float maximumStep = Math.Max(0f, speedPixelsPerSecond) * Math.Max(0f, deltaTime);
            return new MaoCursorFlightPosition(
                StepAxis(currentWindowLeft, targetWindowLeft, maximumStep),
                StepAxis(currentWindowTop, targetWindowTop, maximumStep));
        }

        private static float StepAxis(float current, float target, float maximumStep)
        {
            float delta = target - current;
            if (Math.Abs(delta) <= maximumStep) return target;
            return current + Math.Sign(delta) * maximumStep;
        }
    }

    public sealed class MaoCursorFlightPosition
    {
        public MaoCursorFlightPosition(float left, float top)
        {
            Left = left;
            Top = top;
        }

        public float Left { get; }
        public float Top { get; }
    }

}
