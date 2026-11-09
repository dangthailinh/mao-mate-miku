using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MaoIntegration;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MaoMateBootstrap
{
    private static MaoGeminiChatProvider geminiProvider;
    private static bool smokeTestStarted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register()
    {
        if (geminiProvider == null)
        {
            var serviceObject = new GameObject("Mao Gemini Service");
            UnityEngine.Object.DontDestroyOnLoad(serviceObject);
            geminiProvider = serviceObject.AddComponent<MaoGeminiChatProvider>();
            serviceObject.AddComponent<MaoReactionDirector>();
        }
        else if (geminiProvider.GetComponent<MaoReactionDirector>() == null)
            geminiProvider.gameObject.AddComponent<MaoReactionDirector>();

        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ConfigureMiku();
        if (Environment.GetCommandLineArgs().Contains("--mao-smoke-permissions") && geminiProvider.GetComponent<MaoPermissionSmoke>() == null) geminiProvider.gameObject.AddComponent<MaoPermissionSmoke>();
        if (Environment.GetCommandLineArgs().Contains("--mao-permissions-status") && geminiProvider.GetComponent<MaoPermissionStatusProbe>() == null) geminiProvider.gameObject.AddComponent<MaoPermissionStatusProbe>();
        if (Environment.GetCommandLineArgs().Any(argument => argument == "--mao-smoke-harness") &&
            geminiProvider.GetComponent<MaoHarnessSmoke>() == null)
            geminiProvider.gameObject.AddComponent<MaoHarnessSmoke>();

        bool runChatSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-chat", StringComparison.OrdinalIgnoreCase));
        bool runFeatureSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-features", StringComparison.OrdinalIgnoreCase));
        bool runProactiveSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-proactive", StringComparison.OrdinalIgnoreCase));
        bool runProactiveChatSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-proactive-chat", StringComparison.OrdinalIgnoreCase));
        bool runMovementSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-movement", StringComparison.OrdinalIgnoreCase));
        bool runMotionSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-run-jump", StringComparison.OrdinalIgnoreCase));
        bool runGuitarSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-guitar", StringComparison.OrdinalIgnoreCase));
        bool runIdleActionSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-idle-actions", StringComparison.OrdinalIgnoreCase));
        bool runGravitySmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-gravity", StringComparison.OrdinalIgnoreCase));
        bool runScreenChangeSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-screen-change", StringComparison.OrdinalIgnoreCase));
        bool runComputerAgentSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-computer-agent", StringComparison.OrdinalIgnoreCase));
        bool runGmailSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-gmail", StringComparison.OrdinalIgnoreCase));
        bool runSearchSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-search", StringComparison.OrdinalIgnoreCase));
        bool runDailyBriefSmokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--mao-smoke-daily", StringComparison.OrdinalIgnoreCase));

        if ((runChatSmokeTest || runFeatureSmokeTest || runProactiveSmokeTest || runProactiveChatSmokeTest || runMovementSmokeTest ||
             runMotionSmokeTest || runGuitarSmokeTest || runIdleActionSmokeTest || runGravitySmokeTest ||
             runScreenChangeSmokeTest || runComputerAgentSmokeTest || runGmailSmokeTest || runSearchSmokeTest ||
             runDailyBriefSmokeTest) && !smokeTestStarted)
        {
            smokeTestStarted = true;
            if (runSearchSmokeTest)
            {
                string searchReply = string.Empty;
                geminiProvider.SendSilent("/search trang web chính thức AivisSpeech",
                    value => searchReply = value,
                    () => Debug.Log("[Mao Smoke] Search completed=true grounded=" +
                                    searchReply.Contains("\n\nNguồn web:") +
                                    " unavailable_reported=" + (searchReply.Contains("hạn mức") || searchReply.Contains("chưa kết nối"))));
            }
            if (runChatSmokeTest)
            {
                string smokeReply = string.Empty;
                geminiProvider.SendSilent(
                    "Reply with exactly MAO_CHAT_OK and nothing else.",
                    partial => smokeReply = partial,
                    () => Debug.Log("[Mao Smoke] Chat Backend: " +
                                    (smokeReply == "MAO_CHAT_OK" ? "OK" : "FAILED") +
                                    " (configured=" + geminiProvider.IsConfigured +
                                    "; preferred=" + geminiProvider.PreferredTextBackend +
                                    "; used=" + geminiProvider.LastTextBackendUsed +
                                    "; reply_match=" + (smokeReply == "MAO_CHAT_OK") + ")"));
            }

            if (runFeatureSmokeTest)
            {
                MaoScreenVisionService vision = geminiProvider.GetComponent<MaoScreenVisionService>();
                byte[] jpeg = null;
                string captureError = "service unavailable";
                bool captured = vision != null && vision.TryCaptureJpeg(out jpeg, out captureError);
                Debug.Log(captured
                    ? "[Mao Smoke] Screen Vision capture: OK (" + jpeg.Length + " bytes)"
                    : "[Mao Smoke] Screen Vision capture: FAILED (" + captureError + ")");

                MaoAnimeVoiceService voice = geminiProvider.GetComponent<MaoAnimeVoiceService>();
                if (voice != null)
                {
                    voice.Enabled = true;
                    geminiProvider.StartCoroutine(voice.SpeakRoutine(
                        "おかえり、リン。まおのアニメボイス、準備できたよ。",
                        MaoAssistantProfile.AivisSweetStyleName));
                }
            }

            if (runProactiveSmokeTest)
            {
                MaoScreenVisionService vision = geminiProvider.GetComponent<MaoScreenVisionService>();
                if (vision != null) vision.Enabled = true;
                geminiProvider.ObserveScreenNow(comment =>
                    Debug.Log("[Mao Smoke] Proactive Vision: " +
                              (string.IsNullOrWhiteSpace(comment) ? "NO_COMMENT" : comment)));
            }

            if (runProactiveChatSmokeTest)
                geminiProvider.StartCoroutine(RunProactiveChatSmokeTest());

            if (runMovementSmokeTest)
                geminiProvider.StartCoroutine(RunMovementSmokeTest());
            if (runMotionSmokeTest)
                geminiProvider.StartCoroutine(RunMotionSmokeTest());
            if (runGuitarSmokeTest)
                geminiProvider.StartCoroutine(RunGuitarSmokeTest());
            if (runIdleActionSmokeTest)
                geminiProvider.StartCoroutine(RunIdleActionSmokeTest());
            if (runGravitySmokeTest)
                geminiProvider.StartCoroutine(RunGravitySmokeTest());
            if (runScreenChangeSmokeTest)
            {
                MaoReactionDirector director = geminiProvider.GetComponent<MaoReactionDirector>();
                if (director != null) director.enabled = false;
                Debug.Log("[Mao Smoke] Screen Change Detector: AUTONOMY_DISABLED");
                geminiProvider.StartCoroutine(RunScreenChangeSmokeTest());
            }
            if (runComputerAgentSmokeTest)
            {
                MaoComputerAgentRuntime agent = geminiProvider.GetComponent<MaoComputerAgentRuntime>();
                string reply = string.Empty;
                bool enabled = agent != null && agent.TryHandleUserCommand("/computer on", out _);
                bool handled = agent != null && agent.TryHandleUserCommand("mở notepad đi miku", out reply);
                Debug.Log("[Mao Smoke] Computer Agent: " +
                          (enabled && handled && reply.Contains("Miku đã mở Notepad") ? "OK" : "FAILED") +
                          " handled=" + handled + " reply=" + (reply ?? string.Empty));
            }
            if (runGmailSmokeTest)
                geminiProvider.StartCoroutine(RunGmailSmokeTest());
            if (runDailyBriefSmokeTest)
                RunDailyBriefSmokeTest();
        }
    }

    /// <summary>
    /// Offline, deterministic smoke for the daily brief's parser/formatter/
    /// delivery contracts. It intentionally does not contact weather/news APIs
    /// and therefore is safe to run in CI or while diagnosing network outages.
    /// </summary>
    private static void RunDailyBriefSmokeTest()
    {
        System.DateTime now = new System.DateTime(2026, 9, 29, 9, 0, 0, System.DateTimeKind.Unspecified);
        bool parsed = MaoDailyBriefCommandParser.TryParse("nhắc tôi 10 phút nữa uống nước", now, out MaoDailyBriefCommand command) &&
                      command != null && command.Kind == MaoDailyBriefCommandKind.AddReminder;
        var reminderState = new MaoReminderState();
        if (parsed) reminderState.Reminders.Add(command.Reminder);
        bool dueBefore = MaoReminderScheduler.GetDue(reminderState, now).Count == 0;
        bool dueAfter = MaoReminderScheduler.GetDue(reminderState, now.AddMinutes(11)).Count == 1;
        bool marked = MaoReminderScheduler.MarkDelivered(reminderState, command?.Reminder?.Id, now.AddMinutes(11));
        bool once = MaoReminderScheduler.GetDue(reminderState, now.AddMinutes(11)).Count == 0;
        bool weather = false;
        try
        {
            MaoWeatherSnapshot snapshot = MaoWeatherParser.ParseForecast(
                "{\"current\":{\"temperature_2m\":29.4,\"weather_code\":61}," +
                "\"daily\":{\"temperature_2m_min\":[24.1],\"temperature_2m_max\":[32.8]}}", now.Date);
            weather = snapshot.Available && snapshot.TemperatureC > 29f;
        }
        catch { weather = false; }
        // Keep this fixture aligned with the production RSS shape (including
        // a publication date and escaped/CDATA text) so the smoke exercises
        // the same XML-reader path as a real feed, not a Unity-specific
        // minimal XML shortcut.
        string newsXml = "<?xml version=\"1.0\"?><rss><channel>" +
                         "<item><title>Smoke news</title><description><![CDATA[Offline fixture]]></description>" +
                         "<pubDate>Tue, 29 Sep 2026 02:00:00 GMT</pubDate>" +
                         "<link>https://vnexpress.net/smoke</link></item>" +
                         "</channel></rss>";
        bool news = false;
        int newsCount = 0;
        try
        {
            newsCount = MaoNewsRssParser.Parse(newsXml, 3).Count();
            news = newsCount > 0;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("[Mao Smoke] Daily news fixture exception_type=" + exception.GetType().Name +
                             " message=" + exception.Message +
                             (exception.InnerException == null ? string.Empty :
                              " inner_type=" + exception.InnerException.GetType().Name +
                              " inner_message=" + exception.InnerException.Message));
        }
        string brief = MaoDailyBriefFormatter.Format(
            now,
            new MaoDailyLocation("Đà Nẵng", 16.05, 108.2, "Asia/Ho_Chi_Minh"),
            MaoWeatherSnapshot.Unavailable(now.Date),
            reminderState.Reminders);
        bool formatted = brief.Contains("Đà Nẵng") && brief.Contains("không lấy được thời tiết");
        Debug.Log("[Mao Smoke] Daily Brief: " +
                  (parsed && dueBefore && dueAfter && marked && once && weather && news && formatted ? "OK" : "FAILED") +
                  " parser=" + parsed + " scheduler=" + (dueBefore && dueAfter && marked && once) +
                  " weather_fixture=" + weather + " news_fixture=" + news + " news_count=" + newsCount + " formatter=" + formatted);
    }

    private static IEnumerator RunGmailSmokeTest()
    {
        MaoGmailRuntime gmail = geminiProvider.Gmail;
        MaoGmailCommand command = null;
        bool parsed = gmail != null && gmail.TryParseCommand("Gmail đang kết nối chưa?", out command);
        string reply = string.Empty;
        if (parsed) yield return gmail.ExecuteRoutine(command, value => reply = value ?? string.Empty);
        bool dpapiOk = false;
        bool loopbackOk = MaoWindowsLoopbackCallback.SmokeRoundTrip();
        byte[] plain = Encoding.UTF8.GetBytes("mao-gmail-dpapi-smoke");
        byte[] protectedValue = null;
        byte[] roundTrip = null;
        try
        {
            protectedValue = MaoWindowsDataProtector.Protect(plain);
            roundTrip = MaoWindowsDataProtector.Unprotect(protectedValue);
            dpapiOk = plain.SequenceEqual(roundTrip);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Smoke] Gmail DPAPI failed error_type=" + exception.GetType().Name);
        }
        finally
        {
            Array.Clear(plain, 0, plain.Length);
            if (protectedValue != null) Array.Clear(protectedValue, 0, protectedValue.Length);
            if (roundTrip != null) Array.Clear(roundTrip, 0, roundTrip.Length);
        }
        Debug.Log("[Mao Smoke] Gmail Readonly: " +
                  (parsed && reply.Contains("Gmail") && dpapiOk && loopbackOk ? "OK" : "FAILED") +
                  " parsed=" + parsed + " has_reply=" + (!string.IsNullOrWhiteSpace(reply)) +
                  " dpapi=" + dpapiOk + " loopback=" + loopbackOk);
    }

    private static IEnumerator RunScreenChangeSmokeTest()
    {
        MaoScreenVisionService vision = geminiProvider.GetComponent<MaoScreenVisionService>();
        if (vision == null)
        {
            Debug.Log("[Mao Smoke] Screen Change Detector: FAILED (service unavailable)");
            yield break;
        }

        vision.Enabled = true;
        yield return new WaitForSecondsRealtime(8f);
        bool baselineCaptured = vision.TryCaptureForVision(false, out byte[] jpeg, out string error);
        if (!baselineCaptured)
        {
            Debug.Log("[Mao Smoke] Screen Change Detector: FAILED (" + error + ")");
            yield break;
        }
        vision.MarkVisionAccepted("smoke", "verification", "baseline_captured");
        Debug.Log("[Mao Smoke] Screen Change Detector: BASELINE_OK jpeg_bytes=" + jpeg.Length);

        for (int sample = 1; sample <= 13; sample++)
        {
            yield return new WaitForSecondsRealtime(5.2f);
            MaoScreenChangeResult result = vision.LatestChange;
            Debug.Log("[Mao Smoke] Screen Change Detector: SAMPLE=" + sample +
                      " checks=" + vision.LocalCheckCount +
                      " decision=" + result.Reason +
                      " difference=" + result.Difference.ToString("0.000") +
                      " motion=" + result.MotionDifference.ToString("0.000") +
                      " ready=" + result.ShouldRequestVision.ToString().ToLowerInvariant() +
                      " vision_request_reservations=" + vision.VisionRequestCount);
        }
    }

    private static IEnumerator RunProactiveChatSmokeTest()
    {
        LLMUnitySamples.ChatBot chat = null;
        float deadline = Time.realtimeSinceStartup + 12f;
        while (chat == null && Time.realtimeSinceStartup < deadline)
        {
            chat = UnityEngine.Object
                .FindObjectsByType<LLMUnitySamples.ChatBot>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault();
            if (chat == null) yield return new WaitForSecondsRealtime(0.25f);
        }

        var activatedForTest = new List<GameObject>();
        if (chat != null && !chat.gameObject.activeInHierarchy)
        {
            var hierarchy = new List<GameObject>();
            Transform current = chat.transform;
            while (current != null)
            {
                hierarchy.Add(current.gameObject);
                current = current.parent;
            }
            for (int index = hierarchy.Count - 1; index >= 0; index--)
            {
                if (hierarchy[index].activeSelf) continue;
                hierarchy[index].SetActive(true);
                activatedForTest.Add(hierarchy[index]);
            }
            yield return null;
            yield return null;
        }

        const string marker = "Miku proactive console check: are you there?";
        int beforeCount = chat != null ? chat.VisibleMessageCount : -1;
        bool scheduleDue = MaoProactiveConversationPolicy.IsDue(
            DateTime.UtcNow,
            DateTime.UtcNow.AddSeconds(-MaoProactiveConversationPolicy.FirstCheckInSeconds),
            null);
        geminiProvider.PublishEphemeralProactiveMessage(marker);
        yield return new WaitForSecondsRealtime(0.35f);

        bool published = geminiProvider.LastPublishedProactiveMessage == marker;
        bool displayed = chat != null && chat.LastProactiveMessageDisplayed == marker &&
                         chat.VisibleMessageCount == beforeCount + 1;
        AvatarRandomMessages[] avatarMessageSurfaces = UnityEngine.Object
            .FindObjectsByType<AvatarRandomMessages>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        bool avatarBarHidden = avatarMessageSurfaces.Length > 0 &&
                               avatarMessageSurfaces.All(surface =>
                                   surface.LastExternalMessageDisplayed != marker);
        var state = new MaoProactiveConversationState();
        DateTime policyNow = DateTime.UtcNow;
        state.RecordPublished("Linh còn bận à? Rảnh thì kể Miku nghe bạn đang làm gì nha?", "idle_checkin", policyNow.AddHours(-2));
        state.RecordUserInteraction();
        MaoProactiveDecision duplicate = MaoProactiveDecisionPolicy.CanPublish(
            policyNow, state, "Anh đang làm gì thế?", "idle_checkin", false);
        state.RecordPublished("Một câu mới", "screen_vision", policyNow.AddMinutes(-2));
        state.RecordPublished("Một câu mới khác", "screen_vision", policyNow.AddMinutes(-1));
        MaoProactiveDecision quietReengagement = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
            policyNow, policyNow.AddHours(-3), state);
        bool statePolicyPassed = !duplicate.ShouldSpeak && duplicate.Reason == "duplicate_or_style" &&
                                 !quietReengagement.ShouldSpeak &&
                                 quietReengagement.Reason == "quiet_reengagement_cooldown";
        var highEngagement = new MaoProactiveConversationState
        {
            EngagementScore = 0.85f,
            LastUserInteractionUtc = policyNow.ToString("o")
        };
        var lowEngagement = new MaoProactiveConversationState
        {
            EngagementScore = 0.15f,
            LastUserInteractionUtc = policyNow.ToString("o")
        };
        bool adaptiveRhythmPassed =
            MaoConversationEngagementPolicy.FirstIdleDelaySeconds(policyNow, highEngagement) == 180 &&
            MaoConversationEngagementPolicy.FirstIdleDelaySeconds(policyNow, lowEngagement) == 480 &&
            MaoConversationEngagementPolicy.QuietEvaluationCooldownSeconds(policyNow, highEngagement) == 600 &&
            MaoConversationEngagementPolicy.QuietEvaluationCooldownSeconds(policyNow, lowEngagement) == 1800;
        Debug.Log("[Mao Smoke] Proactive Conversation: " +
                  (scheduleDue && published && displayed && avatarBarHidden &&
                   statePolicyPassed && adaptiveRhythmPassed &&
                   geminiProvider.ProactiveConversationEnabled ? "OK" : "FAILED") +
                  " (enabled=" + geminiProvider.ProactiveConversationEnabled +
                  ";schedule_due=" + scheduleDue +
                  ";published=" + published +
                  ";console_displayed=" + displayed +
                  ";avatar_bar_hidden=" + avatarBarHidden +
                  ";state_policy=" + statePolicyPassed +
                  ";adaptive_rhythm=" + adaptiveRhythmPassed + ")");

        for (int index = activatedForTest.Count - 1; index >= 0; index--)
            if (activatedForTest[index] != null) activatedForTest[index].SetActive(false);
    }

    private static IEnumerator RunGravitySmokeTest()
    {
        MaoReactionDirector director = geminiProvider.GetComponent<MaoReactionDirector>();
        float deadline = Time.realtimeSinceStartup + 18f;
        int stableAvatarId = 0;
        float stableSince = Time.realtimeSinceStartup;
        while (director != null && Time.realtimeSinceStartup < deadline)
        {
            int currentAvatarId = director.BoundAvatarInstanceId;
            if (currentAvatarId != stableAvatarId)
            {
                stableAvatarId = currentAvatarId;
                stableSince = Time.realtimeSinceStartup;
            }
            if (director.GravityReady && stableAvatarId != 0 &&
                Time.realtimeSinceStartup - stableSince >= 2f)
                break;
            yield return new WaitForSecondsRealtime(0.25f);
        }

        director?.DebugSetGravityPhysicalDragSuppressed(true);
        bool enabled = director != null && director.SetGravityEnabled(true);
        yield return new WaitForSecondsRealtime(0.15f);
        bool started = enabled && director.DebugLiftAndDropGravity(420);
        bool sawFalling = false;
        bool sawImpact = false;
        bool sawStanding = false;
        bool capturedImpact = false;
        bool capturedStanding = false;
        float impactSeenAt = -1f;
        float standingSeenAt = -1f;
        deadline = Time.realtimeSinceStartup + 24f;
        while (director != null && Time.realtimeSinceStartup < deadline)
        {
            MikuGravityPhase phase = director.GravityPhase;
            sawFalling |= phase == MikuGravityPhase.Falling && director.GravityAnimationActive;
            sawImpact |= phase == MikuGravityPhase.Impact && director.GravityAnimationActive;
            sawStanding |= phase == MikuGravityPhase.StandingUp && director.GravityAnimationActive;
            if (phase == MikuGravityPhase.Impact && impactSeenAt < 0f) impactSeenAt = Time.realtimeSinceStartup;
            if (phase == MikuGravityPhase.StandingUp && standingSeenAt < 0f) standingSeenAt = Time.realtimeSinceStartup;
            if (phase == MikuGravityPhase.Impact && !capturedImpact &&
                Time.realtimeSinceStartup - impactSeenAt >= 0.45f)
            {
                capturedImpact = true;
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "MaoGravity-Impact.png"));
            }
            if (phase == MikuGravityPhase.StandingUp && !capturedStanding &&
                Time.realtimeSinceStartup - standingSeenAt >= 5f)
            {
                capturedStanding = true;
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "MaoGravity-StandingUp.png"));
            }
            if (phase == MikuGravityPhase.Supported && sawStanding) break;
            yield return new WaitForSecondsRealtime(0.05f);
        }

        float error = director != null ? director.GravitySurfaceErrorPixels : float.PositiveInfinity;
        bool recovered = director != null && director.GravityPhase == MikuGravityPhase.Supported;
        bool passed = started && sawFalling && sawImpact && sawStanding && recovered && error <= 2.5f;
        Debug.Log("[Mao Smoke] Gravity: " + (passed ? "OK" : "FAILED") +
                  " (fall=" + sawFalling +
                  ";impact=" + sawImpact +
                  ";stand=" + sawStanding +
                  ";recovered=" + recovered +
                  ";surface_error=" + error.ToString("0.0") + ")");

        bool interruptDropStarted = passed && director.DebugLiftAndDropGravity(360);
        bool recoveryInterrupted = false;
        deadline = Time.realtimeSinceStartup + 20f;
        while (interruptDropStarted && director != null && Time.realtimeSinceStartup < deadline)
        {
            if (director.GravityPhase == MikuGravityPhase.StandingUp)
            {
                recoveryInterrupted = director.DebugInterruptGravityRecoveryForDrag() &&
                                      director.GravityPhase == MikuGravityPhase.Dragging;
                break;
            }
            yield return new WaitForSecondsRealtime(0.05f);
        }
        Debug.Log("[Mao Smoke] Gravity drag interrupt: " +
                  (recoveryInterrupted ? "OK" : "FAILED") +
                  " (phase=" + (director != null ? director.GravityPhase.ToString() : "missing") + ")");

        bool shortDropStarted = recoveryInterrupted && director.DebugLiftAndDropGravity(180);
        bool sawShortFall = false;
        bool sawHardLanding = false;
        bool sawLandingFootIk = false;
        bool shortPlayedFullFallAnimation = false;
        bool capturedHardLanding = false;
        float hardLandingSeenAt = -1f;
        deadline = Time.realtimeSinceStartup + 16f;
        while (shortDropStarted && director != null && Time.realtimeSinceStartup < deadline)
        {
            MikuGravityPhase phase = director.GravityPhase;
            if (phase == MikuGravityPhase.Falling)
            {
                sawShortFall = true;
                shortPlayedFullFallAnimation |= director.GravityAnimationActive;
            }
            if (phase == MikuGravityPhase.HardLanding && director.GravityAnimationActive)
            {
                sawHardLanding = true;
                sawLandingFootIk |= director.GravityLandingFootIkApplied;
                if (hardLandingSeenAt < 0f) hardLandingSeenAt = Time.realtimeSinceStartup;
            }
            if (phase == MikuGravityPhase.HardLanding && !capturedHardLanding && hardLandingSeenAt >= 0f &&
                Time.realtimeSinceStartup - hardLandingSeenAt >= 0.35f)
            {
                capturedHardLanding = true;
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "MaoGravity-HardLanding.png"));
            }
            if (phase == MikuGravityPhase.Supported && sawHardLanding) break;
            yield return new WaitForSecondsRealtime(0.05f);
        }

        float shortError = director != null ? director.GravitySurfaceErrorPixels : float.PositiveInfinity;
        bool shortRecovered = director != null && director.GravityPhase == MikuGravityPhase.Supported;
        bool shortPassed = shortDropStarted && sawShortFall && sawHardLanding && sawLandingFootIk &&
                           !shortPlayedFullFallAnimation &&
                           shortRecovered && shortError <= 2.5f;
        Debug.Log("[Mao Smoke] Gravity short drop: " + (shortPassed ? "OK" : "FAILED") +
                  " (mode=" + (director != null ? director.GravityLandingMode.ToString() : "missing") +
                  ";hard_landing=" + sawHardLanding +
                  ";foot_ik=" + sawLandingFootIk +
                  ";full_fall_animation=" + shortPlayedFullFallAnimation +
                  ";recovered=" + shortRecovered +
                  ";surface_error=" + shortError.ToString("0.0") + ")");

        bool taskbarSitStarted = shortPassed && director.DebugReleaseGravityNearTaskbar(24);
        yield return new WaitForSecondsRealtime(0.75f);
        int firstTaskbarPose = director != null ? director.GravityTaskbarSitPoseIndex : -1;
        bool firstTaskbarAnimation = director != null && director.GravityTaskbarSitAnimationActive;
        bool secondTaskbarSitStarted = taskbarSitStarted && director.DebugReleaseGravityNearTaskbar(24);
        yield return new WaitForSecondsRealtime(0.75f);
        int secondTaskbarPose = director != null ? director.GravityTaskbarSitPoseIndex : -1;
        float taskbarError = director != null ? director.GravityTaskbarSnapErrorPixels : float.PositiveInfinity;
        bool taskbarSitPassed = taskbarSitStarted && secondTaskbarSitStarted && director != null &&
                                director.GravityTaskbarSitting && firstTaskbarAnimation &&
                                director.GravityTaskbarSitAnimationActive &&
                                firstTaskbarPose >= 0 && secondTaskbarPose >= 0 &&
                                firstTaskbarPose != secondTaskbarPose &&
                                director.GravityPhase == MikuGravityPhase.Supported &&
                                !director.GravityAnimationActive && taskbarError <= 1.5f;
        if (taskbarSitPassed)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "MaoGravity-TaskbarSit.png"));
        }
        Debug.Log("[Mao Smoke] Gravity taskbar sit: " + (taskbarSitPassed ? "OK" : "FAILED") +
                  " (sitting=" + (director != null && director.GravityTaskbarSitting) +
                  ";window_sit_animation=" + (director != null && director.GravityTaskbarSitAnimationActive) +
                  ";poses=" + firstTaskbarPose + "->" + secondTaskbarPose +
                  ";phase=" + (director != null ? director.GravityPhase.ToString() : "missing") +
                  ";gravity_animation=" + (director != null && director.GravityAnimationActive) +
                  ";snap_error=" + taskbarError.ToString("0.0") + ")");
        director?.DebugSetGravityPhysicalDragSuppressed(false);
    }

    private static IEnumerator RunIdleActionSmokeTest()
    {
        MaoReactionDirector director = geminiProvider.GetComponent<MaoReactionDirector>();
        float deadline = Time.realtimeSinceStartup + 15f;
        int stableAvatarId = 0;
        float stableSince = Time.realtimeSinceStartup;
        while (director != null && Time.realtimeSinceStartup < deadline)
        {
            int currentAvatarId = director.BoundAvatarInstanceId;
            if (currentAvatarId != stableAvatarId)
            {
                stableAvatarId = currentAvatarId;
                stableSince = Time.realtimeSinceStartup;
            }
            if (director.IdleActionsReady && stableAvatarId != 0 &&
                Time.realtimeSinceStartup - stableSince >= 2f)
                break;
            yield return new WaitForSecondsRealtime(0.25f);
        }

        Array actions = Enum.GetValues(typeof(MaoIdleActionKind));
        for (int index = 0; index < actions.Length; index++)
        {
            MaoIdleActionKind action = (MaoIdleActionKind)actions.GetValue(index);
            bool started = director != null && director.PlayIdleAction(action);
            yield return new WaitForSecondsRealtime(0.45f);
            bool active = started && director.IdleActionPlaying && director.IdleActionAnimationActive;
            float drift = director != null ? director.IdleActionRootDrift : -1f;
            Debug.Log("[Mao Smoke] Idle Action " + action + ": " +
                      (active && drift < 0.002f ? "OK" : "FAILED") +
                      " (active=" + active + ";drift=" + drift.ToString("0.0000") + ")");
            string actionScreenshot = Path.Combine(
                Application.persistentDataPath,
                "MaoIdleAction-" + action + ".png");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(actionScreenshot);
            Debug.Log("[Mao Smoke] Idle Action Screenshot: " + actionScreenshot);
        }
    }

    private static IEnumerator RunGuitarSmokeTest()
    {
        MaoReactionDirector director = geminiProvider.GetComponent<MaoReactionDirector>();
        float deadline = Time.realtimeSinceStartup + 15f;
        int stableAvatarId = 0;
        float stableSince = Time.realtimeSinceStartup;
        while (director != null && Time.realtimeSinceStartup < deadline)
        {
            int currentAvatarId = director.BoundAvatarInstanceId;
            if (currentAvatarId != stableAvatarId)
            {
                stableAvatarId = currentAvatarId;
                stableSince = Time.realtimeSinceStartup;
            }
            if (director.GuitarReady && stableAvatarId != 0 &&
                Time.realtimeSinceStartup - stableSince >= 2f)
                break;
            yield return new WaitForSecondsRealtime(0.25f);
        }

        bool setup = director != null && director.GuitarReady;
        bool firstPlay = setup && director.PlayGuitar();
        bool secondPlay = firstPlay && director.PlayGuitar();
        yield return new WaitForSecondsRealtime(0.85f);

        bool animation = setup && director.GuitarPlaying && director.GuitarAnimationActive;
        bool visible = setup && director.GuitarVisible;
        bool locomotionBlocked = setup && !director.CursorFollowEnabled;
        bool rootStable = setup && director.GuitarRootDriftDistance < 0.002f;
        bool guitarAngle = setup && director.GuitarNeckRise > 0.25f;
        float observedGripError = director != null ? director.GuitarHandGripError : float.PositiveInfinity;
        bool handsAligned = setup && observedGripError < 0.12f;
        float observedRootDrift = director != null ? director.GuitarRootDriftDistance : -1f;
        Debug.Log("[Mao Smoke] Guitar Runtime: " +
                  (director != null ? director.GuitarRuntimeDebugSnapshot : "director=null"));

        string screenshotPath = Path.Combine(Application.persistentDataPath, "MaoGuitarSmoke.png");
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(screenshotPath);
        yield return new WaitForSecondsRealtime(0.5f);

        bool firstStop = setup && director.StopGuitar();
        bool secondStop = setup && director.StopGuitar();
        yield return new WaitForSecondsRealtime(0.55f);
        bool stopped = setup && !director.GuitarPlaying && !director.GuitarVisible;

        Debug.Log("[Mao Smoke] Guitar Setup: " + (setup ? "OK" : "FAILED"));
        Debug.Log("[Mao Smoke] Guitar Play Idempotent: " + (firstPlay && secondPlay ? "OK" : "FAILED"));
        Debug.Log("[Mao Smoke] Guitar Animation: " + (animation ? "OK" : "FAILED"));
        Debug.Log("[Mao Smoke] Guitar Prop Visible: " + (visible ? "OK" : "FAILED"));
        Debug.Log("[Mao Smoke] Guitar Locomotion Block: " + (locomotionBlocked ? "OK" : "FAILED"));
        Debug.Log("[Mao Smoke] Guitar Root Drift: " + (rootStable ? "OK" : "FAILED") +
                  " (distance=" + (observedRootDrift >= 0f ? observedRootDrift.ToString("0.0000") : "n/a") + ")");
        Debug.Log("[Mao Smoke] Guitar Neck Angle: " + (guitarAngle ? "OK" : "FAILED") +
                  " (rise=" + (director != null ? director.GuitarNeckRise.ToString("0.000") : "n/a") + ")");
        Debug.Log("[Mao Smoke] Guitar Hand Alignment: " + (handsAligned ? "OK" : "FAILED") +
                  " (error=" + (!float.IsPositiveInfinity(observedGripError) ? observedGripError.ToString("0.000") : "n/a") + ")");
        Debug.Log("[Mao Smoke] Guitar Screenshot: " + screenshotPath);
        Debug.Log("[Mao Smoke] Guitar Stop Idempotent: " + (firstStop && secondStop && stopped ? "OK" : "FAILED"));
    }

    private static IEnumerator RunMovementSmokeTest()
    {
        MaoReactionDirector director = geminiProvider.GetComponent<MaoReactionDirector>();
        float deadline = Time.realtimeSinceStartup + 12f;
        bool enabled = false;
        while (!enabled && Time.realtimeSinceStartup < deadline)
        {
            enabled = director != null && director.SetCursorFollowEnabled(true);
            if (enabled)
            {
                yield return new WaitForSecondsRealtime(2f);
                enabled = director.CursorFollowEnabled;
            }
            if (!enabled) yield return new WaitForSecondsRealtime(0.25f);
        }
        Debug.Log("[Mao Smoke] Cursor Follow: " + (enabled ? "OK" : "FAILED"));
    }

    private static IEnumerator RunMotionSmokeTest()
    {
        MaoReactionDirector director = geminiProvider.GetComponent<MaoReactionDirector>();
        float deadline = Time.realtimeSinceStartup + 18f;
        bool adaptiveReady = false;
        int stableAvatarId = 0;
        float stableSince = Time.realtimeSinceStartup;
        while (!adaptiveReady && Time.realtimeSinceStartup < deadline)
        {
            bool enabled = director != null && director.SetCursorFollowEnabled(true, true);
            int currentAvatarId = director != null ? director.BoundAvatarInstanceId : 0;
            if (currentAvatarId != stableAvatarId)
            {
                stableAvatarId = currentAvatarId;
                stableSince = Time.realtimeSinceStartup;
            }
            adaptiveReady = enabled && stableAvatarId != 0 && director.CursorFollowEnabled &&
                            director.AdaptiveMotionReady && director.CursorFlightReady &&
                            Time.realtimeSinceStartup - stableSince >= 2f;
            if (!adaptiveReady) yield return new WaitForSecondsRealtime(0.25f);
        }

        bool right = adaptiveReady && director.BeginAdaptiveMotionSmokeProbe(1);
        yield return new WaitForSecondsRealtime(0.25f);
        right &= director.CurrentCursorGait == MaoLocomotionGait.Run &&
                 director.CursorFacingAgreesWithDirection(1) &&
                 (!director.AdvancedGroundMotionReady ||
                  (director.CurrentGroundState == MaoGroundLocomotionState.Running &&
                   director.AdvancedGroundAnimationActive));

        bool left = adaptiveReady && director.BeginAdaptiveMotionSmokeProbe(-1);
        yield return new WaitForSecondsRealtime(0.25f);
        left &= director.CurrentCursorGait == MaoLocomotionGait.Run &&
                director.CursorFacingAgreesWithDirection(-1) &&
                (!director.AdvancedGroundMotionReady ||
                 (director.CurrentGroundState == MaoGroundLocomotionState.Running &&
                  director.AdvancedGroundAnimationActive));

        bool rightAgain = adaptiveReady && director.BeginAdaptiveMotionSmokeProbe(1);
        yield return new WaitForSecondsRealtime(0.25f);
        rightAgain &= director.CurrentCursorGait == MaoLocomotionGait.Run &&
                      director.CursorFacingAgreesWithDirection(1) &&
                      (!director.AdvancedGroundMotionReady || director.AdvancedGroundAnimationActive);

        MaoLocomotionGait observedGait = director.CurrentCursorGait;
        float observedYaw = director.CursorFacingYawDegrees;

        bool walkRight = adaptiveReady && director.BeginAdaptiveWalkSmokeProbe(1);
        yield return new WaitForSecondsRealtime(0.25f);
        walkRight &= director.CurrentCursorGait == MaoLocomotionGait.Walk &&
                     director.CursorFacingAgreesWithDirection(1) &&
                     (!director.AdvancedGroundMotionReady ||
                      (director.CurrentGroundState == MaoGroundLocomotionState.Walking &&
                       director.AdvancedGroundAnimationActive));

        bool walkLeft = adaptiveReady && director.BeginAdaptiveWalkSmokeProbe(-1);
        yield return new WaitForSecondsRealtime(0.25f);
        walkLeft &= director.CurrentCursorGait == MaoLocomotionGait.Walk &&
                    director.CursorFacingAgreesWithDirection(-1) &&
                    (!director.AdvancedGroundMotionReady ||
                     (director.CurrentGroundState == MaoGroundLocomotionState.Walking &&
                      director.AdvancedGroundAnimationActive));

        bool movedSideways = right && left && rightAgain;
        bool walkedSideways = walkRight && walkLeft;
        Debug.Log("[Mao Smoke] Adaptive Walk/Run Setup: " + (adaptiveReady ? "OK" : "FAILED"));
        Debug.Log("[Mao Smoke] Advanced Ground Animator: " +
                  (director.AdvancedGroundMotionReady ? "OK" : "FAILED") +
                  " (state=" + director.CurrentGroundState + ")");
        Debug.Log("[Mao Smoke] Side Facing: " + (movedSideways && walkedSideways ? "OK" : "FAILED") +
                  " (gait=" + observedGait +
                  ", yaw=" + observedYaw.ToString("0") + ")");
        Debug.Log("[Mao Smoke] Direction Sequence right-left-right: " +
                  (movedSideways ? "OK" : "FAILED") +
                  " (right=" + right + ", left=" + left + ", rightAgain=" + rightAgain + ")");
        Debug.Log("[Mao Smoke] Walk Direction Sequence right-left: " +
                  (walkedSideways ? "OK" : "FAILED") +
                  " (right=" + walkRight + ", left=" + walkLeft + ")");

        bool flying = director.CursorFlightReady &&
                      director.BeginCursorFlightSmokeProbe(MaoCursorFlightPhase.Flying);
        yield return new WaitForSecondsRealtime(0.2f);
        flying &= director.CursorFlightPhase == MaoCursorFlightPhase.Flying &&
                  director.CursorFlightAnimationActive &&
                  director.MacaronFlightVisualRequested;
        bool returning = director.BeginCursorFlightSmokeProbe(MaoCursorFlightPhase.Returning);
        yield return new WaitForSecondsRealtime(0.2f);
        returning &= director.CursorFlightPhase == MaoCursorFlightPhase.Returning &&
                     director.CursorFlightAnimationActive &&
                     director.MacaronFlightVisualRequested;
        director.EndCursorFlightSmokeProbe();
        Debug.Log("[Mao Smoke] Macaron Cursor Flight: " +
                  (flying && returning ? "OK" : "FAILED") +
                  " (flying=" + flying + ", returning=" + returning + ")");
    }

    private static void ConfigureMiku()
    {
        string source = ResolveMikuSource();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            Debug.LogWarning("[Mao Bootstrap] hatsune-miku.vrm was not found; use Mate's avatar picker to select it.");
            return;
        }

        try
        {
            string avatarDirectory = Path.Combine(Application.persistentDataPath, "VRM");
            Directory.CreateDirectory(avatarDirectory);
            string target = Path.Combine(avatarDirectory, MaoModelLocator.FileName);

            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);
            if (!targetInfo.Exists || targetInfo.Length != sourceInfo.Length || sourceInfo.LastWriteTimeUtc > targetInfo.LastWriteTimeUtc)
                File.Copy(source, target, true);

            if (SaveLoadHandler.Instance != null)
            {
                SaveLoadHandler.Instance.data.selectedModelPath = target;
                SaveLoadHandler.Instance.data.enableRandomAvatar = false;
                SaveLoadHandler.Instance.SaveToDisk();
            }
            else
            {
                PlayerPrefs.SetString("SavedPathModel", target);
                PlayerPrefs.Save();
            }

            Debug.Log("[Mao Bootstrap] Miku avatar configured through Mate VRMLoader.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[Mao Bootstrap] Could not configure Miku: " + exception.Message);
        }
    }

    private static string ResolveMikuSource()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--mao-model", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[index + 1].Trim('"'));
        }

        string configured = Environment.GetEnvironmentVariable("MAO_MIKU_VRM");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        string bundled = MaoModelLocator.GetBundledModelPath(AppContext.BaseDirectory);
        if (File.Exists(bundled)) return bundled;

#if UNITY_EDITOR
        string workspaceModel = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "AvatarAssets", MaoModelLocator.FileName));
        if (File.Exists(workspaceModel)) return workspaceModel;
#endif
        return bundled;
    }
}
