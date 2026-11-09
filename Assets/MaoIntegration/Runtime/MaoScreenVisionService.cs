using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using MaoIntegration;
using UnityEngine;

[Serializable]
public sealed class MaoScreenWorldState
{
    public string ActiveApp = string.Empty;
    public string Activity = string.Empty;
    public string Event = string.Empty;
    public float Importance;
    public string LastObservedUtc = string.Empty;
}

public sealed class MaoScreenVisionService : MonoBehaviour
{
    private const string EnabledPreference = "Mao.ScreenVision.Enabled";
    private const int MaxWidth = 1280;
    private const int MaxHeight = 720;
    private const int MaxJpegBytes = 3 * 1024 * 1024;
    private const int ComparisonWidth = 160;
    private const int ComparisonHeight = 90;
    private const int StretchHalftone = 4;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayeredWindows = 0x40000000;

    [Header("Local Screen Change Detection")]
    [SerializeField, Range(0.01f, 0.50f)] private float screenChangeThreshold = 0.08f;
    [SerializeField, Range(0.001f, 0.10f)] private float screenStabilityThreshold = 0.04f;
    [SerializeField, Min(0.5f)] private float screenStableSeconds = 3f;
    [SerializeField, Min(5f)] private float minimumObservationInterval = 5f;
    [SerializeField, Min(30f)] private float visionCooldownSeconds = 60f;
    [SerializeField, Min(1f)] private float maxVisionSilenceMinutes = 15f;
    [SerializeField] private bool debugChangeDetection;

    private MaoScreenChangeDetector changeDetector;
    private Bitmap comparisonBitmap;
    private System.Drawing.Graphics comparisonGraphics;
    private byte[] comparisonPixels;
    private byte[] grayscaleSample;
    private float nextLocalObservationAt;
    private MaoScreenChangeResult latestChange;
    private MaoScreenChangeReason lastLoggedReason = (MaoScreenChangeReason)(-1);

    public bool AutomaticObservationReady => latestChange.ShouldRequestVision;
    public MaoScreenChangeResult LatestChange => latestChange;
    public MaoScreenWorldState CurrentWorldState { get; } = new MaoScreenWorldState();
    public int LocalCheckCount { get; private set; }
    public int VisionRequestCount { get; private set; }
    public int SkippedAutomaticRequestCount { get; private set; }
    public bool LastRequestWasMeaningfulChange { get; private set; }

    public bool Enabled
    {
        get => PlayerPrefs.GetInt(EnabledPreference, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(EnabledPreference, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    private void Awake()
    {
        changeDetector = new MaoScreenChangeDetector(
            screenChangeThreshold,
            screenStabilityThreshold,
            screenStableSeconds,
            visionCooldownSeconds,
            maxVisionSilenceMinutes * 60f);
        nextLocalObservationAt = Time.unscaledTime;
        Debug.Log("[Mao Vision] event=local_detector_started sample=160x90" +
                  " threshold=" + screenChangeThreshold.ToString("0.000") +
                  " stability_threshold=" + screenStabilityThreshold.ToString("0.000") +
                  " stable_seconds=" + screenStableSeconds.ToString("0.0") +
                  " check_interval_seconds=" + minimumObservationInterval.ToString("0.0") +
                  " vision_cooldown_seconds=" + visionCooldownSeconds.ToString("0") +
                  " max_silence_seconds=" + (maxVisionSilenceMinutes * 60f).ToString("0"));
    }

    private void Update()
    {
        if (!Enabled || Time.unscaledTime < nextLocalObservationAt) return;
        nextLocalObservationAt = Time.unscaledTime + Math.Max(5f, minimumObservationInterval);
        if (!TryCaptureComparisonSample(out string error))
        {
            if (debugChangeDetection) Debug.LogWarning("[Mao Vision] event=local_capture_failed reason=" + error);
            return;
        }

        LocalCheckCount++;
        latestChange = changeDetector.Observe(grayscaleSample, Time.unscaledTime);
        LogChangeDecision(latestChange);
    }

    public bool TryCaptureJpeg(out byte[] jpeg, out string error)
    {
        jpeg = null;
        error = string.Empty;
        if (!Enabled)
        {
            error = "Screen Vision is off.";
            return false;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            int sourceWidth = GetSystemMetrics(0);
            int sourceHeight = GetSystemMetrics(1);
            if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidOperationException("Windows did not report a valid display size.");

            float scale = Math.Min(1f, Math.Min((float)MaxWidth / sourceWidth, (float)MaxHeight / sourceHeight));
            int targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            using (var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb))
            using (var stream = new MemoryStream())
            {
                CopyDesktopToBitmap(resized, sourceWidth, sourceHeight);
                resized.Save(stream, ImageFormat.Jpeg);
                jpeg = stream.ToArray();
            }

            if (jpeg.Length == 0 || jpeg.Length > MaxJpegBytes)
                throw new InvalidOperationException("The screen capture exceeded the safe in-memory size limit.");

            Debug.Log($"[Mao Vision] Captured current display in memory ({jpeg.Length} bytes); no screenshot was saved.");
            return true;
        }
        catch (Exception exception)
        {
            jpeg = null;
            error = "Mao could not capture the current display.";
            Debug.LogWarning("[Mao Vision] " + exception.Message);
            return false;
        }
#else
        error = "Screen Vision is only available in the Windows build.";
        return false;
#endif
    }

    public bool TryCaptureForVision(bool requireMeaningfulChange, out byte[] jpeg, out string error)
    {
        jpeg = null;
        error = string.Empty;
        if (!TryCaptureComparisonSample(out error)) return false;

        latestChange = changeDetector.Observe(grayscaleSample, Time.unscaledTime);
        LogChangeDecision(latestChange);
        if (requireMeaningfulChange && !latestChange.ShouldRequestVision)
        {
            SkippedAutomaticRequestCount++;
            error = "Local screen change gate: " + latestChange.Reason;
            if (debugChangeDetection)
                Debug.Log("[Mao Vision] event=vision_skipped reason=" + latestChange.Reason +
                          " difference=" + latestChange.Difference.ToString("0.000") +
                          " cooldown_remaining_seconds=" + latestChange.CooldownRemainingSeconds.ToString("0.0"));
            return false;
        }

        if (!TryCaptureJpeg(out jpeg, out error)) return false;
        LastRequestWasMeaningfulChange = latestChange.Reason == MaoScreenChangeReason.MeaningfulChange;
        changeDetector.MarkVisionRequested(Time.unscaledTime);
        VisionRequestCount++;
        Debug.Log("[Mao Vision] event=vision_capture_ready trigger=" +
                  (requireMeaningfulChange ? latestChange.Reason.ToString() : "manual") +
                  " difference=" + latestChange.Difference.ToString("0.000") +
                  " jpeg_bytes=" + jpeg.Length);
        return true;
    }

    public void MarkVisionAccepted(string activeApp, string activity, string observation)
    {
        changeDetector.MarkVisionAccepted(Time.unscaledTime);
        CurrentWorldState.ActiveApp = Normalize(activeApp, 64);
        CurrentWorldState.Activity = Normalize(activity, 48);
        CurrentWorldState.Event = Normalize(
            string.IsNullOrWhiteSpace(observation) ? "screen_observed" : observation,
            220);
        CurrentWorldState.Importance = Math.Max(0f, Math.Min(1f, latestChange.Difference));
        CurrentWorldState.LastObservedUtc = DateTime.UtcNow.ToString("o");
        latestChange = changeDetector.Observe(grayscaleSample, Time.unscaledTime);
        Debug.Log("[Mao Vision] event=world_state_updated app=" +
                  (CurrentWorldState.ActiveApp.Length > 0 ? CurrentWorldState.ActiveApp : "unknown") +
                  " activity=" +
                  (CurrentWorldState.Activity.Length > 0 ? CurrentWorldState.Activity : "unknown") +
                  " importance=" + CurrentWorldState.Importance.ToString("0.000"));
    }

    public void MarkVisionFailed()
    {
        changeDetector.MarkVisionFailed();
    }

    private bool TryCaptureComparisonSample(out string error)
    {
        error = string.Empty;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            int sourceWidth = GetSystemMetrics(0);
            int sourceHeight = GetSystemMetrics(1);
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new InvalidOperationException("Windows did not report a valid display size.");
            EnsureComparisonResources();
            CopyDesktopToBitmap(comparisonBitmap, sourceWidth, sourceHeight, comparisonGraphics);
            CopyGrayscale(comparisonBitmap, comparisonPixels, grayscaleSample);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name;
            return false;
        }
#else
        error = "unsupported_platform";
        return false;
#endif
    }

    private void EnsureComparisonResources()
    {
        if (comparisonBitmap != null) return;
        comparisonBitmap = new Bitmap(ComparisonWidth, ComparisonHeight, PixelFormat.Format24bppRgb);
        comparisonGraphics = System.Drawing.Graphics.FromImage(comparisonBitmap);
        int comparisonStride = ((ComparisonWidth * 3) + 3) & ~3;
        comparisonPixels = new byte[comparisonStride * ComparisonHeight];
        grayscaleSample = new byte[ComparisonWidth * ComparisonHeight];
    }

    private static void CopyGrayscale(Bitmap bitmap, byte[] pixelBuffer, byte[] grayscale)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int byteCount = stride * bitmap.Height;
            if (pixelBuffer == null || pixelBuffer.Length != byteCount)
                throw new InvalidOperationException("The comparison pixel buffer has an invalid size.");
            Marshal.Copy(data.Scan0, pixelBuffer, 0, byteCount);
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = data.Stride >= 0 ? y * stride : (bitmap.Height - 1 - y) * stride;
                int target = y * bitmap.Width;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int source = row + x * 3;
                    grayscale[target + x] = (byte)((pixelBuffer[source + 2] * 77 +
                                                    pixelBuffer[source + 1] * 150 +
                                                    pixelBuffer[source] * 29) >> 8);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void CopyDesktopToBitmap(
        Bitmap target,
        int sourceWidth,
        int sourceHeight,
        System.Drawing.Graphics existingGraphics = null)
    {
        System.Drawing.Graphics targetGraphics = existingGraphics ?? System.Drawing.Graphics.FromImage(target);
        IntPtr sourceDc = IntPtr.Zero;
        IntPtr targetDc = IntPtr.Zero;
        try
        {
            sourceDc = GetDC(IntPtr.Zero);
            if (sourceDc == IntPtr.Zero) throw new InvalidOperationException("Could not acquire the desktop device context.");
            targetDc = targetGraphics.GetHdc();
            SetStretchBltMode(targetDc, StretchHalftone);
            if (!StretchBlt(
                    targetDc, 0, 0, target.Width, target.Height,
                    sourceDc, 0, 0, sourceWidth, sourceHeight,
                    SourceCopy | CaptureLayeredWindows))
                throw new InvalidOperationException("Windows could not copy the desktop image.");
        }
        finally
        {
            if (targetDc != IntPtr.Zero) targetGraphics.ReleaseHdc(targetDc);
            if (sourceDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, sourceDc);
            if (existingGraphics == null) targetGraphics.Dispose();
        }
    }

    private void LogChangeDecision(MaoScreenChangeResult result)
    {
        if (!debugChangeDetection || result.Reason == lastLoggedReason) return;
        lastLoggedReason = result.Reason;
        Debug.Log("[Mao Vision] event=local_change decision=" + result.Reason +
                  " difference=" + result.Difference.ToString("0.000") +
                  " motion_difference=" + result.MotionDifference.ToString("0.000") +
                  " ready=" + result.ShouldRequestVision.ToString().ToLowerInvariant() +
                  " cooldown_remaining_seconds=" + result.CooldownRemainingSeconds.ToString("0.0"));
    }

    private static string Normalize(string value, int maximumLength)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized.Substring(0, maximumLength).TrimEnd();
    }

    private void OnDestroy()
    {
        comparisonGraphics?.Dispose();
        comparisonGraphics = null;
        comparisonBitmap?.Dispose();
        comparisonBitmap = null;
        comparisonPixels = null;
        grayscaleSample = null;
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr source,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint rasterOperation);
#endif
}
