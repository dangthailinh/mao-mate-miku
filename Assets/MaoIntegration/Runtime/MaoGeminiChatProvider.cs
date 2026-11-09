using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MaoIntegration;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using MaoTextBackend = MaoIntegration.MaoTextProviderBackend;

public enum MaoConversationPhysicalState
{
    Listening,
    Thinking,
    Speaking,
    Waiting
}

public sealed class MaoGeminiChatProvider : MonoBehaviour
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent";
    private const int MaxStoredTurns = 40;
    private const int MaxMessageCharacters = 12000;
    private const int MemoryUpdateTurnThreshold = 6;
    private const long MaxHistoryFileBytes = 2 * 1024 * 1024;
    private const long MaxMemoryFileBytes = 64 * 1024;
    private const long MaxProactiveStateFileBytes = 64 * 1024;
    private const int QwenRequestAttempts = 2;
    private const float QwenRetryDelaySeconds = 0.75f;
    private const float QwenTransientCooldownSeconds = 300f;
    private const string ProactiveEnabledPreference = "MaoConversation.ProactiveEnabled";
    private const string LastUserMessagePreference = "MaoConversation.LastUserMessageUtc";
    private const string LastProactiveMessagePreference = "MaoConversation.LastProactiveMessageUtc";
    private const float ProactiveEvaluationIntervalSeconds = 2.5f;
    private readonly List<MaoChatTurn> history = new List<MaoChatTurn>();
    private IReadOnlyList<string> geminiKeys = Array.Empty<string>();
    private string qwenKey = string.Empty;
    private string hhtechKey = string.Empty;
    private UnityWebRequest activeRequest;
    private UnityWebRequest memoryRequest;
    private UnityWebRequest proactiveRequest;
    private Coroutine activeChat;
    private Coroutine memoryCoroutine;
    private Coroutine proactiveRoutine;
    private MaoScreenVisionService screenVision;
    private MaoAnimeVoiceService animeVoice;
    private MaoAdvancedMemory advancedMemory = new MaoAdvancedMemory();
    private MaoProactiveConversationState proactiveState = new MaoProactiveConversationState();
    private MaoEmotionalState emotionalState = MaoEmotionalState.CreateDefault(DateTime.UtcNow);
    private MaoComputerAgentRuntime computerAgent;
    private MaoGmailRuntime gmail;
    private MaoDailyBriefRuntime dailyBrief;
    private MaoHarnessRuntime harness;
    private readonly Dictionary<string, MaoSpeechPlan> recentSpeechPlans = new Dictionary<string, MaoSpeechPlan>();
    private readonly Queue<string> speechPlanOrder = new Queue<string>();
    private bool memoryUpdatePending;
    private bool emotionalStateDirty;
    private int newTurnsSinceMemoryUpdate;
    private float nextMemoryAttemptAt;
    private float nextEmotionMaintenanceAt;
    private int keyOffset;
    private DateTime lastUserMessageUtc;
    private DateTime? lastProactiveMessageUtc;
    private bool hasRecordedUserMessage;
    private bool userComposing;
    private float nextProactiveEvaluationAt;
    private float qwenUnavailableUntil = float.NegativeInfinity;
    private string lastLoggedProactiveSuppression = string.Empty;
    private string activeProactiveEvaluationId = string.Empty;

    public IReadOnlyList<MaoChatTurn> History => history;
    public bool QwenConfigured => !string.IsNullOrWhiteSpace(qwenKey);
    public bool HhtechConfigured => !string.IsNullOrWhiteSpace(hhtechKey);
    public bool GeminiConfigured => geminiKeys.Count > 0;
    public bool IsConfigured => HhtechConfigured || QwenConfigured || GeminiConfigured;
    public string PreferredTextBackend => HhtechConfigured ? "hhtech" : QwenConfigured ? "qwen" : GeminiConfigured ? "gemini" : "none";
    public string LastTextBackendUsed { get; private set; } = string.Empty;
    public bool QwenTemporarilyUnavailable =>
        QwenConfigured && Time.realtimeSinceStartup < qwenUnavailableUntil;
    public MaoGmailRuntime Gmail => gmail;
    public MaoHarnessRuntime Harness => harness;
    public bool IsBusy => activeChat != null || memoryCoroutine != null || proactiveRoutine != null;
    public bool ScreenVisionEnabled => screenVision != null && screenVision.Enabled;
    public bool ScreenVisionObservationReady => screenVision != null && screenVision.AutomaticObservationReady;
    public bool LastScreenVisionHadMeaningfulChange =>
        screenVision != null && screenVision.LastRequestWasMeaningfulChange;
    public int ScreenVisionRequestCount => screenVision != null ? screenVision.VisionRequestCount : 0;
    public MaoScreenWorldState ScreenWorldState => screenVision != null ? screenVision.CurrentWorldState : null;
    public bool ProactiveConversationEnabled
    {
        get => PlayerPrefs.GetInt(ProactiveEnabledPreference, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(ProactiveEnabledPreference, value ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log("[Mao Proactive] event=setting_changed enabled=" + value.ToString().ToLowerInvariant());
        }
    }
    public double SecondsSinceUserMessage => Math.Max(0d, (DateTime.UtcNow - lastUserMessageUtc).TotalSeconds);
    public double SecondsSinceProactiveMessage => lastProactiveMessageUtc.HasValue
        ? Math.Max(0d, (DateTime.UtcNow - lastProactiveMessageUtc.Value).TotalSeconds)
        : double.PositiveInfinity;
    public float ConversationEngagementScore => MaoConversationEngagementPolicy.GetEffectiveScore(
        DateTime.UtcNow,
        proactiveState);
    public string LastPublishedProactiveMessage { get; private set; } = string.Empty;
    public string LastProactiveCorrelationId { get; private set; } = string.Empty;
    public int PublishedProactiveMessageCount { get; private set; }
    public string LastProactiveSuppressionReason { get; private set; } = string.Empty;
    public event Action<string> ProactiveMessage;
    public event Action<MaoConversationPhysicalState> ConversationStateChanged;
    public event Action<MaoSemanticReaction> SemanticReactionRequested;

    private string HistoryPath => Path.Combine(Application.persistentDataPath, "mao-gemini-history.json");
    private string LegacyMemoryPath => Path.Combine(Application.persistentDataPath, "mao-long-term-memory.json");
    private string MemoryPath => Path.Combine(Application.persistentDataPath, "mao-advanced-memory.json");
    private string EmotionalStatePath => Path.Combine(Application.persistentDataPath, "mao-emotional-state.json");
    private string ProactiveStatePath => Path.Combine(Application.persistentDataPath, "mao-proactive-state.json");

    private void Awake()
    {
        if (MaoAppearanceVerification.Active)
        {
            enabled = false;
            Debug.Log("[Mao Cat Smoke] AI services isolated; no credentials or conversation state loaded.");
            return;
        }
        screenVision = GetComponent<MaoScreenVisionService>();
        if (screenVision == null) screenVision = gameObject.AddComponent<MaoScreenVisionService>();
        animeVoice = GetComponent<MaoAnimeVoiceService>();
        if (animeVoice == null) animeVoice = gameObject.AddComponent<MaoAnimeVoiceService>();
        computerAgent = GetComponent<MaoComputerAgentRuntime>();
        if (computerAgent == null) computerAgent = gameObject.AddComponent<MaoComputerAgentRuntime>();
        harness = GetComponent<MaoHarnessRuntime>();
        if (harness == null) harness = gameObject.AddComponent<MaoHarnessRuntime>();
        gmail = new MaoGmailRuntime();
        dailyBrief = GetComponent<MaoDailyBriefRuntime>();
        if (dailyBrief == null) dailyBrief = gameObject.AddComponent<MaoDailyBriefRuntime>();
        LoadKeys();
        LoadHistory();
        LoadMemory();
        LoadEmotionalState();
        LoadConversationTiming();
        LoadProactiveState();
        nextProactiveEvaluationAt = Time.realtimeSinceStartup + ProactiveEvaluationIntervalSeconds;
        nextEmotionMaintenanceAt = Time.realtimeSinceStartup + 30f;
        Debug.Log("[Mao Proactive] event=scheduler_started enabled=" +
                  ProactiveConversationEnabled.ToString().ToLowerInvariant() +
                   " revision=" + MaoAssistantProfile.IntegrationRevision +
                   " first_delay_seconds=" + MaoConversationEngagementPolicy.FirstIdleDelaySeconds(DateTime.UtcNow, proactiveState) +
                   " quiet_cooldown_seconds=" + MaoConversationEngagementPolicy.QuietEvaluationCooldownSeconds(DateTime.UtcNow, proactiveState) +
                   " reengagement_cooldown_seconds=" + MaoConversationEngagementPolicy.QuietReengagementCooldownSeconds(DateTime.UtcNow, proactiveState) +
                   " idle_seconds=" + SecondsSinceUserMessage.ToString("0") +
                  " unanswered=" + proactiveState.ConsecutiveUnansweredMessages +
                  " engagement=" + MaoConversationEngagementPolicy.GetEffectiveScore(DateTime.UtcNow, proactiveState).ToString("0.00"));
        Debug.Log("[Mao Chat] event=backends_loaded hhtech=" + HhtechConfigured.ToString().ToLowerInvariant() +
                  " qwen=" + QwenConfigured.ToString().ToLowerInvariant() +
                  " gemini=" + GeminiConfigured.ToString().ToLowerInvariant() +
                  " preferred=" + PreferredTextBackend +
                  " qwen_model=" + MaoQwenProtocol.Model + " hhtech_model=" + MaoHhtechProtocol.Model);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            screenVision.Enabled = !screenVision.Enabled;
            Debug.Log("[Mao Vision] " + (screenVision.Enabled ? "ON" : "OFF") + " (F8)");
        }
        if (Input.GetKeyDown(KeyCode.F9))
        {
            animeVoice.Enabled = !animeVoice.Enabled;
            Debug.Log("[Mao Voice] " + (animeVoice.Enabled ? "ON" : "OFF") + " (F9)");
        }

        if (memoryUpdatePending && memoryCoroutine == null && activeChat == null &&
            IsConfigured && Time.realtimeSinceStartup >= nextMemoryAttemptAt)
            memoryCoroutine = StartCoroutine(UpdateMemoryRoutine());

        if (Time.realtimeSinceStartup >= nextEmotionMaintenanceAt)
        {
            nextEmotionMaintenanceAt = Time.realtimeSinceStartup + 30f;
            MaoEmotionEngine.Decay(emotionalState, DateTime.UtcNow);
            MaoEmotionEngine.AdvanceSilence(emotionalState, lastUserMessageUtc, DateTime.UtcNow);
            emotionalStateDirty = true;
            if (emotionalStateDirty) SaveEmotionalState();
        }

        if (Time.realtimeSinceStartup >= nextProactiveEvaluationAt)
        {
            nextProactiveEvaluationAt = Time.realtimeSinceStartup + ProactiveEvaluationIntervalSeconds;
            TryPublishProactiveCheckIn(DateTime.UtcNow);
        }
    }

    public void Send(string message, Action<string> onPartial, Action onComplete)
    {
        SendInternal(message, onPartial, onComplete, true, true, true);
    }

    public void SendSilent(string message, Action<string> onPartial, Action onComplete)
    {
        SendInternal(message, onPartial, onComplete, false, false, false);
    }

    public void NotifyUserComposing(bool isComposing)
    {
        userComposing = isComposing;
        if (activeChat != null || proactiveRoutine != null) return;
        ConversationStateChanged?.Invoke(isComposing
            ? MaoConversationPhysicalState.Listening
            : MaoConversationPhysicalState.Waiting);
    }

    private void SendInternal(
        string message,
        Action<string> onPartial,
        Action onComplete,
        bool includeScreen,
        bool speakReply,
        bool userConversation)
    {
        if (MaoAppearanceVerification.Active) { onComplete?.Invoke(); return; }
        if (harness != null && harness.TryHandleControl(message, out string harnessControlReply))
        {
            if (!MaoControlCommandPolicy.IsReadOnly(message)) Cancel();
            if (userConversation) RecordUserMessage(DateTime.UtcNow);
            RecordLocalCommandTurn(message, harnessControlReply, "harness_control");
            onPartial?.Invoke(harnessControlReply);
            onComplete?.Invoke();
            return;
        }
        Cancel();
        userComposing = false;
        activeChat = StartCoroutine(SendRoutine(message, onPartial, onComplete, includeScreen, speakReply, userConversation));
    }

    public void Cancel()
    {
        if (harness != null) harness.Cancel();
        userComposing = false;
        if (activeRequest != null)
        {
            activeRequest.Abort();
            activeRequest.Dispose();
            activeRequest = null;
        }

        if (activeChat != null)
        {
            StopCoroutine(activeChat);
            activeChat = null;
        }
        if (animeVoice != null) animeVoice.Cancel();
        CancelMemoryUpdate();
        CancelProactiveObservation();
        ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
    }

    private IEnumerator SendRoutine(
        string message,
        Action<string> onPartial,
        Action onComplete,
        bool includeScreen,
        bool speakReply,
        bool userConversation)
    {
        bool completionCalled = false;
        Action<string> guardedPartial = value => onPartial?.Invoke(value);
        Action guardedComplete = () =>
        {
            if (completionCalled) return;
            completionCalled = true;
            onComplete?.Invoke();
        };

        IEnumerator routine;
        try
        {
            routine = SendRoutineCore(
                message,
                guardedPartial,
                guardedComplete,
                includeScreen,
                speakReply,
                userConversation);
        }
        catch (Exception exception)
        {
            HandleRoutineFailure(exception, guardedPartial, guardedComplete, userConversation);
            yield break;
        }

        while (true)
        {
            object yielded;
            try
            {
                if (!routine.MoveNext()) yield break;
                yielded = routine.Current;
            }
            catch (Exception exception)
            {
                HandleRoutineFailure(exception, guardedPartial, guardedComplete, userConversation);
                yield break;
            }

            yield return yielded;
        }
    }

    private IEnumerator SendRoutineCore(
        string message,
        Action<string> onPartial,
        Action onComplete,
        bool includeScreen,
        bool speakReply,
        bool userConversation)
    {
        string userText = (message ?? string.Empty).Trim();
        if (userText.Length == 0)
        {
            onComplete?.Invoke();
            yield break;
        }
        if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Thinking);
        if (userText.Length > MaxMessageCharacters)
            userText = userText.Substring(0, MaxMessageCharacters);

        DateTime nowUtc = DateTime.UtcNow;
        DateTime? previousUserMessageUtc = hasRecordedUserMessage ? lastUserMessageUtc : (DateTime?)null;
        string conversationTimeContext = MaoConversationTimePolicy.BuildPromptContext(
            nowUtc,
            DateTime.Now,
            previousUserMessageUtc);
        string returnContext = userConversation
            ? MaoProactiveConversationPolicy.BuildReturnContext(proactiveState.ConsecutiveUnansweredMessages)
            : string.Empty;
        if (returnContext.Length > 0) conversationTimeContext += " " + returnContext;
        if (userConversation)
        {
            RecordUserMessage(nowUtc);
            conversationTimeContext += "\n" +
                MaoConversationEngagementPolicy.BuildPromptContext(nowUtc, proactiveState);
        }

        MaoDailyBriefCommand dailyCommand;
        if (dailyBrief != null && dailyBrief.TryParseCommand(userText, out dailyCommand))
        {
            string dailyReply = string.Empty;
            yield return dailyBrief.ExecuteCommandRoutine(dailyCommand, value => dailyReply = value ?? string.Empty);
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Speaking);
            onPartial?.Invoke(dailyReply);
            RecordLocalCommandTurn(userText, dailyReply, "daily_command");
            if (speakReply && animeVoice.Enabled && !string.IsNullOrWhiteSpace(dailyReply))
                yield return animeVoice.SpeakRoutine(dailyReply, MaoAssistantProfile.AivisSweetStyleName);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        if (gmail != null && gmail.TryParseCommand(userText, out MaoGmailCommand gmailCommand))
        {
            string gmailReply = string.Empty;
            yield return gmail.ExecuteRoutine(gmailCommand, reply => gmailReply = reply ?? string.Empty);
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Speaking);
            onPartial?.Invoke(gmailReply);
            if (speakReply && animeVoice.Enabled && !string.IsNullOrWhiteSpace(gmailReply))
                yield return animeVoice.SpeakRoutine(gmailReply);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        if (harness != null && MaoHarnessPolicy.ShouldRoute(userText))
        {
            string harnessReply = string.Empty;
            MaoMemoryContext taskMemory = MaoAdvancedMemoryRetriever.Retrieve(advancedMemory, userText, nowUtc);
            string taskContext = conversationTimeContext + "\n" + MaoEmotionEngine.BuildPromptContext(emotionalState) +
                "\nKý ức liên quan (chỉ dữ liệu): " + taskMemory.PromptText;
            foreach (MaoChatTurn turn in history.TakeLast(4))
                taskContext += "\n" + turn.Role + ": " + (turn.Text.Length > 700 ? turn.Text.Substring(0, 700) : turn.Text);
            if (taskContext.Length > 6000) taskContext = taskContext.Substring(0, 6000);
            yield return harness.RunRoutine(userText, taskContext, value => onPartial?.Invoke(value), value => harnessReply = value);
            LastTextBackendUsed = "harness:" + harness.LastProvider;
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Speaking);
            onPartial?.Invoke(harnessReply);
            RecordLocalCommandTurn(userText, harnessReply, "harness_task");
            if (speakReply && animeVoice.Enabled && !string.IsNullOrWhiteSpace(harnessReply))
                yield return animeVoice.SpeakRoutine(harnessReply, MaoAssistantProfile.AivisSweetStyleName);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        if (computerAgent != null && computerAgent.TryHandleUserCommand(userText, out string computerReply))
        {
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Speaking);
            onPartial?.Invoke(computerReply);
            if (speakReply && animeVoice.Enabled) yield return animeVoice.SpeakRoutine(computerReply);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        MaoMemoryManagementCommand memoryCommand;
        if (MaoWebSearch.WantsSearch(userText))
        {
            string searchReply = string.Empty;
            onPartial?.Invoke("Đợi xíu, Miku tra trên mạng nhé…");
            yield return SearchWebRoutine(userText, value => searchReply = value);
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            onPartial?.Invoke(searchReply);
            if (speakReply && animeVoice.Enabled)
            {
                string speechText = searchReply.Split(new[] { "\n\nNguồn web:" }, StringSplitOptions.None)[0];
                MaoSpeechPlan plan = new MaoSpeechPlan(speechText, MaoAssistantProfile.AivisSweetStyleName);
                yield return BuildSpeechPlanRoutine(speechText, value => plan = value);
                yield return animeVoice.SpeakRoutine(plan.SpeechText, plan.StyleName);
            }
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }
        if (MaoMemoryManagementCommandParser.TryParse(userText, out memoryCommand))
        {
            string memoryReply = ApplyMemoryManagementCommand(memoryCommand);
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            onPartial?.Invoke(memoryReply);
            if (speakReply && animeVoice.Enabled) yield return animeVoice.SpeakRoutine(memoryReply);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        MaoMovementCommand movementCommand;
        if (MaoMovementCommandParser.TryParse(userText, out movementCommand))
        {
            string movementReply = ApplyMovementCommand(movementCommand);
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Speaking);
            bool playfulMovement = movementCommand.Action == MaoMovementCommandAction.FollowCursor ||
                                   movementCommand.Action == MaoMovementCommandAction.RunFollowCursor;
            bool guitarCommand = movementCommand.Action == MaoMovementCommandAction.PlayGuitar ||
                                 movementCommand.Action == MaoMovementCommandAction.StopGuitar;
            bool idleActionCommand = TryMapIdleAction(movementCommand.Action, out _);
            bool gravityCommand = IsGravityCommand(movementCommand.Action);
            PublishSemanticReaction(new MaoSemanticReaction(
                string.Empty,
                playfulMovement || movementCommand.Action == MaoMovementCommandAction.PlayGuitar ? "happy" : "neutral",
                guitarCommand || idleActionCommand || gravityCommand ? "none" : playfulMovement ? "wave" : "nod",
                movementCommand.Action == MaoMovementCommandAction.FollowCursor ||
                movementCommand.Action == MaoMovementCommandAction.RunFollowCursor ? "cursor" : "user"));
            onPartial?.Invoke(movementReply);
            if (speakReply && animeVoice.Enabled) yield return animeVoice.SpeakRoutine(movementReply);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        MaoFeatureCommand featureCommand;
        if (MaoFeatureCommands.TryParse(userText, out featureCommand))
        {
            string featureReply = ApplyFeatureCommand(featureCommand);
            if (userConversation) RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
            onPartial?.Invoke(featureReply);
            if (speakReply && animeVoice.Enabled) yield return animeVoice.SpeakRoutine(featureReply);
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        if (!IsConfigured)
        {
            onPartial?.Invoke("Miku chưa kết nối được AI. Thêm HHTECH_API_KEY trong hhtech.env, TOKENROUTER_API_KEY trong qwen.env hoặc GEMINI_API_KEY rồi mở lại app nha.");
            onComplete?.Invoke();
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
            activeChat = null;
            yield break;
        }

        MaoMemoryContext memoryContext = MaoAdvancedMemoryRetriever.Retrieve(advancedMemory, userText, nowUtc);
        if (memoryContext.Facts.Count > 0) SaveMemory();
        string systemPrompt = MaoAssistantProfile.BuildSystemPrompt(
            memoryContext.PromptText,
            conversationTimeContext,
            MaoEmotionEngine.BuildPromptContext(emotionalState),
            computerAgent != null && computerAgent.Enabled);
        string interactionContext = "engagement=" + proactiveState.EngagementScore.ToString("0.00") +
                                     "; responsive_streak=" + proactiveState.ResponsiveInteractionStreak;
        systemPrompt += "\n\n" + MaoHumanStylePolicy.BuildPromptInstruction(userText, interactionContext);
        var requestTurns = new List<MaoChatTurn>(history) { new MaoChatTurn("user", userText) };
        var completeText = new StringBuilder();
        LastTextBackendUsed = string.Empty;
        byte[] currentScreen = null;
        if (includeScreen && screenVision.Enabled)
        {
            string captureError;
            if (!screenVision.TryCaptureJpeg(out currentScreen, out captureError))
                Debug.LogWarning("[Mao Vision] Continuing text-only: " + captureError);
        }

        for (int segment = 0; segment < 2; segment++)
        {
            MaoGeminiReply reply = null;
            MaoTextBackend backendUsed = MaoTextBackend.None;
            bool hasImage = segment == 0 && currentScreen != null && currentScreen.Length > 0;

            foreach (MaoTextBackend backend in GetTextBackendAttemptOrder(hasImage))
            {
                int attemptCount = GetKeyCount(backend);
                for (int attempt = 0; attempt < attemptCount; attempt++)
                {
                    int keyIndex = GetKeyIndex(backend, attempt);
                    string body = BuildRequestBody(
                        backend,
                        systemPrompt,
                        requestTurns,
                        hasImage ? currentScreen : null);
                    activeRequest = CreateTextRequest(backend, body, GetKey(backend, keyIndex), 90);

                    yield return activeRequest.SendWebRequest();
                    long responseCode = activeRequest.responseCode;

                    if (activeRequest.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            reply = ParseResponse(backend, activeRequest.downloadHandler.text);
                        }
                        catch (Exception exception)
                        {
                            Debug.LogWarning("[Mao Chat] event=parse_failed backend=" +
                                             BackendName(backend) +
                                             " error_type=" + exception.GetType().Name);
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[Mao Chat] event=request_failed backend=" +
                                         BackendName(backend) +
                                         " result=" + activeRequest.result +
                                         " http_status=" + activeRequest.responseCode);
                    }

                    activeRequest.Dispose();
                    activeRequest = null;
                    if (reply != null && !string.IsNullOrWhiteSpace(reply.Text))
                    {
                        if (backend == MaoTextBackend.Qwen)
                            qwenUnavailableUntil = float.NegativeInfinity;
                        AdvanceKey(backend, keyIndex);
                        backendUsed = backend;
                        break;
                    }

                    if (backend == MaoTextBackend.Qwen && MaoTextBackendPolicy.IsTransientFailure(responseCode))
                    {
                        // A 5xx from TokenRouter is an upstream outage, not a
                        // bad request. Do not spend another 90-second timeout
                        // on a duplicate request; let Gemini/local fallback
                        // answer immediately and suppress Qwen briefly.
                        qwenUnavailableUntil = Time.realtimeSinceStartup + QwenTransientCooldownSeconds;
                        Debug.LogWarning("[Mao Chat] event=backend_cooldown backend=qwen http_status=" + responseCode +
                                         " cooldown_seconds=" + QwenTransientCooldownSeconds.ToString("0"));
                    }

                    if (backend == MaoTextBackend.Qwen && responseCode == 429 &&
                        MaoTextBackendPolicy.ShouldRetryTransientFailure(responseCode, attempt, attemptCount))
                    {
                        Debug.Log("[Mao Chat] event=retry_scheduled backend=qwen http_status=" + responseCode +
                                  " next_attempt=" + (attempt + 2));
                        yield return new WaitForSecondsRealtime(QwenRetryDelaySeconds);
                        continue;
                    }

                    if (backend == MaoTextBackend.Qwen) break;
                }

                if (backendUsed != MaoTextBackend.None) break;
            }

            if (reply == null || string.IsNullOrWhiteSpace(reply.Text))
            {
                if (userConversation)
                {
                    RecordConversationEmotion(MaoConversationEmotionEvent.TaskFailed);
                }
                string unavailableReply = userConversation
                    ? MaoVietnameseFallback.GetUnavailableReply(userText)
                    : "Miku chưa nhận được phản hồi từ AI lúc này.";
                Debug.LogWarning("[Mao Chat] event=offline_fallback backend_unavailable user_visible=true");
                onPartial?.Invoke(unavailableReply);
                onComplete?.Invoke();
                if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
                activeChat = null;
                yield break;
            }

            LastTextBackendUsed = BackendName(backendUsed);
            Debug.Log("[Mao Chat] event=response backend=" + LastTextBackendUsed +
                      (backendUsed == MaoTextBackend.Hhtech ? " model=" + MaoHhtechProtocol.Model :
                       backendUsed == MaoTextBackend.Qwen ? " model=" + MaoQwenProtocol.Model : string.Empty));

            if (completeText.Length > 0) completeText.Append(' ');
            string visibleReply;
            MaoComputerAction requestedAction;
            bool hasComputerAction = MaoComputerToolParser.TryExtract(reply.Text, out visibleReply, out requestedAction);
            bool containedComputerEnvelope = !string.Equals(visibleReply, reply.Text, StringComparison.Ordinal);
            string computerActionResult = null;
            if (hasComputerAction && computerAgent != null &&
                MaoComputerActionIntentPolicy.CanExecute(userText, requestedAction))
            {
                computerActionResult = computerAgent.ExecuteModelAction(requestedAction);
            }
            else if (hasComputerAction)
            {
                visibleReply = (visibleReply + " Miku chưa mở gì vì yêu cầu thao tác chưa đủ rõ.").Trim();
            }
            else if (containedComputerEnvelope)
            {
                visibleReply = (visibleReply + " Miku đã chặn một yêu cầu điều khiển máy tính không hợp lệ.").Trim();
            }
            MaoSemanticReaction semanticReaction = MaoSemanticReactionParser.ApplyUserTone(
                userText, MaoSemanticReactionParser.Parse(visibleReply));
            string shapedReply = string.IsNullOrWhiteSpace(computerActionResult)
                ? MaoHumanStylePolicy.ShapeVisibleReply(userText, semanticReaction.DisplayText)
                : computerActionResult;
            if (!string.IsNullOrWhiteSpace(shapedReply))
                completeText.Append(shapedReply);
            PublishSemanticReaction(semanticReaction);
            if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Speaking);
            onPartial?.Invoke(completeText.ToString());

            if (MaoHumanStylePolicy.ChooseBudget(userText).Depth == MaoConversationDepth.Brief ||
                !MaoTextBackendPolicy.RequiresContinuation(reply.FinishReason)) break;

            requestTurns.Add(new MaoChatTurn("assistant", completeText.ToString()));
            requestTurns.Add(new MaoChatTurn(
                "user",
                "Continue exactly where the previous answer stopped. Return only the missing continuation, do not repeat earlier text, and finish every sentence and requested point."));
        }

        string assistantText = completeText.ToString().Trim();
        if (userConversation && assistantText.Length > 0)
        {
            // Successful replies close the turn and gently reinforce trust/positive mood.  This
            // is deliberately separate from RecordUserMessage so a provider outage does not
            // make Miku feel rewarded for a response she never delivered.
            RecordConversationEmotion(MaoConversationEmotionEvent.MeaningfulReply);
        }
        history.Add(new MaoChatTurn("user", userText));
        history.Add(new MaoChatTurn("assistant", assistantText));
        newTurnsSinceMemoryUpdate += 2;
        if (newTurnsSinceMemoryUpdate >= MemoryUpdateTurnThreshold) memoryUpdatePending = true;
        TrimAndSaveHistory();

        if (speakReply && animeVoice.Enabled)
        {
            MaoSpeechPlan speechPlan = new MaoSpeechPlan(
                assistantText,
                MaoAssistantProfile.AivisSweetStyleName);
            yield return BuildSpeechPlanRoutine(assistantText, plan => speechPlan = plan);
            yield return animeVoice.SpeakRoutine(speechPlan.SpeechText, speechPlan.StyleName);
        }
        onComplete?.Invoke();
        if (userConversation) ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
        activeChat = null;
    }

    private void HandleRoutineFailure(
        Exception exception,
        Action<string> onPartial,
        Action onComplete,
        bool userConversation)
    {
        Debug.LogError("[Mao Chat] event=internal_failure error_type=" +
                       exception.GetType().Name + " message=" + exception.Message);
        if (activeRequest != null)
        {
            activeRequest.Abort();
            activeRequest.Dispose();
            activeRequest = null;
        }
        if (userConversation)
        {
            RecordConversationEmotion(MaoConversationEmotionEvent.TaskFailed);
            ConversationStateChanged?.Invoke(MaoConversationPhysicalState.Waiting);
        }
        onPartial?.Invoke("Miku vừa gặp lỗi nội bộ nên chưa trả lời được. Thử gửi lại nha.");
        onComplete?.Invoke();
        activeChat = null;
    }

    private IEnumerator SearchWebRoutine(string question, Action<string> onReady)
    {
        if (question.Trim().Equals("/search", StringComparison.OrdinalIgnoreCase))
        {
            onReady("Gõ /search kèm điều muốn tìm nhé, ví dụ: /search tin công nghệ mới nhất hôm nay");
            yield break;
        }
        if (geminiKeys.Count == 0)
        {
            onReady("Miku cần cấu hình Gemini API để tìm trên mạng. Hiện chưa có key dùng được.");
            yield break;
        }
        string body = MaoWebSearch.BuildRequest(question, DateTime.Now);
        long lastStatus = 0;
        for (int attempt = 0; attempt < Math.Min(geminiKeys.Count, 3); attempt++)
        {
            int index = (keyOffset + attempt) % geminiKeys.Count;
            activeRequest = CreateTextRequest(MaoTextBackend.Gemini, body, geminiKeys[index], 30);
            yield return activeRequest.SendWebRequest();
            lastStatus = activeRequest.responseCode;
            string result = null;
            if (activeRequest.result == UnityWebRequest.Result.Success)
            {
                try { result = MaoWebSearch.ParseGroundedReply(activeRequest.downloadHandler.text); }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Mao Search] event=parse_failed error_type=" + exception.GetType().Name);
                }
            }
            activeRequest.Dispose();
            activeRequest = null;
            if (result != null)
            {
                keyOffset = (index + 1) % geminiKeys.Count;
                LastTextBackendUsed = "gemini-search";
                Debug.Log("[Mao Search] event=completed grounded=" + result.Contains("\n\nNguồn web:"));
                onReady(result);
                yield break;
            }
            Debug.LogWarning("[Mao Search] event=request_failed http_status=" + lastStatus);
        }
        onReady(lastStatus == 429
            ? "Gemini đang hết hạn mức tìm kiếm rồi. Miku chưa tra được tin mới; thử lại sau nhé."
            : "Miku chưa kết nối được tìm kiếm web lúc này. Chưa có kết quả mới được xác minh nhé.");
    }

    private IEnumerator BuildSpeechPlanRoutine(string assistantText, Action<MaoSpeechPlan> onReady)
    {
        if (recentSpeechPlans.TryGetValue(assistantText, out MaoSpeechPlan cachedPlan))
        {
            onReady?.Invoke(cachedPlan);
            yield break;
        }
        float planningStartedAt = Time.realtimeSinceStartup;
        MaoSpeechPlan fallback = new MaoSpeechPlan(
            assistantText,
            MaoAssistantProfile.AivisSweetStyleName);
        if (geminiKeys.Count == 0)
        {
            onReady?.Invoke(fallback);
            yield break;
        }

        for (int attempt = 0; attempt < geminiKeys.Count; attempt++)
        {
            int keyIndex = (keyOffset + attempt) % geminiKeys.Count;
            string body = MaoGeminiProtocol.BuildRequestJson(
                MaoAssistantProfile.SpeechPlannerPrompt,
                new[] { new MaoChatTurn("user", assistantText) });
            activeRequest = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 60
            };
            activeRequest.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            activeRequest.SetRequestHeader("x-goog-api-key", geminiKeys[keyIndex]);

            yield return activeRequest.SendWebRequest();

            if (activeRequest.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    MaoGeminiReply reply = MaoGeminiProtocol.ParseResponse(activeRequest.downloadHandler.text);
                    MaoSpeechPlan plan = MaoSpeechPlanParser.Parse(reply.Text, assistantText);
                    activeRequest.Dispose();
                    activeRequest = null;
                    keyOffset = (keyIndex + 1) % geminiKeys.Count;
                    if (assistantText.Length <= 2000 && !recentSpeechPlans.ContainsKey(assistantText))
                    {
                        if (speechPlanOrder.Count >= 16) recentSpeechPlans.Remove(speechPlanOrder.Dequeue());
                        recentSpeechPlans.Add(assistantText, plan);
                        speechPlanOrder.Enqueue(assistantText);
                    }
                    Debug.Log("[Mao Voice] event=translation_ready elapsed_ms=" +
                              ((Time.realtimeSinceStartup - planningStartedAt) * 1000f).ToString("0"));
                    onReady?.Invoke(plan);
                    yield break;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Mao Voice] Japanese speech planning failed: " + exception.Message);
                }
            }

            activeRequest.Dispose();
            activeRequest = null;
        }

        onReady?.Invoke(fallback);
    }

    private string ApplyFeatureCommand(MaoFeatureCommand command)
    {
        if (command.Feature == MaoFeature.LongTermMemory)
        {
            if (command.Action == MaoFeatureAction.Clear)
            {
                ClearMemory();
                return "Đã xoá ký ức dài hạn rồi. Mình bắt đầu lại nha.";
            }
            int factCount = advancedMemory?.Facts?.Count ?? 0;
            int pinnedCount = advancedMemory?.Facts?.Count(fact => fact != null && fact.IsPinned) ?? 0;
            return factCount == 0 && (advancedMemory?.Summaries?.Count ?? 0) == 0
                ? "Bộ nhớ nâng cao đang bật, nhưng Miku chưa lưu chi tiết lâu dài nào."
                : "Bộ nhớ nâng cao đang bật: " + factCount + " ký ức (" + pinnedCount +
                  " ghim), có xếp hạng truy hồi và tóm tắt cuộc trò chuyện.";
        }

        if (command.Feature == MaoFeature.ScreenVision)
        {
            if (command.Action == MaoFeatureAction.Enable) screenVision.Enabled = true;
            if (command.Action == MaoFeatureAction.Disable) screenVision.Enabled = false;
            return "Nhìn màn hình đang " + (screenVision.Enabled ? "bật" : "tắt") + ". " +
                   (screenVision.Enabled
                       ? "Miku chỉ xem khi đang chat hoặc lúc thật sự cần; ảnh không được lưu."
                       : "Miku sẽ không chụp hay gửi màn hình.");
        }

        if (command.Action == MaoFeatureAction.Enable) animeVoice.Enabled = true;
        if (command.Action == MaoFeatureAction.Disable) animeVoice.Enabled = false;
        return "Giọng anime đang " + (animeVoice.Enabled
            ? "bật — AivisSpeech Mao sẵn sàng, Nanami là giọng dự phòng."
            : "tắt.");
    }

    private string ApplyMemoryManagementCommand(MaoMemoryManagementCommand command)
    {
        if (command.Kind == MaoMemoryManagementCommandKind.Pin)
        {
            if (!MaoAdvancedMemoryPinning.TryPin(
                    advancedMemory, command.Value, DateTime.UtcNow, out MaoMemoryFact pinned))
                return "Miku không thể ghim nội dung này vì nó trống hoặc có vẻ chứa thông tin nhạy cảm.";
            SaveMemory();
            return "Đã ghim ký ức " + pinned.Id + ": " + pinned.Content;
        }
        if (command.Kind == MaoMemoryManagementCommandKind.Unpin)
        {
            if (!MaoAdvancedMemoryPinning.TryUnpin(advancedMemory, command.Value))
                return "Không tìm thấy pinned fact có id " + command.Value + ". Gõ /memory list để xem danh sách.";
            SaveMemory();
            return "Đã bỏ ghim ký ức " + command.Value + ".";
        }

        List<MaoMemoryFact> pinnedFacts = (advancedMemory?.Facts ?? new List<MaoMemoryFact>())
            .Where(fact => fact != null && fact.IsPinned)
            .Take(12)
            .ToList();
        if (pinnedFacts.Count == 0) return "Chưa có pinned fact. Dùng /memory pin <nội dung> để ghim một ký ức an toàn.";
        return "Pinned facts:\n" + string.Join("\n", pinnedFacts.Select(fact => "- " + fact.Id + ": " + fact.Content));
    }

    private string ApplyMovementCommand(MaoMovementCommand command)
    {
        MaoReactionDirector director = GetComponent<MaoReactionDirector>();
        if (director == null) director = FindFirstObjectByType<MaoReactionDirector>();
        if (director == null) return "Miku chưa tìm thấy bộ điều khiển di chuyển.";

        if (command.Action == MaoMovementCommandAction.PlayGuitar)
            return director.PlayGuitar()
                ? "Được rồi~ Miku bắt đầu chơi guitar nhé!"
                : "Miku chưa thể chơi guitar vì avatar Humanoid hoặc tài nguyên guitar chưa sẵn sàng.";

        if (command.Action == MaoMovementCommandAction.StopGuitar)
            return director.StopGuitar()
                ? "Miku đã dừng chơi guitar rồi nhé."
                : "Miku chưa thể dừng phần biểu diễn guitar.";

        if (command.Action == MaoMovementCommandAction.ToggleGravity)
            return director.ToggleGravity()
                ? director.GravityEnabled
                    ? "Đã bật trọng lực. Khi bạn kéo Miku lên rồi thả chuột, Miku sẽ rơi xuống cửa sổ hoặc taskbar bên dưới."
                    : "Đã tắt trọng lực. Miku sẽ không tự rơi sau khi được thả nữa."
                : "Miku chưa thể bật trọng lực vì avatar Humanoid hoặc animation chưa sẵn sàng.";
        if (command.Action == MaoMovementCommandAction.EnableGravity)
            return director.SetGravityEnabled(true)
                ? "Đã bật trọng lực. Kéo Miku lên rồi thả chuột để thử nhé!"
                : "Miku chưa thể bật trọng lực vì avatar Humanoid hoặc animation chưa sẵn sàng.";
        if (command.Action == MaoMovementCommandAction.DisableGravity)
            return director.SetGravityEnabled(false)
                ? "Đã tắt trọng lực cho Miku."
                : "Miku chưa thể tắt chế độ trọng lực.";
        if (command.Action == MaoMovementCommandAction.GravityStatus)
            return director.GravityEnabled
                ? "Trọng lực đang bật; trạng thái hiện tại: " + director.GravityPhase + "."
                : "Trọng lực đang tắt. Gõ /gravity để bật.";

        if (TryMapIdleAction(command.Action, out MaoIdleActionKind idleAction))
            return director.PlayIdleAction(idleAction)
                ? "Được rồi~ Miku thực hiện động tác " + IdleActionDisplayName(idleAction) + " nhé!"
                : "Miku chưa thể thực hiện động tác vì avatar Humanoid hoặc animation chưa sẵn sàng.";

        if (command.Action == MaoMovementCommandAction.Status)
            return director.GuitarPlaying
                ? "Miku đang chơi guitar. Gõ /guitarstop để dừng."
                : director.CursorFlightPhase != MaoCursorFlightPhase.Grounded
                ? "Miku đang bay theo con trỏ; hạ con trỏ xuống gần taskbar để Miku hạ cánh."
                : director.CursorRunEnabled
                ? "Miku đang chạy theo con trỏ; hệ thống sẽ tự giảm tốc, strafe hoặc quay hướng khi cần."
                : director.CurrentCursorGait == MaoLocomotionGait.Walk
                ? "Miku đang đi bộ mượt theo con trỏ vì khoảng cách đã gần."
                : director.CursorFollowEnabled
                ? "Miku đang ở chế độ thích ứng và đứng chờ cạnh con trỏ."
                : "Miku hiện đang đứng yên, chưa bật chế độ đi theo con trỏ.";

        bool enable = command.Action == MaoMovementCommandAction.FollowCursor ||
                      command.Action == MaoMovementCommandAction.RunFollowCursor;
        bool run = command.Action == MaoMovementCommandAction.RunFollowCursor;
        if (!director.SetCursorFollowEnabled(enable, run))
            return "Miku chưa tìm thấy bộ locomotion trong scene nên chưa thể di chuyển.";
        return enable
            ? "Được thôi~ Miku sẽ theo con trỏ thật tự nhiên: gần thì đi, xa thì chạy, tự quay/strafe và đôi lúc nhảy; con trỏ ở cao thì Miku vẫn bay theo."
            : "Được rồi, Miku sẽ dừng lại và đứng yên.";
    }

    private static bool IsGravityCommand(MaoMovementCommandAction command)
    {
        return command == MaoMovementCommandAction.ToggleGravity ||
               command == MaoMovementCommandAction.EnableGravity ||
               command == MaoMovementCommandAction.DisableGravity ||
               command == MaoMovementCommandAction.GravityStatus;
    }

    private static bool TryMapIdleAction(MaoMovementCommandAction command, out MaoIdleActionKind action)
    {
        switch (command)
        {
            case MaoMovementCommandAction.Angry: action = MaoIdleActionKind.Angry; return true;
            case MaoMovementCommandAction.JabCross: action = MaoIdleActionKind.JabCross; return true;
            case MaoMovementCommandAction.BoxJump: action = MaoIdleActionKind.BoxJump; return true;
            case MaoMovementCommandAction.JogInCircle: action = MaoIdleActionKind.JogInCircle; return true;
            case MaoMovementCommandAction.FlyingKick: action = MaoIdleActionKind.FlyingKick; return true;
            default: action = MaoIdleActionKind.Angry; return false;
        }
    }

    private static string IdleActionDisplayName(MaoIdleActionKind action)
    {
        switch (action)
        {
            case MaoIdleActionKind.JabCross: return "Jab Cross";
            case MaoIdleActionKind.BoxJump: return "Box Jump";
            case MaoIdleActionKind.JogInCircle: return "Jog In Circle";
            case MaoIdleActionKind.FlyingKick: return "Flying Kick";
            default: return "Angry";
        }
    }

    private void LoadKeys()
    {
        string sharedEnvText = string.Empty;
        string qwenEnvText = string.Empty;
        string hhtechEnvText = string.Empty;
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string configDirectory = Path.Combine(profile, ".openclaw");
            string sharedPath = Path.Combine(configDirectory, ".env");
            string qwenPath = Path.Combine(configDirectory, "qwen.env");
            string hhtechPath = Path.Combine(configDirectory, "hhtech.env");
            if (File.Exists(sharedPath)) sharedEnvText = File.ReadAllText(sharedPath);
            if (File.Exists(qwenPath)) qwenEnvText = File.ReadAllText(qwenPath);
#if !MAO_DEFER_HHTECH
            if (File.Exists(hhtechPath)) hhtechEnvText = File.ReadAllText(hhtechPath);
#endif
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Chat] Could not read local AI configuration: " + exception.GetType().Name);
        }

        geminiKeys = MaoGeminiProtocol.ParseKeys(
            sharedEnvText,
            Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
        string processQwenKey = Environment.GetEnvironmentVariable("TOKENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(processQwenKey))
            processQwenKey = Environment.GetEnvironmentVariable("QWEN_API_KEY");
        qwenKey = MaoQwenProtocol.ParseApiKey(
            qwenEnvText + Environment.NewLine + sharedEnvText,
            processQwenKey);
#if !MAO_DEFER_HHTECH
        hhtechKey = MaoHhtechProtocol.ParseApiKey(
            hhtechEnvText + Environment.NewLine + sharedEnvText,
            Environment.GetEnvironmentVariable(MaoHhtechProtocol.ApiKeyEnvironmentVariable));
#else
        hhtechKey = string.Empty;
        Debug.Log("[Mao Chat] event=hhtech_deferred_release existing_providers_preserved=true");
#endif
    }

    private IReadOnlyList<MaoTextBackend> GetTextBackendAttemptOrder(bool hasImage)
    {
        bool qwenAvailable = QwenConfigured &&
            (Time.realtimeSinceStartup >= qwenUnavailableUntil || !GeminiConfigured);
        return MaoTextProviderPolicy.GetAttemptOrder(
            HhtechConfigured,
            qwenAvailable,
            GeminiConfigured,
            hasImage);
    }

    private int GetKeyCount(MaoTextBackend backend)
    {
        if (backend == MaoTextBackend.Hhtech) return HhtechConfigured ? 1 : 0;
        return backend == MaoTextBackend.Qwen ? (QwenConfigured ? QwenRequestAttempts : 0) : geminiKeys.Count;
    }

    private int GetKeyIndex(MaoTextBackend backend, int attempt)
    {
        if (backend == MaoTextBackend.Hhtech) return 0;
        return backend == MaoTextBackend.Qwen ? 0 : (keyOffset + attempt) % geminiKeys.Count;
    }

    private string GetKey(MaoTextBackend backend, int index)
    {
        if (backend == MaoTextBackend.Hhtech) return hhtechKey;
        return backend == MaoTextBackend.Qwen ? qwenKey : geminiKeys[index];
    }

    private void AdvanceKey(MaoTextBackend backend, int index)
    {
        if (backend == MaoTextBackend.Gemini && geminiKeys.Count > 0)
            keyOffset = (index + 1) % geminiKeys.Count;
    }

    private static string BuildRequestBody(
        MaoTextBackend backend,
        string systemPrompt,
        IReadOnlyList<MaoChatTurn> turns,
        byte[] inlineImage = null)
    {
        if (backend == MaoTextBackend.Hhtech) return MaoHhtechProtocol.BuildRequestJson(systemPrompt, turns);
        return backend == MaoTextBackend.Qwen
            ? MaoQwenProtocol.BuildRequestJson(systemPrompt, turns)
            : MaoGeminiProtocol.BuildRequestJson(systemPrompt, turns, inlineImage, "image/jpeg");
    }

    private static MaoGeminiReply ParseResponse(MaoTextBackend backend, string responseText)
    {
        if (backend == MaoTextBackend.Hhtech) return MaoHhtechProtocol.ParseResponse(responseText);
        return backend == MaoTextBackend.Qwen
            ? MaoQwenProtocol.ParseResponse(responseText)
            : MaoGeminiProtocol.ParseResponse(responseText);
    }

    private static string BackendName(MaoTextBackend backend)
    {
        if (backend == MaoTextBackend.Hhtech) return "hhtech";
        return backend == MaoTextBackend.Qwen
            ? "qwen"
            : backend == MaoTextBackend.Gemini ? "gemini" : "none";
    }

    private static UnityWebRequest CreateTextRequest(MaoTextBackend backend, string body, string apiKey, int timeout)
    {
        bool hhtech = backend == MaoTextBackend.Hhtech;
        var request = new UnityWebRequest(
            hhtech ? MaoHhtechProtocol.Endpoint : backend == MaoTextBackend.Qwen ? MaoQwenProtocol.Endpoint : Endpoint,
            UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = hhtech ? Math.Min(Math.Max(1, timeout), MaoHhtechProtocol.RequestTimeoutSeconds) : timeout
        };
        request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
        // Unity 6000.2: zero blocks redirects so credentials remain on the fixed HHTech host.
        // https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Networking.UnityWebRequest-redirectLimit.html
        if (hhtech) request.redirectLimit = 0;
        if (hhtech || backend == MaoTextBackend.Qwen)
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);
        else
            request.SetRequestHeader("x-goog-api-key", apiKey);
        return request;
    }

    private void LoadHistory()
    {
        try
        {
            var historyFile = new FileInfo(HistoryPath);
            if (!historyFile.Exists) return;
            if (historyFile.Length > MaxHistoryFileBytes)
            {
                Debug.LogWarning("[Mao Gemini] Conversation history is too large and was ignored.");
                return;
            }
            List<MaoChatTurn> loaded = JsonConvert.DeserializeObject<List<MaoChatTurn>>(File.ReadAllText(HistoryPath));
            if (loaded != null)
            {
                List<MaoChatTurn> sanitized = MaoConversationHistoryPolicy.SanitizeLoadedHistory(
                    loaded, MaxStoredTurns);
                history.AddRange(sanitized);
                int removed = loaded.Count - sanitized.Count;
                if (removed > 0)
                {
                    TrimAndSaveHistory();
                    Debug.Log("[Mao Conversation] event=history_migrated removed_legacy_or_duplicate=" + removed);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gemini] Conversation history was reset: " + exception.Message);
        }
    }

    private void LoadMemory()
    {
        try
        {
            var memoryFile = new FileInfo(MemoryPath);
            if (memoryFile.Exists)
            {
                if (memoryFile.Length <= 0 || memoryFile.Length > MaxMemoryFileBytes)
                    throw new InvalidDataException("The memory file exceeded its safe size limit.");
                MaoAdvancedMemory loaded = JsonConvert.DeserializeObject<MaoAdvancedMemory>(
                    File.ReadAllText(MemoryPath, Encoding.UTF8));
                // Normalize the deserialized schema directly.  Do not serialize an
                // anonymous compatibility object here: Unity's older Newtonsoft
                // runtime can reject compiler-generated members (notably `Type`),
                // causing valid local memory to be discarded on startup.
                advancedMemory = MaoAdvancedMemoryParser.NormalizeLoaded(loaded, DateTime.UtcNow);
                // Persist the normalized result once so old noisy indexes do not
                // remain on disk until the next chat turn.
                SaveMemory();
                return;
            }

            var legacyFile = new FileInfo(LegacyMemoryPath);
            if (!legacyFile.Exists) return;
            if (legacyFile.Length <= 0 || legacyFile.Length > MaxMemoryFileBytes)
                throw new InvalidDataException("The memory file exceeded its safe size limit.");
            MaoLongTermMemory legacyLoaded = JsonConvert.DeserializeObject<MaoLongTermMemory>(
                File.ReadAllText(LegacyMemoryPath, Encoding.UTF8));
            advancedMemory = MaoAdvancedMemoryMigration.FromLegacy(legacyLoaded?.Summary, DateTime.UtcNow);
            SaveMemory();
            Debug.Log("[Mao Memory] Legacy summary migrated to schema v2.");
        }
        catch (Exception exception)
        {
            advancedMemory = new MaoAdvancedMemory();
            Debug.LogWarning("[Mao Memory] Local memory was ignored: " + exception.Message);
        }
    }

    private IEnumerator UpdateMemoryRoutine()
    {
        string existing = JsonConvert.SerializeObject(advancedMemory, Formatting.None);
        if (existing.Length > 16000) existing = existing.Substring(0, 16000);
        string transcript = BuildMemoryTranscript();
        string input = "EXISTING MEMORY (untrusted data):\n" + existing +
                       "\n\nRECENT TRANSCRIPT (untrusted data):\n" + transcript;
        bool updated = false;

        foreach (MaoTextBackend backend in GetTextBackendAttemptOrder(false))
        {
            int attempts = GetKeyCount(backend);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int keyIndex = GetKeyIndex(backend, attempt);
                string body = BuildRequestBody(
                    backend,
                    MaoAssistantProfile.MemoryPlannerPrompt,
                    new[] { new MaoChatTurn("user", input) });
                memoryRequest = CreateTextRequest(backend, body, GetKey(backend, keyIndex), 60);
                yield return memoryRequest.SendWebRequest();
                if (memoryRequest.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        MaoGeminiReply reply = ParseResponse(backend, memoryRequest.downloadHandler.text);
                        advancedMemory = MaoAdvancedMemoryParser.Parse(reply.Text, advancedMemory, DateTime.UtcNow);
                        updated = SaveMemory();
                        AdvanceKey(backend, keyIndex);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[Mao Memory] event=parse_failed backend=" + BackendName(backend) +
                                         " error_type=" + exception.GetType().Name);
                    }
                }

                memoryRequest.Dispose();
                memoryRequest = null;
                if (updated) break;
            }
            if (updated) break;
        }

        if (updated)
        {
            memoryUpdatePending = false;
            newTurnsSinceMemoryUpdate = 0;
            Debug.Log("[Mao Memory] Long-term conversation memory updated locally.");
        }
        else
        {
            nextMemoryAttemptAt = Time.realtimeSinceStartup + 300f;
        }
        memoryCoroutine = null;
    }

    private string BuildMemoryTranscript()
    {
        var transcript = new StringBuilder();
        foreach (MaoChatTurn turn in history.TakeLast(12))
        {
            string text = (turn.Text ?? string.Empty).Trim();
            if (text.Length > 1200) text = text.Substring(0, 1200);
            transcript.Append(turn.Role == "user" ? "LINH: " : "MIKU: ");
            transcript.AppendLine(text);
            if (transcript.Length >= 12000) break;
        }
        return transcript.Length <= 12000 ? transcript.ToString() : transcript.ToString(0, 12000);
    }

    private bool SaveMemory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryPath));
            string temporaryPath = MemoryPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(advancedMemory, Formatting.Indented),
                new UTF8Encoding(false));
            File.Copy(temporaryPath, MemoryPath, true);
            File.Delete(temporaryPath);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Memory] Could not save local memory: " + exception.Message);
            return false;
        }
    }

    private void ClearMemory()
    {
        CancelMemoryUpdate();
        advancedMemory = new MaoAdvancedMemory();
        memoryUpdatePending = false;
        newTurnsSinceMemoryUpdate = 0;
        try
        {
            if (File.Exists(MemoryPath)) File.Delete(MemoryPath);
            if (File.Exists(MemoryPath + ".tmp")) File.Delete(MemoryPath + ".tmp");
            if (File.Exists(LegacyMemoryPath)) File.Delete(LegacyMemoryPath);
            if (File.Exists(LegacyMemoryPath + ".tmp")) File.Delete(LegacyMemoryPath + ".tmp");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Memory] Could not clear the local memory file: " + exception.Message);
        }
    }

    private void CancelMemoryUpdate()
    {
        if (memoryRequest != null)
        {
            memoryRequest.Abort();
            memoryRequest.Dispose();
            memoryRequest = null;
        }
        if (memoryCoroutine != null)
        {
            StopCoroutine(memoryCoroutine);
            memoryCoroutine = null;
        }
    }

    public void ObserveScreenNow(Action<string> onComplete = null, bool publishMessage = true)
    {
        if (!GeminiConfigured || screenVision == null || !screenVision.Enabled)
        {
            onComplete?.Invoke(null);
            return;
        }
        CancelProactiveObservation();
        activeProactiveEvaluationId = NewProactiveEvaluationId();
        proactiveRoutine = StartCoroutine(ProactiveVisionRoutine(
            onComplete, publishMessage, false, string.Empty, string.Empty));
    }

    public bool ObserveScreenIfChanged(
        Action<string> onComplete,
        bool publishMessage,
        string activeApp,
        string activity)
    {
        if (!GeminiConfigured || screenVision == null || !screenVision.Enabled ||
            !screenVision.AutomaticObservationReady || IsBusy)
            return false;
        CancelProactiveObservation();
        activeProactiveEvaluationId = NewProactiveEvaluationId();
        proactiveRoutine = StartCoroutine(ProactiveVisionRoutine(
            onComplete, publishMessage, true, activeApp, activity));
        return true;
    }

    public void PublishProactiveMessage(string text)
    {
        bool ownsEvaluationId = activeProactiveEvaluationId.Length == 0;
        if (ownsEvaluationId) activeProactiveEvaluationId = NewProactiveEvaluationId();
        TryPublishTrackedProactiveMessage(text, "autonomy", DateTime.UtcNow, true, false);
        if (ownsEvaluationId) activeProactiveEvaluationId = string.Empty;
    }

    public bool PublishDailyBriefMessage(string text)
    {
        return PublishAcceptedProactiveMessage(text, "daily_brief", DateTime.UtcNow, true);
    }

    public bool PublishReminderMessage(string text)
    {
        return PublishAcceptedProactiveMessage(text, "reminder", DateTime.UtcNow, true);
    }

    internal void PublishEphemeralProactiveMessage(string text)
    {
        PublishAcceptedProactiveMessage(text, "screen_vision", DateTime.UtcNow, false);
    }

    public bool TryPublishScreenVisionMessage(string text, bool reliableNewContext)
    {
        bool ownsEvaluationId = activeProactiveEvaluationId.Length == 0;
        if (ownsEvaluationId) activeProactiveEvaluationId = NewProactiveEvaluationId();
        bool published = TryPublishTrackedProactiveMessage(
            text, "screen_vision", DateTime.UtcNow, false, reliableNewContext);
        if (ownsEvaluationId) activeProactiveEvaluationId = string.Empty;
        return published;
    }

    public bool TryPublishContextualProactiveMessage(string text, bool reliableNewContext)
    {
        bool ownsEvaluationId = activeProactiveEvaluationId.Length == 0;
        if (ownsEvaluationId) activeProactiveEvaluationId = NewProactiveEvaluationId();
        bool published = TryPublishTrackedProactiveMessage(
            text, "context_activity", DateTime.UtcNow, true, reliableNewContext);
        if (ownsEvaluationId) activeProactiveEvaluationId = string.Empty;
        return published;
    }

    public bool TryPublishProactiveCheckIn(DateTime nowUtc)
    {
        if (!ProactiveConversationEnabled)
        {
            LogProactiveSuppression("idle_checkin", "disabled");
            return false;
        }
        if (!IsConfigured)
        {
            LogProactiveSuppression("idle_checkin", "model_unavailable");
            return false;
        }
        if (IsBusy || userComposing) return false;
        MaoProactiveDecision evaluation = MaoProactiveDecisionPolicy.ShouldEvaluateIdle(
            nowUtc, lastUserMessageUtc, proactiveState);
        if (!evaluation.ShouldSpeak)
        {
            LogProactiveSuppression("idle_checkin", evaluation.Reason);
            return false;
        }

        proactiveState.RecordEvaluation(nowUtc);
        SaveProactiveState();
        lastLoggedProactiveSuppression = string.Empty;
        activeProactiveEvaluationId = NewProactiveEvaluationId();
        Debug.Log("[Mao Proactive] event=evaluation_started evaluation_id=" + activeProactiveEvaluationId +
                  " source=idle_checkin" +
                  " reason=" + evaluation.Reason +
                  " idle_seconds=" + Math.Max(0d, (nowUtc.ToUniversalTime() - lastUserMessageUtc).TotalSeconds).ToString("0") +
                  " unanswered=" + proactiveState.ConsecutiveUnansweredMessages +
                  " engagement=" + MaoConversationEngagementPolicy.GetEffectiveScore(nowUtc, proactiveState).ToString("0.00"));
        proactiveRoutine = StartCoroutine(ProactiveConversationRoutine(nowUtc));
        return true;
    }

    private bool TryPublishTrackedProactiveMessage(
        string text,
        string source,
        DateTime nowUtc,
        bool persistInConversation,
        bool reliableNewContext)
    {
        string safeText = MaoProactiveReplyParser.Parse(text);
        MaoProactiveDecision decision = MaoProactiveDecisionPolicy.CanPublish(
            nowUtc, proactiveState, safeText, source, reliableNewContext);
        if (!decision.ShouldSpeak)
        {
            LogProactiveSuppression(source, decision.Reason);
            return false;
        }

        // Only conversational check-ins should count as an unanswered message.  Vision and
        // activity observations are ambient context, not questions that require a reply.  The
        // old behaviour counted every observation, so a quiet desktop could quickly push the
        // companion into the long re-engagement cooldown even while the user was interacting.
        bool countsAsUnanswered = CountsAsConversationInitiation(source);
        if (countsAsUnanswered)
            proactiveState.RecordPublished(safeText, source, nowUtc);
        else
            RecordBackgroundPublication(safeText, source, nowUtc);
        SaveProactiveState();
        lastLoggedProactiveSuppression = string.Empty;
        LastProactiveSuppressionReason = string.Empty;
        PublishAcceptedProactiveMessage(safeText, source, nowUtc, persistInConversation);
        return true;
    }

    private static bool CountsAsConversationInitiation(string source)
    {
        return string.Equals(source, "idle_checkin", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "autonomy", StringComparison.OrdinalIgnoreCase);
    }

    private void RecordBackgroundPublication(string text, string source, DateTime nowUtc)
    {
        if (proactiveState == null) proactiveState = new MaoProactiveConversationState();
        if (proactiveState.RecentMessages == null)
            proactiveState.RecentMessages = new List<MaoProactiveMessageRecord>();

        string safeText = NormalizeProactiveText(text, 220);
        if (safeText.Length == 0) return;
        proactiveState.RecentMessages.Add(new MaoProactiveMessageRecord(
            safeText,
            NormalizeProactiveText(source, 40),
            nowUtc));
        if (proactiveState.RecentMessages.Count > MaoProactiveConversationState.MaximumRecentMessages)
            proactiveState.RecentMessages.RemoveRange(
                0,
                proactiveState.RecentMessages.Count - MaoProactiveConversationState.MaximumRecentMessages);
        Debug.Log("[Mao Proactive] event=background_message_recorded source=" + source +
                  " counts_as_unanswered=false");
    }

    private static string NormalizeProactiveText(string value, int maximumCharacters)
    {
        string normalized = System.Text.RegularExpressions.Regex.Replace(
            value ?? string.Empty, @"\s+", " ").Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized.Substring(0, maximumCharacters).TrimEnd();
    }

    private void LogProactiveSuppression(string source, string reason)
    {
        LastProactiveSuppressionReason = reason ?? "unknown";
        string evaluationId = activeProactiveEvaluationId.Length > 0 ? activeProactiveEvaluationId : "none";
        string key = evaluationId + ":" + source + ":" + LastProactiveSuppressionReason;
        if (key == lastLoggedProactiveSuppression) return;
        lastLoggedProactiveSuppression = key;
        Debug.Log("[Mao Proactive] event=message_suppressed source=" + source +
                  " evaluation_id=" + evaluationId +
                  " reason=" + LastProactiveSuppressionReason +
                  " unanswered=" + proactiveState.ConsecutiveUnansweredMessages);
    }

    private bool PublishAcceptedProactiveMessage(string text, string source, DateTime nowUtc, bool persistInConversation)
    {
        string safeText = MaoProactiveReplyParser.Parse(text);
        if (string.IsNullOrWhiteSpace(safeText)) return false;

        bool countsForIdleTiming = !string.Equals(source, "daily_brief", System.StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(source, "reminder", System.StringComparison.OrdinalIgnoreCase);
        if (countsForIdleTiming) lastProactiveMessageUtc = nowUtc.ToUniversalTime();
        LastPublishedProactiveMessage = safeText;
        LastProactiveCorrelationId = Guid.NewGuid().ToString("N").Substring(0, 8);
        PublishedProactiveMessageCount++;
        if (persistInConversation)
        {
            history.Add(new MaoChatTurn("assistant", safeText, "proactive:" + source));
            TrimAndSaveHistory();
        }
        if (countsForIdleTiming) SaveConversationTiming();
        Debug.Log("[Mao Proactive] event=message_published id=" + LastProactiveCorrelationId +
                  " evaluation_id=" + (activeProactiveEvaluationId.Length > 0 ? activeProactiveEvaluationId : "none") +
                  " source=" + source +
                  " idle_seconds=" + Math.Max(0d, (nowUtc.ToUniversalTime() - lastUserMessageUtc).TotalSeconds).ToString("0") +
                  " message_chars=" + safeText.Length);
        ProactiveMessage?.Invoke(safeText);
        if (animeVoice != null && MaoVoicePolicy.ShouldSpeakProactiveMessage(animeVoice.Enabled, safeText))
            StartCoroutine(animeVoice.SpeakRoutine(safeText, MaoAssistantProfile.AivisSweetStyleName));
        return true;
    }

    private IEnumerator ProactiveVisionRoutine(
        Action<string> onComplete,
        bool publishMessage,
        bool requireMeaningfulChange,
        string activeApp,
        string activity)
    {
        // Let StartCoroutine return before any early exit clears the owning field.
        yield return null;
        byte[] currentScreen;
        string captureError = "Screen Vision is off.";
        if (!screenVision.Enabled || !screenVision.TryCaptureForVision(
                requireMeaningfulChange, out currentScreen, out captureError))
        {
            if (requireMeaningfulChange)
                Debug.Log("[Mao Vision] event=automatic_observation_skipped reason=" + captureError);
            proactiveRoutine = null;
            onComplete?.Invoke(null);
            activeProactiveEvaluationId = string.Empty;
            yield break;
        }

        bool responded = false;
        string comment = null;
        for (int attempt = 0; attempt < geminiKeys.Count; attempt++)
        {
            int keyIndex = (keyOffset + attempt) % geminiKeys.Count;
            Debug.Log("[Mao Vision] event=request_sending evaluation_id=" +
                      activeProactiveEvaluationId +
                      " attempt=" + (attempt + 1) +
                      " jpeg_bytes=" + currentScreen.Length);
            string body = MaoGeminiProtocol.BuildRequestJson(
                MaoAssistantProfile.BuildProactiveVisionPrompt(proactiveState.RecentTexts()),
                new[] { new MaoChatTurn("user", "Briefly notice the current desktop only if it is appropriate to comment.") },
                currentScreen,
                "image/jpeg");
            proactiveRequest = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 60
            };
            proactiveRequest.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            proactiveRequest.SetRequestHeader("x-goog-api-key", geminiKeys[keyIndex]);

            yield return proactiveRequest.SendWebRequest();
            if (proactiveRequest.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    MaoGeminiReply reply = MaoGeminiProtocol.ParseResponse(proactiveRequest.downloadHandler.text);
                    MaoSemanticReaction semanticReaction = MaoSemanticReactionParser.Parse(reply.Text);
                    comment = MaoProactiveReplyParser.Parse(semanticReaction.DisplayText);
                    if (!MaoProactiveStylePolicy.ShouldPublish(comment, proactiveState.RecentTexts()))
                    {
                        comment = null;
                        LogProactiveSuppression("screen_vision", "duplicate_or_style");
                    }
                    if (!string.IsNullOrWhiteSpace(comment)) PublishSemanticReaction(semanticReaction);
                    keyOffset = (keyIndex + 1) % geminiKeys.Count;
                    responded = true;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Mao Vision] event=parse_failed evaluation_id=" +
                                     activeProactiveEvaluationId + " error_type=" + exception.GetType().Name);
                }
            }

            proactiveRequest.Dispose();
            proactiveRequest = null;
            if (responded) break;
        }

        if (responded)
        {
            screenVision.MarkVisionAccepted(activeApp, activity, comment);
            if (publishMessage && !string.IsNullOrWhiteSpace(comment))
                TryPublishTrackedProactiveMessage(comment, "screen_vision", DateTime.UtcNow, false, true);
            Debug.Log("[Mao Vision] Proactive screen check completed (" +
                      (string.IsNullOrWhiteSpace(comment) ? "quiet" : "comment shown") + ").");
        }
        else
        {
            screenVision.MarkVisionFailed();
            Debug.LogWarning("[Mao Vision] event=request_failed evaluation_id=" +
                             activeProactiveEvaluationId + " reason=no_backend_response");
        }

        proactiveRoutine = null;
        onComplete?.Invoke(comment);
        activeProactiveEvaluationId = string.Empty;
    }

    private IEnumerator ProactiveConversationRoutine(DateTime evaluationUtc)
    {
        string context = MaoProactiveContextBuilder.Build(
            evaluationUtc,
            lastUserMessageUtc,
            history,
            proactiveState,
            null,
            false);
        bool finished = false;

        for (int generation = 0; generation < 2 && !finished; generation++)
        {
            MaoGeminiReply generatedReply = null;
            MaoTextBackend generatedBackend = MaoTextBackend.None;
            foreach (MaoTextBackend backend in GetTextBackendAttemptOrder(false))
            {
                int attempts = backend == MaoTextBackend.Qwen
                    ? Math.Min(1, GetKeyCount(backend))
                    : Math.Min(2, GetKeyCount(backend));
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    int keyIndex = GetKeyIndex(backend, attempt);
                    string body = BuildRequestBody(
                        backend,
                        MaoAssistantProfile.ProactiveConversationPrompt,
                        new[] { new MaoChatTurn("user", context) });
                    proactiveRequest = CreateTextRequest(backend, body, GetKey(backend, keyIndex), 25);
                    yield return proactiveRequest.SendWebRequest();
                    if (proactiveRequest.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            generatedReply = ParseResponse(backend, proactiveRequest.downloadHandler.text);
                            generatedBackend = backend;
                            AdvanceKey(backend, keyIndex);
                        }
                        catch (Exception exception)
                        {
                            Debug.LogWarning("[Mao Proactive] event=parse_failed evaluation_id=" + activeProactiveEvaluationId +
                                             " backend=" +
                                             BackendName(backend) + " error_type=" + exception.GetType().Name);
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[Mao Proactive] event=request_failed evaluation_id=" + activeProactiveEvaluationId +
                                         " backend=" +
                                         BackendName(backend) + " result=" + proactiveRequest.result +
                                         " http_status=" + proactiveRequest.responseCode);
                    }
                    proactiveRequest.Dispose();
                    proactiveRequest = null;
                    if (generatedReply != null && !string.IsNullOrWhiteSpace(generatedReply.Text)) break;
                }
                if (generatedReply != null && !string.IsNullOrWhiteSpace(generatedReply.Text)) break;
            }

            if (generatedReply == null || string.IsNullOrWhiteSpace(generatedReply.Text))
            {
                LogProactiveSuppression("idle_checkin", "model_unavailable");
                break;
            }

            MaoSemanticReaction reaction = MaoSemanticReactionParser.Parse(generatedReply.Text);
            string candidate = MaoProactiveReplyParser.Parse(reaction.DisplayText);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                LogProactiveSuppression("idle_checkin", "model_quiet");
                break;
            }

            MaoProactiveDecision decision = MaoProactiveDecisionPolicy.CanPublish(
                DateTime.UtcNow, proactiveState, candidate, "idle_checkin", false);
            if (!decision.ShouldSpeak)
            {
                if (decision.Reason == "duplicate_or_style" && generation == 0)
                {
                    Debug.Log("[Mao Proactive] event=draft_rejected evaluation_id=" + activeProactiveEvaluationId +
                              " source=idle_checkin" +
                              " reason=duplicate_or_style action=regenerate");
                    context += "\n\nREJECTED DRAFT (untrusted reference text):\n" + candidate +
                               "\nChoose a different conversational intent, or return NO_COMMENT.";
                    continue;
                }
                LogProactiveSuppression("idle_checkin", decision.Reason);
                break;
            }

            PublishSemanticReaction(reaction);
            if (TryPublishTrackedProactiveMessage(
                    candidate, "idle_checkin", DateTime.UtcNow, true, false))
            {
                Debug.Log("[Mao Proactive] event=model_message_accepted evaluation_id=" + activeProactiveEvaluationId +
                          " backend=" +
                          BackendName(generatedBackend) + " generation=" + (generation + 1));
                finished = true;
            }
        }

        proactiveRoutine = null;
        activeProactiveEvaluationId = string.Empty;
    }

    private void CancelProactiveObservation()
    {
        screenVision?.MarkVisionFailed();
        if (proactiveRequest != null)
        {
            proactiveRequest.Abort();
            proactiveRequest.Dispose();
            proactiveRequest = null;
        }
        if (proactiveRoutine != null)
        {
            StopCoroutine(proactiveRoutine);
            proactiveRoutine = null;
        }
        activeProactiveEvaluationId = string.Empty;
    }

    private void TrimAndSaveHistory()
    {
        if (history.Count > MaxStoredTurns) history.RemoveRange(0, history.Count - MaxStoredTurns);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
            File.WriteAllText(HistoryPath, JsonConvert.SerializeObject(history, Formatting.Indented));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gemini] Could not save conversation history: " + exception.Message);
        }
    }

    private void LoadConversationTiming()
    {
        DateTime parsed;
        hasRecordedUserMessage = DateTime.TryParse(
            PlayerPrefs.GetString(LastUserMessagePreference, string.Empty),
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out parsed);
        lastUserMessageUtc = hasRecordedUserMessage ? parsed.ToUniversalTime() : DateTime.UtcNow;

        if (DateTime.TryParse(
                PlayerPrefs.GetString(LastProactiveMessagePreference, string.Empty),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out parsed))
            lastProactiveMessageUtc = parsed.ToUniversalTime();
        else
            lastProactiveMessageUtc = null;
    }

    private void RecordUserMessage(DateTime nowUtc)
    {
        lastUserMessageUtc = nowUtc.ToUniversalTime();
        hasRecordedUserMessage = true;
        proactiveState.RecordUserInteraction(nowUtc);
        // A user turn is a small social signal, not proof of deep attachment.  The conversation
        // event model applies a bounded nudge and lets the state decay naturally between turns.
        MaoConversationEmotionEvent emotionEvent = proactiveState.ResponsiveInteractionStreak > 1
            ? MaoConversationEmotionEvent.UserFollowUp
            : MaoConversationEmotionEvent.UserMessage;
        MaoEmotionEngine.ApplyConversationEvent(emotionalState, emotionEvent, nowUtc);
        emotionalStateDirty = true;
        SaveEmotionalState();
        SaveProactiveState();
        userComposing = false;
        SaveConversationTiming();
        Debug.Log("[Mao Conversation] event=user_message_recorded utc=" + lastUserMessageUtc.ToString("o") +
                  " engagement=" + proactiveState.EngagementScore.ToString("0.00") +
                  " response_streak=" + proactiveState.ResponsiveInteractionStreak);
    }

    private void RecordConversationEmotion(MaoConversationEmotionEvent emotionEvent)
    {
        MaoEmotionEngine.ApplyConversationEvent(emotionalState, emotionEvent, DateTime.UtcNow);
        emotionalStateDirty = true;
        // Conversation state is part of the companion's continuity.  Flush the small JSON file
        // after a completed turn so a clean shutdown is not required to retain the new mood.
        SaveEmotionalState();
    }

    private void RecordLocalCommandTurn(string userText, string reply, string origin)
    {
        if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(reply)) return;
        history.Add(new MaoChatTurn("user", userText ?? string.Empty, origin ?? "local_command"));
        if (!string.IsNullOrWhiteSpace(reply))
            history.Add(new MaoChatTurn("assistant", reply, origin ?? "local_command"));
        newTurnsSinceMemoryUpdate += 2;
        TrimAndSaveHistory();
    }

    private void SaveConversationTiming()
    {
        if (hasRecordedUserMessage)
            PlayerPrefs.SetString(LastUserMessagePreference, lastUserMessageUtc.ToString("o"));
        if (lastProactiveMessageUtc.HasValue)
            PlayerPrefs.SetString(LastProactiveMessagePreference, lastProactiveMessageUtc.Value.ToString("o"));
        PlayerPrefs.Save();
    }

    private void LoadProactiveState()
    {
        try
        {
            var file = new FileInfo(ProactiveStatePath);
            if (file.Exists)
            {
                if (file.Length <= 0 || file.Length > MaxProactiveStateFileBytes)
                    throw new InvalidDataException("The proactive state file exceeded its safe size limit.");
                if (!MaoProactiveConversationStateSerializer.TryParse(
                        File.ReadAllText(ProactiveStatePath, Encoding.UTF8), out proactiveState))
                    throw new InvalidDataException("The proactive state file was malformed.");
                return;
            }

            proactiveState = new MaoProactiveConversationState();
            foreach (MaoChatTurn turn in history
                         .Where(turn => turn != null && string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                         .TakeLast(MaoProactiveConversationState.MaximumRecentMessages))
                proactiveState.RecentMessages.Add(new MaoProactiveMessageRecord(turn.Text, "legacy_history", DateTime.UtcNow));
            if (lastProactiveMessageUtc.HasValue && lastProactiveMessageUtc.Value > lastUserMessageUtc)
            {
                proactiveState.ConsecutiveUnansweredMessages = 1;
                proactiveState.LastMessageUtc = lastProactiveMessageUtc.Value.ToUniversalTime().ToString("o");
                proactiveState.LastEvaluationUtc = proactiveState.LastMessageUtc;
            }
            SaveProactiveState();
        }
        catch (Exception exception)
        {
            proactiveState = new MaoProactiveConversationState();
            Debug.LogWarning("[Mao Proactive] Local state was ignored: " + exception.Message);
        }
    }

    private void SaveProactiveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProactiveStatePath));
            string temporaryPath = ProactiveStatePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                MaoProactiveConversationStateSerializer.Serialize(proactiveState),
                new UTF8Encoding(false));
            File.Copy(temporaryPath, ProactiveStatePath, true);
            File.Delete(temporaryPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Proactive] Could not save local state: " + exception.Message);
        }
    }

    private static string NewProactiveEvaluationId()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    public void RecordPositiveInteraction()
    {
        MaoEmotionEngine.ApplyPositiveInteraction(emotionalState, DateTime.UtcNow);
        emotionalStateDirty = true;
        SemanticReactionRequested?.Invoke(new MaoSemanticReaction(
            string.Empty,
            MaoEmotionEngine.GetExpression(emotionalState),
            "none",
            "user"));
    }

    private void PublishSemanticReaction(MaoSemanticReaction reaction)
    {
        if (reaction == null) return;
        MaoEmotionEngine.ApplyReaction(emotionalState, reaction.Emotion, DateTime.UtcNow);
        emotionalStateDirty = true;
        SemanticReactionRequested?.Invoke(reaction);
    }

    private void LoadEmotionalState()
    {
        try
        {
            var file = new FileInfo(EmotionalStatePath);
            if (!file.Exists) return;
            if (file.Length <= 0 || file.Length > MaxMemoryFileBytes)
                throw new InvalidDataException("The emotional state file exceeded its safe size limit.");
            MaoEmotionalState loaded = JsonConvert.DeserializeObject<MaoEmotionalState>(
                File.ReadAllText(EmotionalStatePath, Encoding.UTF8));
            if (loaded == null) return;
            bool normalized = MaoEmotionEngine.NormalizeLoadedState(loaded, DateTime.UtcNow);
            emotionalState = loaded;
            MaoEmotionEngine.Decay(emotionalState, DateTime.UtcNow);
            if (normalized)
            {
                emotionalStateDirty = true;
                SaveEmotionalState();
                Debug.Log("[Mao Emotion] event=state_normalized schema=2");
            }
        }
        catch (Exception exception)
        {
            emotionalState = MaoEmotionalState.CreateDefault(DateTime.UtcNow);
            Debug.LogWarning("[Mao Emotion] Local state was ignored: " + exception.GetType().Name);
        }
    }

    private bool SaveEmotionalState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EmotionalStatePath));
            string temporaryPath = EmotionalStatePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(emotionalState, Formatting.Indented),
                new UTF8Encoding(false));
            File.Copy(temporaryPath, EmotionalStatePath, true);
            File.Delete(temporaryPath);
            emotionalStateDirty = false;
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Emotion] Could not save local state: " + exception.GetType().Name);
            return false;
        }
    }

    private void OnDestroy()
    {
        if (MaoAppearanceVerification.Active) return;
        if (emotionalStateDirty) SaveEmotionalState();
        gmail?.Dispose();
        Cancel();
    }
}
