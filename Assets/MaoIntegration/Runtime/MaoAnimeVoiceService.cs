using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using MaoIntegration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

public sealed class MaoAnimeVoiceService : MonoBehaviour
{
    private const string EnabledPreference = "Mao.AnimeVoice.Enabled";
    private const float AivisStartupTimeoutSeconds = 150f;
    private const float EdgeSynthesisTimeoutSeconds = 120f;
    private const int MaxMetadataBytes = 2 * 1024 * 1024;
    private const int MaxWaveBytes = 128 * 1024 * 1024;

    private AudioSource audioSource;
    private Process activeProcess;
    private Process ownedAivisEngine;
    private UnityWebRequest activeWebRequest;
    private string activeTextPath;
    private string activeAudioPath;
    private readonly Dictionary<string, int> aivisStyleIds = new Dictionary<string, int>();
    private readonly Dictionary<string, byte[]> recentWaves = new Dictionary<string, byte[]>();
    private readonly Queue<string> waveOrder = new Queue<string>();
    private int cachedWaveBytes;
    private Coroutine warmupRoutine;
    private float lastReadyAt = float.NegativeInfinity;
    private float speechStartedAt;

    private void Start()
    {
        if (Enabled) warmupRoutine = StartCoroutine(WarmupRoutine());
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mao-smoke-voice-latency") >= 0)
            StartCoroutine(VoiceLatencySmoke());
    }

    private IEnumerator VoiceLatencySmoke()
    {
        if (!Enabled)
        {
            Debug.Log("[Mao Voice] event=latency_smoke skipped=voice_disabled");
            yield break;
        }
        yield return null;
        while (warmupRoutine != null) yield return null;
        Debug.Log("[Mao Voice] event=latency_smoke sample=fresh");
        yield return SpeakRoutine("リン、何してるの？");
        Debug.Log("[Mao Voice] event=latency_smoke sample=cached");
        yield return SpeakRoutine("リン、何してるの？");
        Debug.Log("[Mao Voice] event=latency_smoke completed=true");
    }

    private IEnumerator WarmupRoutine()
    {
        // Load the same voice model silently before the first user response.
        yield return null;
        bool ready = false;
        yield return EnsureAivisReady(value => ready = value);
        if (ready && Enabled)
        {
            int styleId = -1;
            yield return ResolveAivisStyleId(MaoAssistantProfile.AivisSweetStyleName, value => styleId = value);
            if (styleId >= 0)
                yield return SynthesizeAivis("こんにちは。", styleId, MaoAssistantProfile.AivisSweetStyleName, wave =>
                    Debug.Log("[Mao Voice] event=warmup completed=" + (wave != null)));
        }
        warmupRoutine = null;
    }

    public bool Enabled
    {
        get => PlayerPrefs.GetInt(EnabledPreference, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(EnabledPreference, value ? 1 : 0);
            PlayerPrefs.Save();
            if (!value) Cancel();
        }
    }

    private void Awake()
    {
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        audioSource.volume = 0.92f;
    }

    public IEnumerator SpeakRoutine(string rawText)
    {
        yield return SpeakRoutine(rawText, MaoAssistantProfile.AivisSweetStyleName);
    }

    public IEnumerator SpeakRoutine(string rawText, string requestedStyle)
    {
        string speech = MaoSpeechText.Prepare(rawText);
        if (!Enabled || speech.Length == 0) yield break;

        string style = requestedStyle == MaoAssistantProfile.AivisTeasingStyleName
            ? MaoAssistantProfile.AivisTeasingStyleName
            : MaoAssistantProfile.AivisSweetStyleName;
        Cancel();
        speechStartedAt = Time.realtimeSinceStartup;

        string cacheKey = style + "\n" + speech;
        if (recentWaves.TryGetValue(cacheKey, out byte[] cachedWave))
        {
            bool cachedPlayed = false;
            yield return PlayWave(cachedWave, value => cachedPlayed = value);
            if (cachedPlayed) yield break;
        }

        bool aivisReady = false;
        yield return EnsureAivisReady(value => aivisReady = value);
        if (aivisReady && Enabled)
        {
            int styleId = -1;
            yield return ResolveAivisStyleId(style, value => styleId = value);
            byte[] wave = null;
            if (styleId >= 0)
                yield return SynthesizeAivis(speech, styleId, style, value => wave = value);

            if (wave != null && Enabled)
            {
                CacheWave(cacheKey, wave);
                bool played = false;
                yield return PlayWave(wave, value => played = value);
                if (played)
                {
                    Debug.Log("[Mao Voice] AivisSpeech Mao playback completed (" + style + ").");
                    yield break;
                }
            }
        }

        if (!Enabled) yield break;
        Debug.LogWarning("[Mao Voice] AivisSpeech Mao was unavailable; using Japanese Nanami fallback.");
        bool edgePlayed = false;
        yield return SpeakEdgeFallback(speech, value => edgePlayed = value);
        if (!edgePlayed && Enabled)
        {
            Debug.Log("[Mao Voice] Falling back to Microsoft Zira.");
            yield return SpeakWindowsFallback(speech);
        }
    }

    private IEnumerator EnsureAivisReady(Action<bool> onReady)
    {
        if (Time.realtimeSinceStartup - lastReadyAt < 30f)
        {
            onReady(true);
            yield break;
        }
        bool ready = false;
        yield return ProbeAivis(value => ready = value);
        if (ready)
        {
            onReady(true);
            yield break;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            bool runningOwnedEngine = ownedAivisEngine != null && !ownedAivisEngine.HasExited;
            if (!runningOwnedEngine)
            {
                ownedAivisEngine?.Dispose();
                string executable = MaoAivisLocator.GetEngineExecutablePath(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (!File.Exists(executable))
                {
                    Debug.LogWarning("[Mao Voice] AivisSpeech Engine is not installed at the verified location.");
                    onReady(false);
                    yield break;
                }

                ownedAivisEngine = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--host 127.0.0.1 --port 10101 --no-use_gpu --disable_mutable_api",
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Debug.Log("[Mao Voice] Starting the local AivisSpeech Engine.");
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Voice] Could not start AivisSpeech Engine: " + exception.Message);
            onReady(false);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + AivisStartupTimeoutSeconds;
        while (Enabled && Time.realtimeSinceStartup < deadline)
        {
            yield return ProbeAivis(value => ready = value);
            if (ready)
            {
                onReady(true);
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }
#endif
        onReady(false);
    }

    private IEnumerator ProbeAivis(Action<bool> onReady)
    {
        var request = UnityWebRequest.Get(MaoAssistantProfile.AivisApiBaseUrl + "/version");
        request.timeout = 2;
        activeWebRequest = request;
        yield return request.SendWebRequest();
        string version = request.result == UnityWebRequest.Result.Success
            ? request.downloadHandler.text.Trim().Trim('"')
            : string.Empty;
        bool ready = string.Equals(version, "1.2.0", StringComparison.Ordinal);
        lastReadyAt = ready ? Time.realtimeSinceStartup : float.NegativeInfinity;
        if (ReferenceEquals(activeWebRequest, request)) activeWebRequest = null;
        request.Dispose();
        onReady(ready);
    }

    private IEnumerator ResolveAivisStyleId(string style, Action<int> onReady)
    {
        int cachedStyleId;
        if (aivisStyleIds.TryGetValue(style, out cachedStyleId))
        {
            onReady(cachedStyleId);
            yield break;
        }

        var request = UnityWebRequest.Get(MaoAssistantProfile.AivisApiBaseUrl + "/speakers");
        request.timeout = 15;
        activeWebRequest = request;
        yield return request.SendWebRequest();

        int styleId = -1;
        try
        {
            byte[] bytes = request.downloadHandler.data;
            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(request.error);
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxMetadataBytes)
                throw new InvalidOperationException("AivisSpeech returned invalid speaker metadata.");
            styleId = MaoAivisSpeakerParser.ResolveStyleId(Encoding.UTF8.GetString(bytes), style);
            aivisStyleIds[style] = styleId;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Voice] Could not select the verified Mao voice: " + exception.Message);
        }

        if (ReferenceEquals(activeWebRequest, request)) activeWebRequest = null;
        request.Dispose();
        onReady(styleId);
    }

    private IEnumerator SynthesizeAivis(string speech, int styleId, string style, Action<byte[]> onReady)
    {
        string encodedText = UnityWebRequest.EscapeURL(speech, Encoding.UTF8);
        string queryUrl = MaoAssistantProfile.AivisApiBaseUrl + "/audio_query?text=" + encodedText + "&speaker=" + styleId;
        var queryRequest = new UnityWebRequest(queryUrl, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Array.Empty<byte>()),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 60
        };
        activeWebRequest = queryRequest;
        yield return queryRequest.SendWebRequest();

        string queryJson = null;
        try
        {
            byte[] queryBytes = queryRequest.downloadHandler.data;
            if (queryRequest.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(queryRequest.error);
            if (queryBytes == null || queryBytes.Length == 0 || queryBytes.Length > MaxMetadataBytes)
                throw new InvalidOperationException("AivisSpeech returned an invalid audio query.");

            JObject query = JObject.Parse(Encoding.UTF8.GetString(queryBytes));
            query["speedScale"] = 1.08;
            query["intonationScale"] = style == MaoAssistantProfile.AivisTeasingStyleName ? 1.25 : 1.15;
            query["prePhonemeLength"] = 0.08;
            query["postPhonemeLength"] = 0.12;
            queryJson = query.ToString(Formatting.None);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Voice] AivisSpeech audio query failed: " + exception.Message);
            lastReadyAt = float.NegativeInfinity;
        }

        if (ReferenceEquals(activeWebRequest, queryRequest)) activeWebRequest = null;
        queryRequest.Dispose();
        if (queryJson == null)
        {
            onReady(null);
            yield break;
        }

        string synthesisUrl = MaoAssistantProfile.AivisApiBaseUrl + "/synthesis?speaker=" + styleId;
        byte[] queryPayload = Encoding.UTF8.GetBytes(queryJson);
        var synthesisRequest = new UnityWebRequest(synthesisUrl, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(queryPayload),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 120
        };
        synthesisRequest.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
        activeWebRequest = synthesisRequest;
        yield return synthesisRequest.SendWebRequest();

        byte[] wave = null;
        try
        {
            byte[] bytes = synthesisRequest.downloadHandler.data;
            if (synthesisRequest.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(synthesisRequest.error);
            if (!IsValidWave(bytes))
                throw new InvalidOperationException("AivisSpeech returned invalid or oversized WAV data.");
            wave = bytes;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Voice] AivisSpeech synthesis failed: " + exception.Message);
            lastReadyAt = float.NegativeInfinity;
        }

        if (ReferenceEquals(activeWebRequest, synthesisRequest)) activeWebRequest = null;
        synthesisRequest.Dispose();
        onReady(wave);
    }

    private IEnumerator PlayWave(byte[] wave, Action<bool> onComplete)
    {
        Directory.CreateDirectory(Application.temporaryCachePath);
        activeAudioPath = Path.Combine(Application.temporaryCachePath, "mao-aivis-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            File.WriteAllBytes(activeAudioPath, wave);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Voice] Could not stage AivisSpeech audio: " + exception.Message);
            TryDelete(activeAudioPath);
            activeAudioPath = null;
            onComplete(false);
            yield break;
        }

        var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(activeAudioPath).AbsoluteUri, AudioType.WAV);
        activeWebRequest = request;
        yield return request.SendWebRequest();
        bool played = false;
        if (request.result == UnityWebRequest.Result.Success && Enabled)
        {
            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip != null)
            {
                audioSource.clip = clip;
                audioSource.Play();
                Debug.Log("[Mao Voice] event=audio_started tts_wait_ms=" +
                          ((Time.realtimeSinceStartup - speechStartedAt) * 1000f).ToString("0"));
                while (Enabled && audioSource.isPlaying) yield return null;
                audioSource.Stop();
                audioSource.clip = null;
                Destroy(clip);
                played = true;
            }
        }
        else if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning("[Mao Voice] Could not load AivisSpeech audio: " + request.error);
        }

        if (ReferenceEquals(activeWebRequest, request)) activeWebRequest = null;
        request.Dispose();
        TryDelete(activeAudioPath);
        activeAudioPath = null;
        onComplete(played);
    }

    private IEnumerator SpeakEdgeFallback(string speech, Action<bool> onComplete)
    {
        Directory.CreateDirectory(Application.temporaryCachePath);
        string stem = "mao-voice-" + Guid.NewGuid().ToString("N");
        activeTextPath = Path.Combine(Application.temporaryCachePath, stem + ".txt");
        activeAudioPath = Path.Combine(Application.temporaryCachePath, stem + ".mp3");

        bool generated = false;
        try
        {
            File.WriteAllText(activeTextPath, speech, new UTF8Encoding(false));
            activeProcess = Process.Start(new ProcessStartInfo
            {
                FileName = ResolvePython(),
                Arguments = "-m edge_tts -f \"" + activeTextPath + "\" -v " + MaoAssistantProfile.EdgeVoiceName +
                            " --rate=" + MaoAssistantProfile.EdgeVoiceRate +
                            " --pitch=" + MaoAssistantProfile.EdgeVoicePitch +
                            " --write-media \"" + activeAudioPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Voice] Japanese Nanami voice could not start: " + exception.Message);
        }

        if (activeProcess != null)
        {
            float deadline = Time.realtimeSinceStartup + EdgeSynthesisTimeoutSeconds;
            while (!activeProcess.HasExited && Time.realtimeSinceStartup < deadline) yield return null;
            if (!activeProcess.HasExited)
            {
                try { activeProcess.Kill(); } catch { }
                Debug.LogWarning("[Mao Voice] Japanese Nanami synthesis timed out.");
            }
            else
            {
                generated = activeProcess.ExitCode == 0 && File.Exists(activeAudioPath);
            }
            activeProcess.Dispose();
            activeProcess = null;
        }

        TryDelete(activeTextPath);
        activeTextPath = null;
        if (generated && Enabled)
        {
            var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(activeAudioPath).AbsoluteUri, AudioType.MPEG);
            activeWebRequest = request;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                audioSource.clip = clip;
                audioSource.Play();
                while (Enabled && audioSource.isPlaying) yield return null;
                audioSource.Stop();
                audioSource.clip = null;
                if (clip != null) Destroy(clip);
                Debug.Log("[Mao Voice] Japanese Nanami playback completed.");
            }
            else
            {
                Debug.LogWarning("[Mao Voice] Could not load synthesized audio: " + request.error);
                generated = false;
            }
            if (ReferenceEquals(activeWebRequest, request)) activeWebRequest = null;
            request.Dispose();
        }

        TryDelete(activeAudioPath);
        activeAudioPath = null;
        onComplete(generated);
    }

    public void Cancel()
    {
        if (warmupRoutine != null)
        {
            StopCoroutine(warmupRoutine);
            warmupRoutine = null;
        }
        if (activeWebRequest != null)
        {
            try { activeWebRequest.Abort(); } catch { }
            activeWebRequest.Dispose();
            activeWebRequest = null;
        }
        if (audioSource != null)
        {
            audioSource.Stop();
            if (audioSource.clip != null) Destroy(audioSource.clip);
            audioSource.clip = null;
        }
        if (activeProcess != null)
        {
            try { if (!activeProcess.HasExited) activeProcess.Kill(); } catch { }
            activeProcess.Dispose();
            activeProcess = null;
        }
        TryDelete(activeTextPath);
        TryDelete(activeAudioPath);
        activeTextPath = null;
        activeAudioPath = null;
    }

    private IEnumerator SpeakWindowsFallback(string speech)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        Process fallback = null;
        try
        {
            string scriptPath = Path.Combine(Application.streamingAssetsPath, "MaoVoiceFallback.ps1");
            string powerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            fallback = Process.Start(new ProcessStartInfo
            {
                FileName = powerShell,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            activeProcess = fallback;
            fallback.StandardInput.Write(speech);
            fallback.StandardInput.Close();
            while (!fallback.HasExited && Enabled) yield return null;
        }
        finally
        {
            if (fallback != null)
            {
                try { if (!fallback.HasExited) fallback.Kill(); } catch { }
                fallback.Dispose();
            }
            if (ReferenceEquals(activeProcess, fallback)) activeProcess = null;
        }
#else
        yield break;
#endif
    }

    private static bool IsValidWave(byte[] bytes)
    {
        return bytes != null && bytes.Length > 44 && bytes.Length <= MaxWaveBytes &&
               bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
               bytes[2] == (byte)'F' && bytes[3] == (byte)'F';
    }

    private void CacheWave(string key, byte[] wave)
    {
        const int budget = 8 * 1024 * 1024;
        if (wave.Length > budget || recentWaves.ContainsKey(key)) return;
        while (waveOrder.Count > 0 && (waveOrder.Count >= 12 || cachedWaveBytes + wave.Length > budget))
        {
            string oldest = waveOrder.Dequeue();
            cachedWaveBytes -= recentWaves[oldest].Length;
            recentWaves.Remove(oldest);
        }
        recentWaves.Add(key, wave);
        waveOrder.Enqueue(key);
        cachedWaveBytes += wave.Length;
    }

    private static string ResolvePython()
    {
        string configured = Environment.GetEnvironmentVariable("MAO_PYTHON");
        return string.IsNullOrWhiteSpace(configured) ? "python.exe" : configured;
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private void OnDestroy()
    {
        Cancel();
        if (ownedAivisEngine != null)
        {
            try { if (!ownedAivisEngine.HasExited) ownedAivisEngine.Kill(); } catch { }
            ownedAivisEngine.Dispose();
            ownedAivisEngine = null;
        }
    }
}
