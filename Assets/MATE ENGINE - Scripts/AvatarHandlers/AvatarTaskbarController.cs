using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MaoIntegration;
using UnityEngine;

[ExecuteAlways]
public class AvatarTaskbarController : MonoBehaviour
{
    public bool ExternalSitRequested { get; set; }
    public bool ExternalWindowSitRequested { get; set; }
    public bool ExternalGroundMovementRequested { get; set; }
    public bool ExternalMacaronFlightRequested { get; set; }

    [Header("Animator")]
    public Animator avatarAnimator;

    [Header("Detection Settings")]
    public Vector2 snapZoneOffset = new Vector2(0, -5);
    public Vector2 snapZoneSize = new Vector2(100, 10);

    [Header("Attach Settings")]
    public GameObject attachTarget;
    public HumanBodyBones attachBone = HumanBodyBones.Head;
    public bool keepOriginalRotation = false;

    [Header("Spawn / Despawn Animation")]
    public float spawnScaleTime = 0.2f;
    public float despawnScaleTime = 0.2f;

    [Header("Debug")]
    public bool showDebugGizmo = true;
    public Color taskbarGizmoColor = Color.green;
    public Color pinkZoneGizmoColor = Color.magenta;

    private IntPtr unityHWND;
    private Vector2 unityPos;
    private Rect taskbarRect;
    private Rect pinkZoneDesktopRect;

    private Animator animator;
    private Transform attachBoneTransform;
    private Transform originalAttachParent;

    private Vector3 originalScale = Vector3.one;
    private float scaleLerpT = 0f;
    private bool isScaling = false;
    private bool scalingUp = false;

    private bool wasAllowSpawn = false;
    private int manualSeat;
    private GameObject loungeChair;
    private bool chairLoading;
    private bool suppressAutomaticSeat;
    private Vector3 chairSeatAnchor;
    private MaoAnimationCoordinator animationCoordinator;
    public int ManualSeat => manualSeat;

    // 0 = automatic, 1 = ordinary sitting, 2 = original Macaron pet, 3 = lounge chair.
    public void SelectSeat(int choice)
    {
        if (choice < 0 || choice > 3 || animator == null) return;
        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Seating, choice != 0))
        {
            UnityEngine.Debug.Log("[Mao Seating] event=blocked_by_owner choice=" + choice + " owner=" + animationCoordinator.CurrentOwner);
            return;
        }
        GetComponentInChildren<AvatarPoseController>()?.StopPose();
        var locomotion = animator.GetComponent<AvatarLocomotionController>();
        if (locomotion != null) locomotion.Stop();
        manualSeat = choice;
        suppressAutomaticSeat = choice == 0;
        if (loungeChair != null) loungeChair.SetActive(choice == 3);
        bool ordinarySeat = choice == 1 || choice == 3;
        animator.SetBool("isWindowSit", ordinarySeat);
        animator.SetBool("isTaskbarSit", false);
        animator.SetFloat("WindowSitIndex", 0f);
        animator.SetBool(IsSitting, choice == 2);
        if (choice != 0)
            animator.CrossFadeInFixedTime(ordinarySeat ? "Base Layer.WindowSit" : "Base Layer.Sitting", 0.2f, 0);
        if (choice == 3 && loungeChair == null && !chairLoading) LoadLoungeChair();
        // The Sitting animation already reveals the avatar's built-in Macaron.
        // Do not spawn a second copy merely because attachTarget is unassigned.
        UnityEngine.Debug.Log("[Mao Seating] selected=" + choice);
    }

    private async void LoadLoungeChair()
    {
        chairLoading = true;
        try
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "MaoSeating", "lounge-chair.glb");
            using (var data = new UniGLTF.GlbFileParser(path).Parse())
            using (var importer = new UniGLTF.ImporterContext(data))
            {
                var loaded = await importer.LoadAsync(new UniGLTF.ImmediateCaller());
                if (this == null) { Destroy(loaded.gameObject); return; }
                loungeChair = loaded.gameObject;
                loungeChair.name = "Mao Lounge Chair";
                loaded.ShowMeshes();
                loungeChair.transform.SetParent(animator.transform, false);
                loungeChair.transform.localRotation = Quaternion.identity;
                // Chair export preserves source metres; seat anchor is in avatar-local space.
                loungeChair.transform.localScale = Vector3.one;
                chairSeatAnchor = new Vector3(0f, 0.40f, 0f);
                loungeChair.SetActive(manualSeat == 3);
                UnityEngine.Debug.Log("[Mao Seating] chair_loaded=true");
            }
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogWarning("[Mao Seating] chair_load_failed=" + exception.GetType().Name);
            if (manualSeat == 3) SelectSeat(0);
        }
        finally { chairLoading = false; }
    }

    private void LateUpdate()
    {
        if (manualSeat != 3 || loungeChair == null || animator == null) return;
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null) return;
        loungeChair.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        loungeChair.transform.position = hips.position - animator.transform.TransformVector(chairSeatAnchor);
    }


    private static readonly int IsSitting = Animator.StringToHash("isSitting");


    void Start()
    {
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;
        animator = avatarAnimator ?? GetComponent<Animator>();
        animationCoordinator = animator != null ? animator.GetComponentInParent<MaoAnimationCoordinator>() : null;

        if (attachTarget != null)
        {
            originalScale = attachTarget.transform.localScale;
            originalAttachParent = attachTarget.transform.parent;
            attachTarget.SetActive(false);
        }

        UpdateTaskbarRect();
        if (Application.isPlaying && Array.IndexOf(Environment.GetCommandLineArgs(), "--mao-smoke-seating") >= 0)
            StartCoroutine(SeatingSmoke());
    }

    private System.Collections.IEnumerator SeatingSmoke()
    {
        yield return new WaitForSecondsRealtime(8f);
        SelectSeat(3);
        float deadline = Time.realtimeSinceStartup + 30f;
        while (chairLoading && Time.realtimeSinceStartup < deadline) yield return null;
        yield return new WaitForSecondsRealtime(3f);
        CaptureSeatPreview("MaoSeating-Chair.png");
        UnityEngine.Debug.Log("[Mao Seating Smoke] chair=" + (loungeChair != null && loungeChair.activeInHierarchy) +
                              " sitting=" + animator.GetCurrentAnimatorStateInfo(0).IsName("WindowSit"));
        yield return new WaitForSecondsRealtime(2f);
        SelectSeat(2);
        yield return new WaitForSecondsRealtime(3f);
        CaptureSeatPreview("MaoSeating-Pet.png");
        UnityEngine.Debug.Log("[Mao Seating Smoke] pet_state=" + animator.GetCurrentAnimatorStateInfo(0).IsName("Sitting"));
        yield return new WaitForSecondsRealtime(2f);
        SelectSeat(1);
        yield return new WaitForSecondsRealtime(1f);
        UnityEngine.Debug.Log("[Mao Seating Smoke] plain=" + (animator.GetBool("isWindowSit") && loungeChair != null && !loungeChair.activeSelf));
        SelectSeat(0);
        yield return new WaitForSecondsRealtime(1f);
        UnityEngine.Debug.Log("[Mao Seating Smoke] stopped=" + (!animator.GetBool("isWindowSit") && !animator.GetBool(IsSitting) && !loungeChair.activeSelf));
    }

    private void CaptureSeatPreview(string filename)
    {
        var cameraObject = new GameObject("Seating verification camera");
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(800, 800, 24);
        var previous = RenderTexture.active;
        var texture = new Texture2D(800, 800, TextureFormat.RGB24, false);
        try
        {
            Vector3 hips = animator.GetBoneTransform(HumanBodyBones.Hips).position;
            camera.transform.position = hips + new Vector3(1.4f, 0.65f, -2.8f) * Mathf.Abs(animator.transform.lossyScale.y);
            camera.transform.LookAt(hips);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.gray;
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 800, 800), 0, 0);
            texture.Apply();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Application.persistentDataPath, filename), texture.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            Destroy(texture); target.Release(); Destroy(target); Destroy(cameraObject);
        }
    }

    public void SetAnimator(Animator newAnimator)
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Seating, false);
        avatarAnimator = newAnimator;
        animator = newAnimator;
        animationCoordinator = animator != null ? animator.GetComponentInParent<MaoAnimationCoordinator>() : null;
        attachBoneTransform = null;
    }

    private void OnDisable()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Seating, false);
    }

    void Update()
    {
        if (unityHWND == IntPtr.Zero || animator == null) return;
        if (animationCoordinator == null)
            animationCoordinator = animator.GetComponentInParent<MaoAnimationCoordinator>();
        if (manualSeat != 0 && animator.GetBool("isDragging")) SelectSeat(0);
        if (animator.GetBool("isDragging")) suppressAutomaticSeat = false;

        UpdateUnityWindowPosition();
        UpdateTaskbarRect();
        UpdatePinkZone();

        Rect topBar = new Rect(taskbarRect.x, taskbarRect.y, taskbarRect.width, 5);
        bool isNearTaskbar = MaoIntegration.MaoAdvancedLocomotionLogic.ShouldUseTaskbarSitting(
            pinkZoneDesktopRect.Overlaps(topBar),
            ExternalSitRequested,
            ExternalWindowSitRequested,
            ExternalGroundMovementRequested,
            ExternalMacaronFlightRequested);
        if (manualSeat != 0) isNearTaskbar = manualSeat == 2;
        else if (suppressAutomaticSeat) isNearTaskbar = false;

        if (manualSeat == 0 && animationCoordinator != null &&
            !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Seating, isNearTaskbar))
            isNearTaskbar = false;

        animator.SetBool(IsSitting, isNearTaskbar);

        bool allowSpawn = isNearTaskbar && animator
            .GetCurrentAnimatorStateInfo(0)
            .IsName("Sitting");

        if (attachBoneTransform == null && attachTarget != null)
            attachBoneTransform = animator.GetBoneTransform(attachBone);

        if (attachTarget != null)
        {
            if (allowSpawn && !keepOriginalRotation && attachBoneTransform != null)
                attachTarget.transform.SetParent(attachBoneTransform, false);
            else if (!allowSpawn && !keepOriginalRotation &&
                     attachTarget.transform.parent != originalAttachParent)
                attachTarget.transform.SetParent(originalAttachParent, false);
        }

        if (attachTarget != null && allowSpawn && !wasAllowSpawn)
        {
            attachTarget.SetActive(true);
            attachTarget.transform.localScale = Vector3.zero;
            scaleLerpT = 0f;
            scalingUp = true;
            isScaling = true;
        }

        if (attachTarget != null && !allowSpawn && attachTarget.activeSelf && (!isScaling || scalingUp))
        {
            scalingUp = false;
            isScaling = true;
            scaleLerpT = 0f;
        }

        if (attachTarget != null && isScaling && attachTarget.activeSelf)
        {
            float duration = scalingUp ? spawnScaleTime : despawnScaleTime;
            scaleLerpT += Time.deltaTime / Mathf.Max(duration, 0.0001f);
            float t = Mathf.Clamp01(scaleLerpT);
            Vector3 from = scalingUp ? Vector3.zero : originalScale;
            Vector3 to = scalingUp ? originalScale : Vector3.zero;
            attachTarget.transform.localScale = Vector3.Lerp(from, to, t);

            if (t >= 1f)
            {
                isScaling = false;
                if (!scalingUp)
                {
                    attachTarget.SetActive(false);
                    attachTarget.transform.localScale = originalScale;
                }
            }
        }

        if (attachTarget != null && attachTarget.activeSelf && keepOriginalRotation && attachBoneTransform != null)
            attachTarget.transform.position = attachBoneTransform.position;

        wasAllowSpawn = allowSpawn;
    }

    void UpdatePinkZone()
    {
        GetWindowRect(unityHWND, out RECT rect);
        int unityWidth = rect.Right - rect.Left;
        int unityHeight = rect.Bottom - rect.Top;

        float centerX = unityPos.x + unityWidth / 2f + snapZoneOffset.x;
        float bottomY = unityPos.y + unityHeight + snapZoneOffset.y;


        pinkZoneDesktopRect = new Rect(centerX - snapZoneSize.x / 2f, bottomY, snapZoneSize.x, snapZoneSize.y);
    }

    void UpdateUnityWindowPosition()
    {
        GetWindowRect(unityHWND, out RECT rect);
        unityPos = new Vector2(rect.Left, rect.Top);
    }

    void UpdateTaskbarRect()
    {
        taskbarRect = MonitorHelper.GetTaskbarRectForWindow(unityHWND);
    }



    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !showDebugGizmo) return;
        float basePixel = 1000f;
        Rect bar = new Rect(taskbarRect.x, taskbarRect.y, taskbarRect.width, 5);
        Gizmos.color = taskbarGizmoColor;
        DrawDesktopRect(bar, basePixel);
        Gizmos.color = pinkZoneGizmoColor;
        DrawDesktopRect(pinkZoneDesktopRect, basePixel);
    }

    void DrawDesktopRect(Rect desktopRect, float basePixel)
    {
        float cx = desktopRect.x + desktopRect.width / 2f;
        float cy = desktopRect.y + desktopRect.height / 2f;
        int screenWidth = Display.main.systemWidth;
        int screenHeight = Display.main.systemHeight;

        float unityX = (cx - screenWidth / 2f) / basePixel;
        float unityY = -(cy - screenHeight / 2f) / basePixel;

        Vector3 worldPos = new Vector3(unityX, unityY, 0);
        Vector3 worldSize = new Vector3(desktopRect.width / basePixel, desktopRect.height / basePixel, 0);

        Gizmos.DrawWireCube(worldPos, worldSize);
    }

    #region WinAPI
    private const int ABM_GETTASKBARPOS = 0x00000005;

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern UInt32 SHAppBarMessage(UInt32 dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    #endregion
}
