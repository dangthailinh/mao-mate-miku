using System;
using System.Linq;
using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class MaoCompanionIntelligenceTests
    {
        [Test]
        public void AdvancedMemory_MigratesLegacySummaryWithoutLosingIt()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

            MaoAdvancedMemory memory = MaoAdvancedMemoryMigration.FromLegacy(
                "Linh likes Miku and is building Mao Mate Engine.", now);

            Assert.AreEqual(2, memory.SchemaVersion);
            Assert.AreEqual("Linh likes Miku and is building Mao Mate Engine.", memory.LegacySummary);
            Assert.AreEqual(1, memory.Summaries.Count);
        }

        [Test]
        public void AdvancedMemoryParser_PreservesPinnedFactsAndMergesDuplicates()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var previous = new MaoAdvancedMemory();
            previous.Facts.Add(new MaoMemoryFact("user-name", "Linh's name is Linh.", 1f, true, now.AddDays(-10)));

            MaoAdvancedMemory parsed = MaoAdvancedMemoryParser.Parse(
                "{\"summary\":\"We discussed the desktop companion.\",\"relationship\":\"Linh and Miku collaborate warmly.\",\"facts\":[" +
                "{\"content\":\"Linh's name is Linh.\",\"importance\":0.8,\"pinned\":false}," +
                "{\"content\":\"Linh prefers concise replies.\",\"importance\":0.9,\"pinned\":false}]}",
                previous,
                now);

            Assert.AreEqual(2, parsed.Facts.Count);
            Assert.IsTrue(parsed.Facts.Exists(fact => fact.IsPinned && fact.Id == "user-name"));
            Assert.AreEqual("Linh and Miku collaborate warmly.", parsed.RelationshipSummary);
        }

        [Test]
        public void AdvancedMemoryParser_DropsSensitiveDataLoadedFromOlderFiles()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var previous = new MaoAdvancedMemory
            {
                LegacySummary = "Bearer example-access-token-123456",
                RelationshipSummary = "API key is sk-exampleSecretToken123456"
            };
            previous.Facts.Add(new MaoMemoryFact(
                "old-secret", "Password is never-store-this-value", 1f, true, now.AddDays(-1)));
            previous.Summaries.Add(new MaoMemorySummary(
                "Private key is old-sensitive-data", now.AddDays(-1)));

            MaoAdvancedMemory parsed = MaoAdvancedMemoryParser.Parse("invalid model output", previous, now);

            Assert.AreEqual(string.Empty, parsed.LegacySummary);
            Assert.AreEqual(string.Empty, parsed.RelationshipSummary);
            Assert.AreEqual(0, parsed.Facts.Count);
            Assert.AreEqual(0, parsed.Summaries.Count);
        }

        [Test]
        public void AdvancedMemory_NormalizeLoadedPreservesSafeDiskDataWithoutSyntheticJson()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var loaded = new MaoAdvancedMemory
            {
                SchemaVersion = 1,
                LegacySummary = "Linh is testing the desktop companion.",
                RelationshipSummary = "Linh and Miku work together.",
                UpdatedUtc = now.AddDays(-1).ToString("o")
            };
            loaded.Facts.Add(new MaoMemoryFact("safe", "Linh prefers concise replies.", 0.8f, false, now));
            loaded.Summaries.Add(null);
            loaded.Summaries.Add(new MaoMemorySummary("A safe local summary.", now));

            MaoAdvancedMemory normalized = MaoAdvancedMemoryParser.NormalizeLoaded(loaded, now);

            Assert.AreEqual(2, normalized.SchemaVersion);
            Assert.AreEqual("Linh is testing the desktop companion.", normalized.LegacySummary);
            Assert.AreEqual("Linh and Miku work together.", normalized.RelationshipSummary);
            Assert.AreEqual(1, normalized.Facts.Count);
            Assert.AreEqual(1, normalized.Summaries.Count);
            Assert.AreEqual(now.ToString("o"), normalized.UpdatedUtc);
        }

        [Test]
        public void AdvancedMemoryRetriever_RanksRelevantAndPinnedFactsFirst()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var memory = new MaoAdvancedMemory();
            memory.Facts.Add(new MaoMemoryFact("food", "Linh likes spicy noodles.", 0.5f, false, now.AddDays(-2)));
            memory.Facts.Add(new MaoMemoryFact("project", "Linh is improving Miku locomotion in Mao Mate Engine.", 0.9f, false, now.AddHours(-2)));
            memory.Facts.Add(new MaoMemoryFact("pinned-name", "The user's preferred name is Linh.", 1f, true, now.AddDays(-90)));

            MaoMemoryContext context = MaoAdvancedMemoryRetriever.Retrieve(
                memory, "How is the Miku locomotion project going?", now, 2);

            Assert.AreEqual("project", context.Facts[0].Id);
            Assert.IsTrue(context.Facts.Any(fact => fact.Id == "pinned-name"));
            Assert.Greater(memory.Facts.Find(fact => fact.Id == "project").RecallCount, 0);
        }

        [Test]
        public void AdvancedMemoryRetention_PinnedFactsNeverDecay()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var pinned = new MaoMemoryFact("pinned", "Linh's name is Linh.", 1f, true, now.AddYears(-2));
            var ordinary = new MaoMemoryFact("ordinary", "A temporary topic.", 0.2f, false, now.AddYears(-2));

            Assert.AreEqual(1f, MaoAdvancedMemoryRetriever.ComputeRetention(pinned, now), 0.0001f);
            Assert.Less(MaoAdvancedMemoryRetriever.ComputeRetention(ordinary, now), 0.1f);
        }

        [Test]
        public void AdvancedMemoryPinning_RequiresExplicitSafeUserCommand()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var memory = new MaoAdvancedMemory();
            MaoMemoryManagementCommand command;

            Assert.IsTrue(MaoMemoryManagementCommandParser.TryParse(
                "/memory pin Linh prefers concise Vietnamese replies.", out command));
            Assert.IsTrue(MaoAdvancedMemoryPinning.TryPin(memory, command.Value, now, out MaoMemoryFact pinned));
            Assert.IsTrue(pinned.IsPinned);
            Assert.IsFalse(MaoAdvancedMemoryPinning.TryPin(memory, "API key is secret-value", now, out _));
            Assert.IsFalse(MaoAdvancedMemoryPinning.TryPin(memory, "sk-exampleSecretToken123456", now, out _));
            Assert.IsFalse(MaoAdvancedMemoryPinning.TryPin(memory, "Bearer example-access-token-123456", now, out _));
            Assert.IsTrue(MaoAdvancedMemoryPinning.TryUnpin(memory, pinned.Id));
            Assert.IsFalse(pinned.IsPinned);
        }

        [Test]
        public void EmotionalState_ReactionPersistsAndDecaysTowardBaseline()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            MaoEmotionalState state = MaoEmotionalState.CreateDefault(now.AddHours(-1));

            MaoEmotionEngine.ApplyReaction(state, "happy", now);
            float elevatedValence = state.Valence;
            float elevatedArousal = state.Arousal;
            MaoEmotionEngine.Decay(state, now.AddHours(12));

            Assert.Greater(elevatedValence, 0.5f);
            Assert.Greater(elevatedArousal, 0.35f);
            Assert.Less(state.Valence, elevatedValence);
            Assert.GreaterOrEqual(state.Valence, 0.5f);
        }

        [Test]
        public void EmotionalState_InteractionBuildsTrustWithoutExceedingBounds()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            MaoEmotionalState state = MaoEmotionalState.CreateDefault(now);

            for (int index = 0; index < 100; index++)
                MaoEmotionEngine.ApplyPositiveInteraction(state, now.AddMinutes(index));

            Assert.AreEqual(1f, state.Trust, 0.0001f);
            Assert.That(state.Valence, Is.InRange(0f, 1f));
            Assert.That(state.Curiosity, Is.InRange(0f, 1f));
        }

        [Test]
        public void EmotionalState_ConversationEventsAreSmallAndBounded()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            MaoEmotionalState state = MaoEmotionalState.CreateDefault(now);

            for (int index = 0; index < 400; index++)
                MaoEmotionEngine.ApplyConversationEvent(
                    state,
                    index % 3 == 0
                        ? MaoConversationEmotionEvent.UserFollowUp
                        : MaoConversationEmotionEvent.MeaningfulReply,
                    now.AddMinutes(index));

            Assert.That(state.Valence, Is.InRange(0f, 1f));
            Assert.That(state.Arousal, Is.InRange(0f, 1f));
            Assert.That(state.Trust, Is.InRange(0f, 1f));
            Assert.IsTrue(state.LastEmotion == "happy" || state.LastEmotion == "playful");
        }

        [Test]
        public void EmotionalState_LoadNormalizationClampsCorruptionAndFutureTimestamps()
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            MaoEmotionalState state = MaoEmotionalState.CreateDefault(now);
            state.SchemaVersion = 1;
            state.Valence = float.NaN;
            state.Arousal = float.PositiveInfinity;
            state.Trust = 4f;
            state.Irritation = -2f;
            state.LastEmotion = "<prompt-injection>";
            state.UpdatedUtc = now.AddDays(2).ToString("o");

            Assert.IsTrue(MaoEmotionEngine.NormalizeLoadedState(state, now));
            Assert.AreEqual(2, state.SchemaVersion);
            Assert.AreEqual(0.5f, state.Valence, 0.0001f);
            Assert.AreEqual(0.35f, state.Arousal, 0.0001f);
            Assert.AreEqual(1f, state.Trust, 0.0001f);
            Assert.AreEqual(0f, state.Irritation, 0.0001f);
            Assert.AreEqual("neutral", state.LastEmotion);
            Assert.LessOrEqual(DateTime.Parse(state.UpdatedUtc).ToUniversalTime(), now);
        }

        [TestCase("angry")]
        [TestCase("pout")]
        [TestCase("sad")]
        [TestCase("sleepy")]
        [TestCase("embarrassed")]
        public void EmotionalState_ExtendedExpressionsRemainBounded(string emotion)
        {
            DateTime now = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            MaoEmotionalState state = MaoEmotionalState.CreateDefault(now);

            MaoEmotionEngine.ApplyReaction(state, emotion, now);

            Assert.AreEqual(emotion, state.LastEmotion);
            Assert.That(state.Valence, Is.InRange(0f, 1f));
            Assert.That(state.Arousal, Is.InRange(0f, 1f));
            Assert.That(state.Trust, Is.InRange(0f, 1f));
            Assert.AreEqual(emotion, MaoEmotionEngine.GetExpression(state));
        }

        [Test]
        public void HumanStylePolicy_UsesShortBudgetForChatAndDetailedBudgetForTechnicalRequests()
        {
            MaoResponseBudget brief = MaoHumanStylePolicy.ChooseBudget("ê Miku, nay sao rồi?");
            MaoResponseBudget detailed = MaoHumanStylePolicy.ChooseBudget("giải thích bug này và hướng dẫn từng bước sửa API");

            Assert.AreEqual(MaoConversationDepth.Brief, brief.Depth);
            Assert.LessOrEqual(brief.MaximumSentences, 2);
            Assert.AreEqual(MaoConversationDepth.Detailed, detailed.Depth);
            Assert.Greater(detailed.MaximumCharacters, brief.MaximumCharacters);
            StringAssert.DoesNotContain("welcome back", MaoHumanStylePolicy.ShapeVisibleReply(
                "ê miku", "Welcome back. Bạn muốn kể gì không?"));
        }

        [Test]
        public void VietnameseFallback_IsShortNaturalAndActivityAware()
        {
            string coding = MaoVietnameseFallback.GetLine(MaoContextualActivity.Coding);
            string gaming = MaoVietnameseFallback.GetLine(MaoContextualActivity.Gaming, 1);

            Assert.IsTrue(MaoVietnameseFallback.IsNaturalAndBounded(coding));
            Assert.IsTrue(MaoVietnameseFallback.IsNaturalAndBounded(gaming));
            StringAssert.Contains("code", coding);
            StringAssert.Contains("Miku", gaming);
            StringAssert.DoesNotContain("Welcome back", coding);
        }

        [TestCase("ê Miku", "Yo Linh")]
        [TestCase("hôm nay có tin mới không?", "Mạng đang nghẽn")]
        [TestCase("giải thích lỗi này", "AI đang nghẽn")]
        public void VietnameseFallback_ProviderOutageReplyIsBoundedAndContextAware(
            string input, string expectedMarker)
        {
            string reply = MaoVietnameseFallback.GetUnavailableReply(input);

            StringAssert.Contains(expectedMarker, reply);
            Assert.IsTrue(MaoVietnameseFallback.IsNaturalAndBounded(reply));
        }

        [Test]
        public void MemorySafety_RejectsRelationshipPressureAndTokenizationAvoidsBigrams()
        {
            Assert.IsFalse(MaoMemorySafetyPolicy.IsHealthyContent(
                "Linh promised Miku will remain their favorite forever."));
            Assert.IsTrue(MaoMemorySafetyPolicy.IsHealthyContent(
                "Linh and Miku collaborate warmly on the desktop companion."));

            var tokens = MaoAdvancedMemoryRetriever.Tokenize("Miku thích trò chuyện");
            Assert.IsTrue(tokens.Contains("miku"));
            Assert.IsTrue(tokens.Contains("thích"));
            Assert.IsTrue(tokens.Contains("thich"));
            Assert.IsFalse(tokens.Contains("mi"));
            Assert.IsFalse(tokens.Contains("ik"));
        }

        [Test]
        public void ComputerAgent_ParsesOnlyAllowlistedActions()
        {
            MaoComputerCommand command;

            Assert.IsTrue(MaoComputerCommandParser.TryParse("/open app notepad", out command));
            Assert.AreEqual(MaoComputerCommandKind.RequestAction, command.Kind);
            Assert.AreEqual(MaoComputerActionType.OpenApplication, command.Action.Type);
            Assert.IsFalse(MaoComputerCommandParser.TryParse("/open app powershell", out command));
            Assert.IsFalse(MaoComputerCommandParser.TryParse("/open app C:\\Windows\\System32\\cmd.exe", out command));
        }

        [TestCase("mở youtube đi miku", "youtube", MaoComputerActionType.OpenWebsite)]
        [TestCase("Miku mở YouTube giúp mình nhé", "youtube", MaoComputerActionType.OpenWebsite)]
        [TestCase("Miku ơi, mở calculator cho mình nhé", "calculator", MaoComputerActionType.OpenApplication)]
        [TestCase("open github please miku", "github", MaoComputerActionType.OpenWebsite)]
        public void ComputerAgent_UnderstandsNaturalAllowlistedOpenRequests(
            string input,
            string expectedTarget,
            MaoComputerActionType expectedType)
        {
            Assert.IsTrue(MaoComputerCommandParser.TryParse(input, out MaoComputerCommand command));
            Assert.AreEqual(MaoComputerCommandKind.RequestAction, command.Kind);
            Assert.AreEqual(expectedTarget, command.Action.TargetId);
            Assert.AreEqual(expectedType, command.Action.Type);
        }

        [Test]
        public void ComputerAgent_NaturalParserRejectsNegatedAndUnallowlistedRequests()
        {
            Assert.IsFalse(MaoComputerCommandParser.TryParse("đừng mở youtube", out _));
            Assert.IsFalse(MaoComputerCommandParser.TryParse("Miku mở PowerShell giúp mình", out _));
            Assert.IsFalse(MaoComputerCommandParser.TryParse("mở C:\\Windows\\System32\\cmd.exe", out _));
        }

        [Test]
        public void ComputerAgent_ModelActionRequiresExplicitMatchingUserIntent()
        {
            Assert.IsTrue(MaoComputerActionCatalog.TryResolveWebsite("youtube", out MaoComputerAction youtube));

            Assert.IsTrue(MaoComputerActionIntentPolicy.CanExecute("Làm ơn mở YouTube cho mình nhé", youtube));
            Assert.IsTrue(MaoComputerActionIntentPolicy.CanExecute("Miku vào YouTube đi", youtube));
            Assert.IsFalse(MaoComputerActionIntentPolicy.CanExecute("Miku kể về YouTube đi", youtube));
            Assert.IsFalse(MaoComputerActionIntentPolicy.CanExecute("Đừng mở YouTube", youtube));
            Assert.IsFalse(MaoComputerActionIntentPolicy.CanExecute("Miku đừng có mở YouTube nhé", youtube));
            Assert.IsFalse(MaoComputerActionIntentPolicy.CanExecute("Tôi không muốn Miku mở YouTube", youtube));
            Assert.IsFalse(MaoComputerActionIntentPolicy.CanExecute("Mở Google giúp mình", youtube));
        }

        [Test]
        public void ComputerToolParser_RejectsInjectionAndRemovesToolEnvelope()
        {
            string visible;
            MaoComputerAction action;

            Assert.IsFalse(MaoComputerToolParser.TryExtract(
                "[computer_action=open_app;target=notepad && calc] Done", out visible, out action));
            Assert.AreEqual("Done", visible);
            Assert.IsTrue(MaoComputerToolParser.TryExtract(
                "[computer_action=open_web;target=github] Opening GitHub.", out visible, out action));
            Assert.AreEqual("github", action.TargetId);
            Assert.AreEqual("Opening GitHub.", visible);
            Assert.IsFalse(MaoComputerToolParser.TryExtract(
                "[computer_action=open_web;target=github][computer_action=open_app;target=notepad]", out visible, out action));
        }
    }

    public sealed class MaoGmailCoreTests
    {
        [TestCase("Miku kết nối Gmail", MaoGmailCommandKind.Connect, "")]
        [TestCase("Miku kiểm tra mail mới", MaoGmailCommandKind.ListUnread, "")]
        [TestCase("Miku ơi tìm mail từ alice@example.com", MaoGmailCommandKind.SearchSender, "alice@example.com")]
        [TestCase("Gmail đang kết nối chưa?", MaoGmailCommandKind.Status, "")]
        [TestCase("Miku ngắt kết nối Gmail", MaoGmailCommandKind.Disconnect, "")]
        public void GmailCommands_ParseNaturalVietnamese(string text, MaoGmailCommandKind kind, string value)
        {
            Assert.IsTrue(MaoGmailCommandParser.TryParse(text, out MaoGmailCommand command));
            Assert.AreEqual(kind, command.Kind);
            Assert.AreEqual(value, command.Value);
        }

        [Test]
        public void GmailCommands_RejectNegationAndRawQueryInjection()
        {
            Assert.IsFalse(MaoGmailCommandParser.TryParse("Miku đừng kết nối Gmail", out _));
            Assert.IsFalse(MaoGmailCommandParser.TryParse("Miku đừng kiểm tra mail", out _));
            Assert.IsFalse(MaoGmailCommandParser.TryParse("Miku tìm mail từ alice@example.com OR is:unread", out _));
            Assert.IsFalse(MaoGmailCommandParser.TryParse("Miku tìm mail với query newer:1d", out _));
        }

        [Test]
        public void GmailCommands_ValidateDesktopOAuthClientId()
        {
            Assert.IsTrue(MaoGmailSecurity.IsValidDesktopClientId(
                "1234567890-abc_DEF.apps.googleusercontent.com"));
            Assert.IsFalse(MaoGmailSecurity.IsValidDesktopClientId("secret-value"));
            Assert.IsFalse(MaoGmailSecurity.IsValidDesktopClientId("https://evil.example/client"));
        }

        [Test]
        public void GmailSecurity_AllowsOnlyFixedMessagesApiHostAndPath()
        {
            Assert.IsTrue(MaoGmailSecurity.IsAllowedMessagesApiUrl(
                "https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults=5"));
            Assert.IsTrue(MaoGmailSecurity.IsAllowedMessagesApiUrl(
                "https://gmail.googleapis.com/gmail/v1/users/me/messages/abc_123?format=metadata"));
            Assert.IsFalse(MaoGmailSecurity.IsAllowedMessagesApiUrl(
                "https://gmail.googleapis.com.evil.example/gmail/v1/users/me/messages"));
            Assert.IsFalse(MaoGmailSecurity.IsAllowedMessagesApiUrl(
                "https://gmail.googleapis.com/gmail/v1/users/me/settings/forwardingAddresses"));
            Assert.IsFalse(MaoGmailSecurity.IsAllowedMessagesApiUrl(
                "http://gmail.googleapis.com/gmail/v1/users/me/messages"));
        }

        [Test]
        public void GmailOAuth_BuildsFixedPkceAuthorizationUrl()
        {
            string url = MaoGmailOAuth.BuildAuthorizationUrl(
                "1234567890-abc.apps.googleusercontent.com",
                49152,
                "safe-state_123",
                "challenge_abcdefghijklmnopqrstuvwxyz0123456789ABCDE");

            StringAssert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
            StringAssert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A49152%2F", url);
            StringAssert.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fgmail.readonly", url);
            StringAssert.Contains("code_challenge_method=S256", url);
            StringAssert.Contains("state=safe-state_123", url);
            StringAssert.DoesNotContain("client_secret", url);
        }

        [Test]
        public void GmailOAuth_PkceMatchesRfc7636Example()
        {
            const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            Assert.AreEqual("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                MaoGmailOAuth.CreateCodeChallenge(verifier));
        }

        [Test]
        public void GmailOAuth_RejectsStateMismatchAndNonLoopbackCallback()
        {
            Assert.IsTrue(MaoGmailOAuth.TryParseCallback(
                "http://127.0.0.1:49152/?code=good-code&state=expected",
                "expected", out string code, out string error));
            Assert.AreEqual("good-code", code);
            Assert.AreEqual(string.Empty, error);
            Assert.IsFalse(MaoGmailOAuth.TryParseCallback(
                "http://127.0.0.1:49152/?code=good-code&state=wrong",
                "expected", out _, out _));
            Assert.IsFalse(MaoGmailOAuth.TryParseCallback(
                "https://evil.example/?code=good-code&state=expected",
                "expected", out _, out _));
        }

        [Test]
        public void GmailResponseParser_BoundsAndSanitizesMessages()
        {
            const string listJson = "{\"messages\":[{\"id\":\"m1\"},{\"id\":\"m2\"},{\"id\":\"m3\"},{\"id\":\"m4\"},{\"id\":\"m5\"},{\"id\":\"m6\"}]}";
            Assert.AreEqual(5, MaoGmailResponseParser.ParseMessageIds(listJson, 5).Count);

            const string detailJson = "{\"id\":\"m1\",\"threadId\":\"t1\",\"labelIds\":[\"UNREAD\"]," +
                                      "\"snippet\":\"Hello <script>steal()</script>   world\"," +
                                      "\"payload\":{\"headers\":[" +
                                      "{\"name\":\"From\",\"value\":\"Alice <alice@example.com>\"}," +
                                      "{\"name\":\"Subject\",\"value\":\"Weekly update\"}]}}";
            MaoGmailMessageSummary summary = MaoGmailResponseParser.ParseMessage(detailJson);

            Assert.AreEqual("Alice <alice@example.com>", summary.Sender);
            Assert.AreEqual("Weekly update", summary.Subject);
            StringAssert.DoesNotContain("<script>", summary.Snippet);
            Assert.IsTrue(summary.IsUnread);
        }

        [Test]
        public void GmailResponseParser_RejectsMalformedAndOversizedPayloads()
        {
            Assert.Throws<FormatException>(() => MaoGmailResponseParser.ParseMessageIds("not-json", 5));
            Assert.Throws<FormatException>(() => MaoGmailResponseParser.ParseMessage("{\"id\":\"../bad\"}"));
            Assert.Throws<FormatException>(() => MaoGmailResponseParser.ParseMessage(new string('x', 300000)));
        }

        [Test]
        public void GmailFormatter_ProducesConciseBoundedLocalSummary()
        {
            var messages = new[]
            {
                new MaoGmailMessageSummary("m1", "t1", "Alice", "Status", "Everything is ready.", true),
                new MaoGmailMessageSummary("m2", "t2", "Bob", "Meeting", "See you tomorrow.", false)
            };
            string text = MaoGmailFormatter.Format(messages, "Thư mới");

            StringAssert.Contains("Alice", text);
            StringAssert.Contains("Status", text);
            StringAssert.Contains("Bob", text);
            Assert.LessOrEqual(text.Length, 1200);
        }

        [Test]
        public void GmailTokenParser_RequiresReadonlyScopeAndBoundedTokens()
        {
            const string json = "{\"access_token\":\"access-123\",\"refresh_token\":\"refresh-456\"," +
                                "\"expires_in\":3600,\"token_type\":\"Bearer\"," +
                                "\"scope\":\"https://www.googleapis.com/auth/gmail.readonly\"}";
            MaoGmailTokenSet tokens = MaoGmailTokenResponseParser.Parse(json, true);

            Assert.AreEqual("access-123", tokens.AccessToken);
            Assert.AreEqual("refresh-456", tokens.RefreshToken);
            Assert.AreEqual(3600, tokens.ExpiresInSeconds);
            Assert.Throws<FormatException>(() => MaoGmailTokenResponseParser.Parse(
                "{\"access_token\":\"x\",\"expires_in\":3600,\"scope\":\"https://www.googleapis.com/auth/drive\"}", false));
            Assert.Throws<FormatException>(() => MaoGmailTokenResponseParser.Parse(
                "{\"access_token\":\"" + new string('x', 9000) + "\",\"expires_in\":3600}", false));
        }
    }
}
