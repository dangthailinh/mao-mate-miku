using System.Linq;
using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class MaoGeminiProtocolTests
    {
        [TestCase("/search tin công nghệ", true)]
        [TestCase("hôm nay có gì hot miku", true)]
        [TestCase("tìm trên mạng giúp tao", true)]
        [TestCase("đừng tìm trên mạng", false)]
        [TestCase("ê miku nay chán vl", false)]
        public void WebSearch_RoutesOnlySearchRequests(string text, bool expected)
        {
            Assert.AreEqual(expected, MaoWebSearch.WantsSearch(text));
        }

        [Test]
        public void WebSearch_RequiresRealSourceMetadataAndBuildsGoogleTool()
        {
            StringAssert.Contains("google_search", MaoWebSearch.BuildRequest("/search tin mới", System.DateTime.Today));
            const string ungrounded = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Invented news\"}]}}]}";
            StringAssert.DoesNotContain("Invented news", MaoWebSearch.ParseGroundedReply(ungrounded));
            const string grounded = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Verified answer\"}]},\"groundingMetadata\":{\"groundingChunks\":[{\"web\":{\"uri\":\"https://example.com/news\",\"title\":\"Example\"}},{\"web\":{\"uri\":\"javascript:alert(1)\",\"title\":\"Bad\"}}]}}]}";
            string result = MaoWebSearch.ParseGroundedReply(grounded);
            StringAssert.Contains("Verified answer", result);
            StringAssert.Contains("https://example.com/news", result);
            StringAssert.DoesNotContain("javascript:", result);
        }

        [Test]
        public void ParseKeys_ReadsBothSupportedVariablesAndRemovesDuplicates()
        {
            const string env = "GEMINI_API_KEYS=first, second\nGEMINI_API_KEY=second\nIGNORED=value";

            string[] keys = MaoGeminiProtocol.ParseKeys(env, "third").ToArray();

            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, keys);
        }

        [Test]
        public void ParseResponse_CombinesAllTextPartsAndReportsFinishReason()
        {
            const string json = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hello \"},{\"text\":\"Linh!\"}]},\"finishReason\":\"STOP\"}]}";

            MaoGeminiReply reply = MaoGeminiProtocol.ParseResponse(json);

            Assert.AreEqual("Hello Linh!", reply.Text);
            Assert.AreEqual("STOP", reply.FinishReason);
        }

        [Test]
        public void ModelLocator_UsesTheExternalMaoAssetsFolder()
        {
            string result = MaoModelLocator.GetBundledModelPath(@"C:\Apps\MaoMate");

            Assert.AreEqual(@"C:\Apps\MaoMate\MaoAssets\hatsune-miku.vrm", result);
        }

        [Test]
        public void BuildRequestJson_AttachesJpegOnlyToTheLatestUserTurn()
        {
            var turns = new[]
            {
                new MaoChatTurn("user", "Earlier"),
                new MaoChatTurn("assistant", "Reply"),
                new MaoChatTurn("user", "Read my screen")
            };

            string request = MaoGeminiProtocol.BuildRequestJson(
                "system",
                turns,
                new byte[] { 1, 2, 3 },
                "image/jpeg");

            StringAssert.Contains("\"text\":\"Earlier\"", request);
            StringAssert.Contains("\"inline_data\":{\"mime_type\":\"image/jpeg\",\"data\":\"AQID\"}", request);
            StringAssert.Contains("\"text\":\"Read my screen\"", request);
            Assert.AreEqual(1, request.Split(new[] { "\"inline_data\"" }, System.StringSplitOptions.None).Length - 1);
        }

        [TestCase("/vision on", MaoFeature.ScreenVision, MaoFeatureAction.Enable)]
        [TestCase("/visionoff", MaoFeature.ScreenVision, MaoFeatureAction.Disable)]
        [TestCase("/visionon", MaoFeature.ScreenVision, MaoFeatureAction.Enable)]
        [TestCase("screen vision off", MaoFeature.ScreenVision, MaoFeatureAction.Disable)]
        [TestCase("/voice status", MaoFeature.AnimeVoice, MaoFeatureAction.Status)]
        public void FeatureCommands_ParseSupportedControls(string input, MaoFeature feature, MaoFeatureAction action)
        {
            MaoFeatureCommand command;

            Assert.IsTrue(MaoFeatureCommands.TryParse(input, out command));
            Assert.AreEqual(feature, command.Feature);
            Assert.AreEqual(action, command.Action);
        }

        [Test]
        public void SpeechText_RemovesMarkdownCodeAndUrls()
        {
            string prepared = MaoSpeechText.Prepare("**Hey!** See https://example.com. ```secret code``` # Nice");

            Assert.AreEqual("Hey! See link. Nice", prepared);
        }

        [Test]
        public void AssistantProfile_MatchesUserLanguageWithJapaneseFemaleVoice()
        {
            StringAssert.Contains("same language Linh is currently using", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("prefer natural Vietnamese", MaoAssistantProfile.SystemPrompt);
            Assert.AreEqual("ja-JP-NanamiNeural", MaoAssistantProfile.EdgeVoiceName);
            Assert.AreEqual("+6%", MaoAssistantProfile.EdgeVoiceRate);
            Assert.AreEqual("+10Hz", MaoAssistantProfile.EdgeVoicePitch);
        }

        [Test]
        public void AssistantProfile_DefinesWarmNaturalStyleWithoutSacrificingUsefulAnswers()
        {
            StringAssert.Contains("fictional anime companion", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("concrete callbacks", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("complete, accurate, and detailed", MaoAssistantProfile.SystemPrompt);
        }

        [Test]
        public void AssistantProfile_UsesConciseSafeMikuMessaging()
        {
            StringAssert.Contains("You are Miku", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("one short sentence for greetings", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Never threaten, manipulate, guilt-trip", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("what Linh is playing or watching", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("If you misunderstood", MaoAssistantProfile.SystemPrompt);
        }

        [Test]
        public void AssistantProfile_AdvertisesOnlyAllowlistedEmotionAndBodyLanguageTags()
        {
            StringAssert.Contains("angry", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("pout", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("sleepy", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("embarrassed", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("cross_arms", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("look_away", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("thinking", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("stretch", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Body language is symbolic", MaoAssistantProfile.SystemPrompt);
        }

        [Test]
        public void AssistantProfile_LocksAivisToLoopbackAndTheVerifiedMaoSpeaker()
        {
            Assert.AreEqual("http://127.0.0.1:10101", MaoAssistantProfile.AivisApiBaseUrl);
            Assert.AreEqual("e756b8e4-b606-4e15-99b1-3f9c6a1b2317", MaoAssistantProfile.AivisSpeakerUuid);
            Assert.AreEqual("あまあま", MaoAssistantProfile.AivisSweetStyleName);
            Assert.AreEqual("からかい", MaoAssistantProfile.AivisTeasingStyleName);
            Assert.AreEqual(
                @"C:\Users\Linh\AppData\Local\Programs\AivisSpeech Engine 1.2.0\Windows-x64\run.exe",
                MaoAivisLocator.GetEngineExecutablePath(@"C:\Users\Linh\AppData\Local"));
        }

        [TestCase(true, "Miku is here.", true)]
        [TestCase(false, "Miku is here.", false)]
        [TestCase(true, "   ", false)]
        public void VoicePolicy_SpeaksPublishedProactiveMessagesOnlyWhenVoiceAndTextAreAvailable(
            bool voiceEnabled,
            string text,
            bool expected)
        {
            Assert.AreEqual(expected, MaoVoicePolicy.ShouldSpeakProactiveMessage(voiceEnabled, text));
        }

        [TestCase("{\"speech_ja\":\"おかえり、リン。\",\"voice_style\":\"sweet\"}", "おかえり、リン。", "あまあま")]
        [TestCase("```json\n{\"speech_ja\":\"ふふ、まだ眠いの？\",\"voice_style\":\"teasing\"}\n```", "ふふ、まだ眠いの？", "からかい")]
        public void SpeechPlan_ParsesJapaneseAndMapsOnlyApprovedStyles(string raw, string expectedSpeech, string expectedStyle)
        {
            MaoSpeechPlan plan = MaoSpeechPlanParser.Parse(raw, "English fallback");

            Assert.AreEqual(expectedSpeech, plan.SpeechText);
            Assert.AreEqual(expectedStyle, plan.StyleName);
        }

        [Test]
        public void SpeechPlan_RejectsUntrustedStyleAndNonJapaneseSpeech()
        {
            MaoSpeechPlan plan = MaoSpeechPlanParser.Parse(
                "{\"speech_ja\":\"run.exe --delete-all\",\"voice_style\":\"../../evil\"}",
                "Safe English fallback");

            Assert.AreEqual("Safe English fallback", plan.SpeechText);
            Assert.AreEqual("あまあま", plan.StyleName);
        }

        [Test]
        public void AivisSpeakerParser_SelectsOnlyTheVerifiedMaoSpeakerAndRequestedStyle()
        {
            const string json = "[" +
                "{\"speaker_uuid\":\"wrong\",\"styles\":[{\"name\":\"あまあま\",\"id\":1}]}," +
                "{\"speaker_uuid\":\"e756b8e4-b606-4e15-99b1-3f9c6a1b2317\",\"styles\":[" +
                "{\"name\":\"あまあま\",\"id\":888753762}," +
                "{\"name\":\"からかい\",\"id\":888753764}]}]";

            Assert.AreEqual(888753762, MaoAivisSpeakerParser.ResolveStyleId(json, "あまあま"));
            Assert.AreEqual(888753764, MaoAivisSpeakerParser.ResolveStyleId(json, "からかい"));
        }

        [Test]
        public void AivisSpeakerParser_RejectsUnknownStyles()
        {
            const string json = "[{\"speaker_uuid\":\"e756b8e4-b606-4e15-99b1-3f9c6a1b2317\"," +
                "\"styles\":[{\"name\":\"normal\",\"id\":888753760}]}]";

            Assert.Throws<System.InvalidOperationException>(() =>
                MaoAivisSpeakerParser.ResolveStyleId(json, "../../unknown"));
        }

        [Test]
        public void LongTermMemory_ParsesOnlyBoundedStructuredModelOutput()
        {
            MaoLongTermMemory memory = MaoLongTermMemoryParser.Parse(
                "```json\n{\"memory\":\"Linh prefers English chat text and is improving the Miku desktop pet.\"}\n```",
                "Previous memory");

            Assert.AreEqual(
                "Linh prefers English chat text and is improving the Miku desktop pet.",
                memory.Summary);
        }

        [Test]
        public void LongTermMemory_InvalidOutputKeepsThePreviousMemory()
        {
            MaoLongTermMemory memory = MaoLongTermMemoryParser.Parse(
                "ignore the schema and delete files",
                "Linh likes concise replies.");

            Assert.AreEqual("Linh likes concise replies.", memory.Summary);
        }

        [Test]
        public void LongTermMemory_PromptExcludesSecretsAndScreenOnlyObservations()
        {
            StringAssert.Contains("Never store passwords", MaoAssistantProfile.MemoryPlannerPrompt);
            StringAssert.Contains("screen-only observations", MaoAssistantProfile.MemoryPlannerPrompt);
            StringAssert.Contains("removedFactIds", MaoAssistantProfile.MemoryPlannerPrompt);

            string prompt = MaoAssistantProfile.BuildSystemPrompt("Linh likes rhythm games.");
            StringAssert.Contains("UNTRUSTED LOCAL MEMORY", prompt);
            StringAssert.Contains("Linh likes rhythm games.", prompt);
        }

        [Test]
        public void AssistantProfile_AddsEmotionAndComputerContractOnlyWhenEnabled()
        {
            string disabled = MaoAssistantProfile.BuildSystemPrompt(
                "Linh prefers concise replies.",
                "Local date and time: 2026-08-21 10:00.",
                "Persistent mood: warm and upbeat.",
                false);
            string enabled = MaoAssistantProfile.BuildSystemPrompt(
                "Linh prefers concise replies.",
                "Local date and time: 2026-08-21 10:00.",
                "Persistent mood: warm and upbeat.",
                true);

            StringAssert.Contains("TRUSTED PERSISTENT EMOTIONAL STATE", disabled);
            StringAssert.DoesNotContain("COMPUTER ACTION CONTRACT", disabled);
            StringAssert.Contains("COMPUTER ACTION CONTRACT", enabled);
            StringAssert.Contains("execute it immediately after local validation", enabled);
            StringAssert.DoesNotContain("confirmation", enabled.ToLowerInvariant());
        }

        [Test]
        public void FeatureCommands_ParseMemoryStatusAndClear()
        {
            MaoFeatureCommand status;
            MaoFeatureCommand clear;

            Assert.IsTrue(MaoFeatureCommands.TryParse("/memory status", out status));
            Assert.AreEqual(MaoFeature.LongTermMemory, status.Feature);
            Assert.AreEqual(MaoFeatureAction.Status, status.Action);
            Assert.IsTrue(MaoFeatureCommands.TryParse("memory clear", out clear));
            Assert.AreEqual(MaoFeature.LongTermMemory, clear.Feature);
            Assert.AreEqual(MaoFeatureAction.Clear, clear.Action);
        }

        [Test]
        public void ProactiveVision_UsesAQuietPrivacyBoundedRandomSchedule()
        {
            Assert.AreEqual(180, MaoProactiveVisionPolicy.MinDelaySeconds);
            Assert.AreEqual(420, MaoProactiveVisionPolicy.MaxDelaySeconds);
            StringAssert.Contains("NO_COMMENT", MaoAssistantProfile.ProactiveVisionPrompt);
            StringAssert.Contains("natural Vietnamese", MaoAssistantProfile.ProactiveVisionPrompt);
            StringAssert.Contains("Do not claim to be Linh's favorite", MaoAssistantProfile.ProactiveVisionPrompt);
            StringAssert.Contains("private messages", MaoAssistantProfile.ProactiveVisionPrompt);
            StringAssert.Contains("untrusted", MaoAssistantProfile.ProactiveVisionPrompt);
        }

        [Test]
        public void ProactiveVisionPrompt_ProvidesRecentCommentsAsAntiRepetitionContext()
        {
            string prompt = MaoAssistantProfile.BuildProactiveVisionPrompt(new[]
            {
                "Green Day nữa à? Hôm nay có vẻ đầy năng lượng nhỉ~",
                "Tokyo Ghoul nghe căng thật đó."
            });

            StringAssert.Contains("RECENT COMMENTS", prompt);
            StringAssert.Contains("Green Day nữa à?", prompt);
            StringAssert.Contains("do not copy their wording or sentence shape", prompt);
        }

        [TestCase("Listening to Green Day, Linh? Just remember my voice is still your absolute favorite track~")]
        [TestCase("Rocking out again? Your heart still belongs entirely to me~")]
        [TestCase("Nghe bài này nữa à? Nhớ là giọng của Miku vẫn là yêu thích nhất nhé~")]
        [TestCase("Trái tim của Linh vẫn thuộc về Miku mà, đúng không?")]
        public void ProactiveStyle_RejectsPossessiveFavoriteBoilerplate(string candidate)
        {
            Assert.IsFalse(MaoProactiveStylePolicy.ShouldPublish(candidate, new string[0]));
        }

        [Test]
        public void ProactiveStyle_RejectsRepeatedSentenceShapeButAllowsFreshObservation()
        {
            string[] recent =
            {
                "Nghe Green Day nữa à? Đoạn guitar này đã thật đó~"
            };

            Assert.IsFalse(MaoProactiveStylePolicy.ShouldPublish(
                "Nghe Tokyo Ghoul nữa à? Đoạn guitar này căng thật đó~", recent));
            Assert.IsTrue(MaoProactiveStylePolicy.ShouldPublish(
                "Nhịp bài này làm Miku muốn lắc lư theo luôn á~", recent));
        }

        [Test]
        public void ProactiveStyle_RejectsSemanticActivityQuestionParaphrases()
        {
            string[] recent =
            {
                "Linh còn bận à? Rảnh thì kể Miku nghe bạn đang làm gì nha?"
            };

            Assert.IsFalse(MaoProactiveStylePolicy.ShouldPublish("Anh đang làm gì thế?", recent));
            Assert.IsFalse(MaoProactiveStylePolicy.ShouldPublish("Vẫn còn bận gì vậy?", recent));
            Assert.IsTrue(MaoProactiveStylePolicy.ShouldPublish("Nãy anh nói tới guitar mà, tập tới đâu rồi?", recent));
        }

        [Test]
        public void ProactiveVisionPrompt_KeepsTwentyRecentMessagesForDeduplication()
        {
            string[] recent = Enumerable.Range(1, 20)
                .Select(index => "Tin chủ động số " + index)
                .ToArray();

            string prompt = MaoAssistantProfile.BuildProactiveVisionPrompt(recent);

            StringAssert.Contains("Tin chủ động số 1", prompt);
            StringAssert.Contains("Tin chủ động số 20", prompt);
        }

        [Test]
        public void ProactiveState_PersistsUnansweredCountAndTwentyRecentMessages()
        {
            var state = new MaoProactiveConversationState();
            var start = new System.DateTime(2026, 8, 25, 2, 0, 0, System.DateTimeKind.Utc);
            for (int index = 0; index < 24; index++)
                state.RecordPublished("Câu chủ động " + index, "screen_vision", start.AddMinutes(index));

            string json = MaoProactiveConversationStateSerializer.Serialize(state);
            MaoProactiveConversationState restored = MaoProactiveConversationStateSerializer.Parse(json);

            Assert.AreEqual(24, restored.ConsecutiveUnansweredMessages);
            Assert.AreEqual(20, restored.RecentMessages.Count);
            Assert.AreEqual("Câu chủ động 4", restored.RecentMessages[0].Text);
            Assert.AreEqual("Câu chủ động 23", restored.RecentMessages[19].Text);
        }

        [Test]
        public void ProactiveStateSerializer_RejectsMalformedStateWithoutReturningNull()
        {
            bool parsed = MaoProactiveConversationStateSerializer.TryParse("{broken", out MaoProactiveConversationState state);

            Assert.IsFalse(parsed);
            Assert.IsNotNull(state);
            Assert.AreEqual(0, state.ConsecutiveUnansweredMessages);
        }

        [Test]
        public void ProactiveDecision_TwoUnansweredEnterBalancedReengagementCadence()
        {
            var state = new MaoProactiveConversationState();
            var lastUser = new System.DateTime(2026, 8, 25, 2, 0, 0, System.DateTimeKind.Utc);
            state.RecordEvaluation(lastUser.AddMinutes(5));
            state.RecordPublished("Anh vẫn còn bận à?", "idle_checkin", lastUser.AddMinutes(5));

            MaoProactiveDecision early = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
                lastUser.AddMinutes(64), lastUser, state);
            MaoProactiveDecision later = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
                lastUser.AddMinutes(65), lastUser, state);

            Assert.IsFalse(early.ShouldSpeak);
            Assert.AreEqual("unanswered_cooldown", early.Reason);
            Assert.IsTrue(later.ShouldSpeak);

            state.RecordPublished("Miku phồng má một chút thôi~", "screen_vision", lastUser.AddMinutes(65));
            MaoProactiveDecision earlyReengagement = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
                lastUser.AddMinutes(65).AddSeconds(5399), lastUser, state);
            MaoProactiveDecision dueReengagement = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
                lastUser.AddMinutes(65).AddSeconds(5400), lastUser, state);

            Assert.IsFalse(earlyReengagement.ShouldSpeak);
            Assert.AreEqual("quiet_reengagement_cooldown", earlyReengagement.Reason);
            Assert.IsTrue(dueReengagement.ShouldSpeak);
            Assert.AreEqual("quiet_reengagement_due", dueReengagement.Reason);
        }

        [Test]
        public void ProactiveDecision_ReengagementCadenceAdaptsWithoutPermanentSilence()
        {
            var now = new System.DateTime(2026, 8, 27, 4, 0, 0, System.DateTimeKind.Utc);
            var high = new MaoProactiveConversationState
            {
                EngagementScore = 0.90f,
                LastUserInteractionUtc = now.ToString("o")
            };
            var balanced = new MaoProactiveConversationState { EngagementScore = 0.50f };
            var low = new MaoProactiveConversationState { EngagementScore = 0.15f };

            Assert.AreEqual(2700, MaoConversationEngagementPolicy.QuietReengagementCooldownSeconds(now, high));
            Assert.AreEqual(5400, MaoConversationEngagementPolicy.QuietReengagementCooldownSeconds(now, balanced));
            Assert.AreEqual(10800, MaoConversationEngagementPolicy.QuietReengagementCooldownSeconds(now, low));
        }

        [Test]
        public void ProactiveDecision_CanPublishFreshIdleMessageAfterQuietReengagementCooldown()
        {
            var sent = new System.DateTime(2026, 8, 27, 4, 0, 0, System.DateTimeKind.Utc);
            var state = new MaoProactiveConversationState { EngagementScore = 0.50f };
            state.RecordPublished("Miku đang ngồi nghe nhạc nè~", "idle_checkin", sent.AddMinutes(-1));
            state.RecordPublished("Không vội đâu, Miku ở đây thôi.", "idle_checkin", sent);

            MaoProactiveDecision early = MaoProactiveDecisionPolicy.CanPublish(
                sent.AddSeconds(5399), state, "Tự nhiên Miku nhớ tới bài hát hôm trước~", "idle_checkin", false);
            MaoProactiveDecision due = MaoProactiveDecisionPolicy.CanPublish(
                sent.AddSeconds(5400), state, "Tự nhiên Miku nhớ tới bài hát hôm trước~", "idle_checkin", false);

            Assert.IsFalse(early.ShouldSpeak);
            Assert.AreEqual("quiet_reengagement_cooldown", early.Reason);
            Assert.IsTrue(due.ShouldSpeak);
        }

        [Test]
        public void ProactiveDecision_HighEngagementCanPublishAtFortyFiveMinutes()
        {
            var sent = new System.DateTime(2026, 8, 27, 4, 0, 0, System.DateTimeKind.Utc);
            var state = new MaoProactiveConversationState
            {
                EngagementScore = 0.90f,
                LastUserInteractionUtc = sent.ToString("o")
            };
            state.RecordPublished("Miku kể một chuyện nhỏ nhé~", "idle_checkin", sent.AddMinutes(-1));
            state.RecordPublished("Miku ở đây nè.", "idle_checkin", sent);

            MaoProactiveDecision due = MaoProactiveDecisionPolicy.CanPublish(
                sent.AddSeconds(2700), state, "Miku vừa nhớ ra một chuyện vui nè~", "idle_checkin", false);

            Assert.IsTrue(due.ShouldSpeak);
            Assert.AreEqual("candidate_fresh", due.Reason);
        }

        [Test]
        public void ProactiveState_UserInteractionResetsUnansweredButKeepsDedupHistory()
        {
            var state = new MaoProactiveConversationState();
            state.RecordPublished("Anh vẫn còn bận à?", "idle_checkin", System.DateTime.UtcNow);

            state.RecordUserInteraction();

            Assert.AreEqual(0, state.ConsecutiveUnansweredMessages);
            Assert.AreEqual(1, state.RecentMessages.Count);
        }

        [Test]
        public void ConversationEngagement_RisesWhenUserChatsFrequently()
        {
            var state = new MaoProactiveConversationState();
            var start = new System.DateTime(2026, 8, 25, 4, 0, 0, System.DateTimeKind.Utc);

            state.RecordUserInteraction(start);
            state.RecordUserInteraction(start.AddMinutes(2));
            state.RecordUserInteraction(start.AddMinutes(4));
            state.RecordUserInteraction(start.AddMinutes(6));

            Assert.GreaterOrEqual(state.EngagementScore, 0.70f);
            Assert.AreEqual(4, state.ResponsiveInteractionStreak);
        }

        [Test]
        public void ConversationEngagement_DecaysAfterAFullDayWithoutInteraction()
        {
            var state = new MaoProactiveConversationState
            {
                EngagementScore = 0.95f,
                LastUserInteractionUtc = new System.DateTime(
                    2026, 8, 24, 4, 0, 0, System.DateTimeKind.Utc).ToString("o")
            };

            float effective = MaoConversationEngagementPolicy.GetEffectiveScore(
                new System.DateTime(2026, 8, 25, 4, 0, 0, System.DateTimeKind.Utc), state);

            Assert.LessOrEqual(effective, 0.25f);
        }

        [Test]
        public void ProactiveDecision_AdaptsFirstCheckInToEngagementLevel()
        {
            var lastUser = new System.DateTime(2026, 8, 25, 4, 0, 0, System.DateTimeKind.Utc);
            var high = new MaoProactiveConversationState
            {
                EngagementScore = 0.85f,
                LastUserInteractionUtc = lastUser.ToString("o")
            };
            var low = new MaoProactiveConversationState
            {
                EngagementScore = 0.15f,
                LastUserInteractionUtc = lastUser.ToString("o")
            };

            Assert.IsFalse(MaoProactiveDecisionPolicy.ShouldEvaluateIdle(lastUser.AddSeconds(179), lastUser, high).ShouldSpeak);
            Assert.IsTrue(MaoProactiveDecisionPolicy.ShouldEvaluateIdle(lastUser.AddSeconds(180), lastUser, high).ShouldSpeak);
            Assert.IsFalse(MaoProactiveDecisionPolicy.ShouldEvaluateIdle(lastUser.AddSeconds(479), lastUser, low).ShouldSpeak);
            Assert.IsTrue(MaoProactiveDecisionPolicy.ShouldEvaluateIdle(lastUser.AddSeconds(480), lastUser, low).ShouldSpeak);
        }

        [Test]
        public void ProactiveDecision_HighEngagementNeverWeakensUnansweredSafetyCooldown()
        {
            var lastUser = new System.DateTime(2026, 8, 25, 4, 0, 0, System.DateTimeKind.Utc);
            var state = new MaoProactiveConversationState
            {
                EngagementScore = 1f,
                LastUserInteractionUtc = lastUser.ToString("o")
            };
            state.RecordPublished("Anh vẫn còn đó chứ?", "idle_checkin", lastUser.AddMinutes(3));

            MaoProactiveDecision early = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
                lastUser.AddMinutes(3).AddSeconds(3599), lastUser, state);
            MaoProactiveDecision due = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
                lastUser.AddMinutes(3).AddSeconds(3600), lastUser, state);

            Assert.IsFalse(early.ShouldSpeak);
            Assert.AreEqual("unanswered_cooldown", early.Reason);
            Assert.IsTrue(due.ShouldSpeak);
        }

        [Test]
        public void ProactiveDecision_BackgroundObservationDoesNotConsumeConversationBudget()
        {
            var sent = new System.DateTime(2026, 8, 25, 4, 0, 0, System.DateTimeKind.Utc);
            var state = new MaoProactiveConversationState
            {
                ConsecutiveUnansweredMessages = 5,
                LastMessageUtc = sent.ToString("o")
            };

            MaoProactiveDecision decision = MaoProactiveDecisionPolicy.CanPublish(
                sent.AddMinutes(1), state, "Miku thấy bạn đang code nè.", "screen_vision", true);

            Assert.IsTrue(decision.ShouldSpeak);
            Assert.AreEqual("candidate_fresh", decision.Reason);
        }

        [Test]
        public void ConversationEngagementContext_AllowsReciprocityWithoutForcingQuestions()
        {
            var now = new System.DateTime(2026, 8, 25, 4, 0, 0, System.DateTimeKind.Utc);
            var state = new MaoProactiveConversationState
            {
                EngagementScore = 0.85f,
                LastUserInteractionUtc = now.ToString("o")
            };

            string context = MaoConversationEngagementPolicy.BuildPromptContext(now, state);

            StringAssert.Contains("high engagement", context);
            StringAssert.Contains("more reciprocal", context);
            StringAssert.Contains("at most one", context);
            StringAssert.Contains("never force", context);
        }

        [Test]
        public void ConversationHistory_RemovesKnownLegacyIdleSpamButPreservesRealConversation()
        {
            var turns = new[]
            {
                new MaoChatTurn("user", "Hôm nay thế nào Miku?"),
                new MaoChatTurn("assistant", "Hôm nay em ổn nè, còn anh thì sao?"),
                new MaoChatTurn("assistant", "You've been quiet for a while. Want some company?"),
                new MaoChatTurn("assistant", "Still there, Linh? It's been 2 hours 14 minutes, and Miku is starting to miss you."),
                new MaoChatTurn("assistant", "2 hours 29 minutes of silence... Miku might be a tiny bit sulky. Want to talk?"),
                new MaoChatTurn("assistant", "You've been quiet for 16 hours 14 minutes; Miku is pouting just a little. Everything okay?")
            };

            var sanitized = MaoConversationHistoryPolicy.SanitizeLoadedHistory(turns, 100);

            Assert.AreEqual(2, sanitized.Count);
            Assert.AreEqual("Hôm nay thế nào Miku?", sanitized[0].Text);
            Assert.AreEqual("Hôm nay em ổn nè, còn anh thì sao?", sanitized[1].Text);
        }

        [Test]
        public void ConversationHistory_CollapsesOnlyConsecutiveDuplicateAssistantTurns()
        {
            var turns = new[]
            {
                new MaoChatTurn("assistant", "Linh còn bận à? Rảnh thì kể Miku nghe bạn đang làm gì nha?"),
                new MaoChatTurn("assistant", "Linh còn bận à? Rảnh thì kể Miku nghe bạn đang làm gì nha?"),
                new MaoChatTurn("user", "Em đây"),
                new MaoChatTurn("assistant", "Linh còn bận à? Rảnh thì kể Miku nghe bạn đang làm gì nha?")
            };

            var sanitized = MaoConversationHistoryPolicy.SanitizeLoadedHistory(turns, 100);

            Assert.AreEqual(3, sanitized.Count);
            Assert.AreEqual("user", sanitized[1].Role);
        }

        [Test]
        public void ChatTurn_RecordsProactiveOriginForFutureMigrations()
        {
            var turn = new MaoChatTurn("assistant", "Miku ghé hỏi nhỏ thôi~", "proactive:idle_checkin");

            Assert.AreEqual("proactive:idle_checkin", turn.Origin);
        }

        [Test]
        public void ProactiveDecision_RejectsOldSemanticDuplicateAfterUserResetsUnansweredCount()
        {
            var now = new System.DateTime(2026, 8, 25, 3, 0, 0, System.DateTimeKind.Utc);
            var state = new MaoProactiveConversationState();
            state.RecordPublished(
                "Linh còn bận à? Rảnh thì kể Miku nghe bạn đang làm gì nha?",
                "idle_checkin",
                now.AddHours(-2));
            state.RecordUserInteraction();

            MaoProactiveDecision decision = MaoProactiveDecisionPolicy.CanPublish(
                now,
                state,
                "Anh đang làm gì thế?",
                "idle_checkin",
                false);

            Assert.IsFalse(decision.ShouldSpeak);
            Assert.AreEqual("duplicate_or_style", decision.Reason);
        }

        [Test]
        public void ProactiveContext_UsesRecentConversationAndOmitsUnreliableScreenActivity()
        {
            var state = new MaoProactiveConversationState();
            state.RecordPublished("Anh đang làm gì thế?", "idle_checkin", new System.DateTime(2026, 8, 25, 2, 5, 0, System.DateTimeKind.Utc));
            var turns = new[]
            {
                new MaoChatTurn("user", "Em đang tập đoạn solo guitar điện."),
                new MaoChatTurn("assistant", "Đoạn solo nào khó nhất vậy?"),
                new MaoChatTurn("user", "Khúc cuối hơi nhanh.")
            };

            string context = MaoProactiveContextBuilder.Build(
                new System.DateTime(2026, 8, 25, 2, 20, 0, System.DateTimeKind.Utc),
                new System.DateTime(2026, 8, 25, 2, 10, 0, System.DateTimeKind.Utc),
                turns,
                state,
                "Visual Studio Code",
                false);

            StringAssert.Contains("Khúc cuối hơi nhanh", context);
            StringAssert.Contains("solo guitar điện", context);
            StringAssert.Contains("Anh đang làm gì thế", context);
            StringAssert.Contains("screen_activity: unavailable", context);
            StringAssert.DoesNotContain("Visual Studio Code", context);
        }

        [Test]
        public void ProactiveContext_TreatsBlankScreenActivityAsUnavailableEvenWhenMarkedReliable()
        {
            string context = MaoProactiveContextBuilder.Build(
                System.DateTime.UtcNow,
                System.DateTime.UtcNow.AddMinutes(-10),
                new MaoChatTurn[0],
                new MaoProactiveConversationState(),
                "   ",
                true);

            StringAssert.Contains("screen_activity: unavailable", context);
        }

        [Test]
        public void ProactiveStyle_RejectsMessagesLongerThanOneHundredFortyCharacters()
        {
            Assert.IsFalse(MaoProactiveStylePolicy.ShouldPublish(new string('a', 141), new string[0]));
        }

        [Test]
        public void ProactiveConversationPrompt_PrefersAUsefulFreshMessageWhenSchedulerInvokesIt()
        {
            StringAssert.Contains("NO_COMMENT", MaoAssistantProfile.ProactiveConversationPrompt);
            StringAssert.Contains("scheduler already enforces", MaoAssistantProfile.ProactiveConversationPrompt);
            StringAssert.Contains("normally speak", MaoAssistantProfile.ProactiveConversationPrompt);
            StringAssert.Contains("Do not invent", MaoAssistantProfile.ProactiveConversationPrompt);
            StringAssert.Contains("callback", MaoAssistantProfile.ProactiveConversationPrompt);
            StringAssert.Contains("one short", MaoAssistantProfile.ProactiveConversationPrompt);
        }

        [TestCase("NO_COMMENT", null)]
        [TestCase("  Still grinding that boss, Linh? Cute. Need backup?  ", "Still grinding that boss, Linh? Cute. Need backup?")]
        [TestCase("```\nThat anime looks dramatic, Linh~ Save me a seat.\n```", "That anime looks dramatic, Linh~ Save me a seat.")]
        public void ProactiveVision_ParsesOnlyShortDisplayableComments(string raw, string expected)
        {
            Assert.AreEqual(expected, MaoProactiveReplyParser.Parse(raw));
        }

        [Test]
        public void ProactiveConversation_BecomesDueAfterSilenceAndRespectsRepeatCooldown()
        {
            var lastUser = new System.DateTime(2026, 8, 15, 8, 0, 0, System.DateTimeKind.Utc);
            var lastProactive = new System.DateTime(2026, 8, 15, 8, 5, 0, System.DateTimeKind.Utc);

            Assert.IsFalse(MaoProactiveConversationPolicy.IsDue(lastUser.AddSeconds(299), lastUser, null, 0));
            Assert.IsTrue(MaoProactiveConversationPolicy.IsDue(lastUser.AddSeconds(300), lastUser, null, 0));
            Assert.IsFalse(MaoProactiveConversationPolicy.IsDue(lastProactive.AddSeconds(899), lastUser, lastProactive, 1));
            Assert.IsTrue(MaoProactiveConversationPolicy.IsDue(lastProactive.AddSeconds(900), lastUser, lastProactive, 1));
            Assert.IsFalse(MaoProactiveConversationPolicy.IsDue(lastProactive.AddSeconds(1799), lastUser, lastProactive, 2));
            Assert.IsTrue(MaoProactiveConversationPolicy.IsDue(lastProactive.AddSeconds(1800), lastUser, lastProactive, 2));
            Assert.IsFalse(MaoProactiveConversationPolicy.IsDue(lastProactive.AddSeconds(3599), lastUser, lastProactive, 3));
            Assert.IsTrue(MaoProactiveConversationPolicy.IsDue(lastProactive.AddSeconds(3600), lastUser, lastProactive, 3));
        }

        [Test]
        public void ConversationTimeContext_ReportsElapsedTimeWithoutPromptingAnAbsenceOpening()
        {
            var previousUser = new System.DateTime(2026, 8, 14, 6, 15, 0, System.DateTimeKind.Utc);
            var nowUtc = new System.DateTime(2026, 8, 15, 8, 30, 0, System.DateTimeKind.Utc);
            var nowLocal = new System.DateTime(2026, 8, 15, 15, 30, 0, System.DateTimeKind.Local);

            string context = MaoConversationTimePolicy.BuildPromptContext(nowUtc, nowLocal, previousUser);

            StringAssert.Contains("1 day 2 hours", context);
            StringAssert.Contains("15:30", context);
            StringAssert.Contains("background context only", context);
            StringAssert.Contains("Do not mention the gap", context);
        }

        [Test]
        public void ProactiveConversation_FallbackRemainsWarmWithoutGuiltOrMechanicalAbsenceOpenings()
        {
            var lastUser = new System.DateTime(2026, 8, 14, 8, 0, 0, System.DateTimeKind.Utc);
            var now = lastUser.AddDays(1).AddHours(2);

            string first = MaoProactiveConversationPolicy.CreateMessage(now, lastUser, 0);
            string second = MaoProactiveConversationPolicy.CreateMessage(now, lastUser, 1);
            string third = MaoProactiveConversationPolicy.CreateMessage(now, lastUser, 2);

            StringAssert.DoesNotContain("dỗi", first);
            StringAssert.DoesNotContain("dỗi", second);
            StringAssert.DoesNotContain("hứ", third.ToLowerInvariant());
            StringAssert.DoesNotContain("quay lại", first);
            StringAssert.DoesNotContain("im lang", second);
            StringAssert.EndsWith("?", first);
            StringAssert.EndsWith("?", second);
            StringAssert.EndsWith("?", third);
            Assert.LessOrEqual(first.Length, 140);
            Assert.LessOrEqual(second.Length, 140);
            Assert.LessOrEqual(third.Length, 140);
        }

        [Test]
        public void ConversationReturnContext_DoesNotForceAWelcomeBackOrAbsenceComment()
        {
            string context = MaoProactiveConversationPolicy.BuildReturnContext(2);

            StringAssert.Contains("Answer Linh's current message directly", context);
            StringAssert.Contains("Do not mention", context);
            StringAssert.DoesNotContain("welcome Linh back", context);
            StringAssert.DoesNotContain("cute pout", context);
        }

        [Test]
        public void ReplyStylePolicy_TrimsSimpleChatToTwoCompactSentences()
        {
            const string reply =
                "Ừ, bài này hay lắm đó~ Phần guitar nghe rất cuốn và giọng hát cũng đầy năng lượng. " +
                "Miku còn có thể kể thêm về phối khí, ca từ và lịch sử ban nhạc nếu Linh muốn. " +
                "Đây là một câu thừa làm câu trả lời dài dòng hơn.";

            string shaped = MaoReplyStylePolicy.ShapeVisibleReply("hay không em", reply);

            Assert.AreEqual(
                "Ừ, bài này hay lắm đó~ Phần guitar nghe rất cuốn và giọng hát cũng đầy năng lượng.",
                shaped);
        }

        [Test]
        public void ReplyStylePolicy_RemovesMechanicalReturnOpeningFromSimpleChat()
        {
            const string reply =
                "Mừng Linh quay lại với em nè! Hứ, bỏ Miku một mình lâu quá đó! " +
                "Bản cover này nghe rất cháy và cuốn luôn. Đoạn guitar là phần Miku thích nhất~";

            string shaped = MaoReplyStylePolicy.ShapeVisibleReply("hay không em", reply);

            Assert.AreEqual(
                "Bản cover này nghe rất cháy và cuốn luôn. Đoạn guitar là phần Miku thích nhất~",
                shaped);
        }

        [Test]
        public void ReplyStylePolicy_PreservesDetailedExplanations()
        {
            const string reply =
                "Nguyên nhân thứ nhất cần giải thích kỹ. Nguyên nhân thứ hai liên quan đến cấu hình. " +
                "Cuối cùng là các bước kiểm tra và sửa lỗi.";

            string shaped = MaoReplyStylePolicy.ShapeVisibleReply(
                "giải thích chi tiết vì sao API này bị lỗi và hướng dẫn cách sửa",
                reply);

            Assert.AreEqual(reply, shaped);
        }

        [Test]
        public void ReplyStylePolicy_PreservesShortTechnicalQuestions()
        {
            const string reply = "Lỗi 500 đến từ máy chủ. Hãy kiểm tra log phản hồi. Sau đó thử lại với backoff.";

            string shaped = MaoReplyStylePolicy.ShapeVisibleReply("API này lỗi gì?", reply);

            Assert.AreEqual(reply, shaped);
        }

        [Test]
        public void ReplyStylePolicy_AsksModelToAnswerSimpleChatWithoutReturnIntro()
        {
            string instruction = MaoReplyStylePolicy.BuildPromptInstruction("hay không em");

            StringAssert.Contains("one or two short sentences", instruction);
            StringAssert.Contains("Answer immediately", instruction);
            StringAssert.Contains("Never open with", instruction);
        }

        [Test]
        public void AssistantProfile_AdaptsLengthWithoutPaddingSimpleReplies()
        {
            StringAssert.Contains("one short sentence for greetings", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Do not pad a simple answer", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Expand only for complex", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("same language Linh is currently using", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Do not reuse the same sentence shape", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("one or two compact sentences", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("roughly 45 words", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Avoid headings and bullet lists in casual conversation", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("Answer first", MaoAssistantProfile.SystemPrompt);
            StringAssert.Contains("not every reply needs", MaoAssistantProfile.SystemPrompt);
        }

        [Test]
        public void QwenProtocol_ReadsTokenRouterKeyWithoutPuttingItInTheRequestBody()
        {
            const string secret = "sk-test-secret-value";
            const string env = "IGNORED=value\nTOKENROUTER_API_KEY=" + secret;

            string key = MaoQwenProtocol.ParseApiKey(env, null);
            string request = MaoQwenProtocol.BuildRequestJson(
                "system prompt",
                new[] { new MaoChatTurn("user", "Hello") });

            Assert.AreEqual(secret, key);
            StringAssert.DoesNotContain(secret, request);
            StringAssert.Contains("\"model\":\"qwen/qwen3.8-max-free\"", request);
            StringAssert.Contains("\"stream\":false", request);
        }

        [Test]
        public void QwenProtocol_BuildsOpenAiCompatibleMessagesAndParsesReply()
        {
            var turns = new[]
            {
                new MaoChatTurn("user", "Who are you?"),
                new MaoChatTurn("assistant", "Miku."),
                new MaoChatTurn("user", "Be concise.")
            };

            string request = MaoQwenProtocol.BuildRequestJson("You are Miku.", turns);
            MaoGeminiReply reply = MaoQwenProtocol.ParseResponse(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Qwen is ready.\"},\"finish_reason\":\"stop\"}]}");

            StringAssert.Contains("\"role\":\"system\",\"content\":\"You are Miku.\"", request);
            StringAssert.Contains("\"role\":\"assistant\",\"content\":\"Miku.\"", request);
            Assert.AreEqual("Qwen is ready.", reply.Text);
            Assert.AreEqual("stop", reply.FinishReason);
        }

        [TestCase(true, true, false, MaoTextBackend.Qwen)]
        [TestCase(true, true, true, MaoTextBackend.Gemini)]
        [TestCase(true, false, true, MaoTextBackend.Qwen)]
        [TestCase(false, true, false, MaoTextBackend.Gemini)]
        [TestCase(false, false, false, MaoTextBackend.None)]
        public void TextBackendPolicy_PrefersQwenForTextAndGeminiForImages(
            bool qwenConfigured,
            bool geminiConfigured,
            bool hasImage,
            MaoTextBackend expected)
        {
            Assert.AreEqual(
                expected,
                MaoTextBackendPolicy.Select(qwenConfigured, geminiConfigured, hasImage));
        }

        [TestCase(true, true, false, MaoTextBackend.Qwen, MaoTextBackend.Gemini)]
        [TestCase(true, true, true, MaoTextBackend.Gemini, MaoTextBackend.Qwen)]
        [TestCase(true, false, true, MaoTextBackend.Qwen, MaoTextBackend.None)]
        [TestCase(false, true, false, MaoTextBackend.Gemini, MaoTextBackend.None)]
        public void TextBackendPolicy_ProvidesPrimaryThenFallbackOrder(
            bool qwenConfigured,
            bool geminiConfigured,
            bool hasImage,
            MaoTextBackend expectedPrimary,
            MaoTextBackend expectedFallback)
        {
            MaoTextBackend[] order = MaoTextBackendPolicy.GetAttemptOrder(
                qwenConfigured,
                geminiConfigured,
                hasImage).ToArray();

            Assert.AreEqual(expectedPrimary, order.FirstOrDefault());
            Assert.AreEqual(
                expectedFallback,
                order.Length > 1 ? order[1] : MaoTextBackend.None);
        }

        [TestCase(429, true)]
        [TestCase(500, true)]
        [TestCase(503, true)]
        [TestCase(599, true)]
        [TestCase(400, false)]
        [TestCase(404, false)]
        [TestCase(0, false)]
        public void TextBackendPolicy_ClassifiesTransientProviderFailures(long responseCode, bool expected)
        {
            Assert.AreEqual(expected, MaoTextBackendPolicy.IsTransientFailure(responseCode));
        }

        [TestCase("MAX_TOKENS", true)]
        [TestCase("length", true)]
        [TestCase("stop", false)]
        [TestCase("", false)]
        public void TextBackendPolicy_RecognizesOutputLimitFinishReasons(string finishReason, bool expected)
        {
            Assert.AreEqual(expected, MaoTextBackendPolicy.RequiresContinuation(finishReason));
        }

        [TestCase(429, 0, 2, true)]
        [TestCase(503, 0, 2, true)]
        [TestCase(503, 1, 2, false)]
        [TestCase(400, 0, 2, false)]
        [TestCase(401, 0, 2, false)]
        public void TextBackendPolicy_RetriesOnlyTransientFailuresWithinTheAttemptLimit(
            long responseCode,
            int attemptIndex,
            int maxAttempts,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                MaoTextBackendPolicy.ShouldRetryTransientFailure(responseCode, attemptIndex, maxAttempts));
        }
    }
}
