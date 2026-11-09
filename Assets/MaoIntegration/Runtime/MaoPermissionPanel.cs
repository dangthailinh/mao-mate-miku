using System;
using System.Linq;
using MaoIntegration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class MaoPermissionPanel : MonoBehaviour
{
    private MaoHarnessRuntime runtime;
    private GameObject canvasObject;
    private Text status, preview, message;
    private Button allow, deny;
    private readonly Button[] modes = new Button[3];
    private GameObject previousFocus;
    private string displayedCall;
    private float refreshAt;
    public void Show(MaoHarnessRuntime owner)
    {
        runtime = owner;
        if (canvasObject == null) Create();
        previousFocus = EventSystem.current?.currentSelectedGameObject;
        canvasObject.SetActive(true); Refresh();
        EventSystem.current?.SetSelectedGameObject(runtime.Permissions.Pending == null ? modes[(int)runtime.Permissions.Mode].gameObject : allow.gameObject);
    }
    public void Hide()
    {
        if (canvasObject != null) canvasObject.SetActive(false);
        if (previousFocus != null && previousFocus.activeInHierarchy) EventSystem.current?.SetSelectedGameObject(previousFocus);
    }
    private void Update()
    {
        if (canvasObject == null || !canvasObject.activeSelf) return;
        if (Input.GetKeyDown(KeyCode.Escape)) Hide();
        if (Time.unscaledTime >= refreshAt) { refreshAt = Time.unscaledTime + 0.2f; Refresh(); }
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            var buttons = canvasObject.GetComponentsInChildren<Button>().Where(b => b.interactable).ToArray();
            int index = Array.FindIndex(buttons, b => b.gameObject == EventSystem.current?.currentSelectedGameObject);
            if (buttons.Length > 0) EventSystem.current?.SetSelectedGameObject(buttons[(index + (Input.GetKey(KeyCode.LeftShift) ? buttons.Length - 1 : 1) + buttons.Length) % buttons.Length].gameObject);
        }
    }
    private void Create()
    {
        canvasObject = new GameObject("Miku Permissions", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 3000;
        var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(900, 760); scaler.matchWidthOrHeight = 0.5f;
        var panel = Box(canvasObject.transform, "Permission panel", new Color32(20, 42, 46, 255));
        var rect = panel.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f); rect.pivot = new Vector2(0.5f, 0.5f); rect.sizeDelta = new Vector2(740, 660);
        Label(panel.transform, "QUYỀN CỦA MIKU", 24, 24, 570, 42, 26);
        ActionButton(panel.transform, "Đóng", 620, 20, 96, 44, Hide);
        status = Label(panel.transform, "", 24, 76, 692, 72, 17);
        string[] labels = { "Chỉ đọc", "Hỏi trước", "Full access" };
        for (int i = 0; i < modes.Length; i++) { int mode = i; modes[i] = ActionButton(panel.transform, labels[i], 24 + i * 236, 156, 220, 48, () => { runtime.SetPermissionMode((MaoPermissionMode)mode); message.text = "Đã đổi quyền và dừng tác vụ cũ."; Refresh(); }); }
        Label(panel.transform, "Full access có thể sửa/xóa file và chạy chương trình theo yêu cầu. Dùng quyền tài khoản Windows hiện tại; không tự nâng Administrator. Chỉ cấp cho tác vụ và dữ liệu anh tin cậy.", 24, 220, 692, 90, 17);
        Label(panel.transform, "BƯỚC ĐANG CHỜ DUYỆT", 24, 318, 692, 30, 18);
        var viewport = Box(panel.transform, "Tool preview", new Color32(11, 28, 32, 255)); Place(viewport.GetComponent<RectTransform>(), 24, 358, 692, 146); viewport.AddComponent<RectMask2D>();
        var scroll = viewport.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 24;
        preview = Label(viewport.transform, "", 12, 10, 664, 130, 17);
        var fit = preview.gameObject.AddComponent<ContentSizeFitter>(); fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport.GetComponent<RectTransform>(); scroll.content = preview.rectTransform;
        allow = ActionButton(panel.transform, "Cho phép một lần", 24, 520, 246, 48, () => { runtime.DecidePermission(true, displayedCall); Refresh(); });
        deny = ActionButton(panel.transform, "Từ chối", 286, 520, 198, 48, () => { runtime.DecidePermission(false, displayedCall); Refresh(); });
        ActionButton(panel.transform, "Dừng tác vụ", 500, 520, 216, 48, () => { runtime.ControlLayer.CancelActive(); runtime.Cancel(); message.text = "Đã dừng; thao tác đã hoàn thành không tự hoàn tác."; Refresh(); });
        message = Label(panel.transform, "Có thể dùng /permissions hoặc /workspace <thư mục>. Esc để đóng.", 24, 590, 692, 48, 16);
    }
    private void Refresh()
    {
        if (runtime == null) return;
        status.text = "Đang chọn: " + runtime.Permissions.WireMode + "\nWorkspace: " + runtime.Workspace;
        for (int i = 0; i < modes.Length; i++) modes[i].GetComponentInChildren<Text>().text = ((int)runtime.Permissions.Mode == i ? "✓ " : "") + new[] { "Chỉ đọc", "Hỏi trước", "Full access" }[i];
        var pending = runtime.Permissions.Pending; displayedCall = pending?.CallId;
        bool waiting = pending != null && !runtime.Permissions.Rejected && !runtime.Permissions.IsApproved(pending);
        allow.interactable = deny.interactable = waiting;
        if (pending == null) preview.text = "Chưa có bước chờ duyệt.\nChỉ đọc: xem dữ liệu. Hỏi trước: duyệt từng thao tác ghi/mở/chạy. Full access: chạy trực tiếp trong tác vụ anh giao.";
        else preview.text = pending.Tool + "\n" + string.Join("\n", pending.Arguments.Where(p => !string.IsNullOrEmpty(p.Value) && p.Key != "content" && p.Key != "target").Select(p => p.Key + ": " + MaoControlRedaction.ApprovalPreview(p.Value))) +
            (pending.Tool == "open_target" ? "\nTarget: " + pending.Argument("target") : "") + (pending.Tool == "write_file" || pending.Tool == "save_note" ? "\nNội dung:\n" + MaoControlRedaction.ApprovalPreview(pending.Argument("content")) : "") + (waiting ? "" : "\nĐã quyết định; đang xử lý.");
    }
    private static GameObject Box(Transform parent, string name, Color color)
    { var box = new GameObject(name, typeof(RectTransform), typeof(Image)); box.transform.SetParent(parent, false); box.GetComponent<Image>().color = color; return box; }
    private static void Place(RectTransform rect, float x, float y, float w, float h)
    { rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(w, h); }
    private static Text Label(Transform parent, string content, float x, float y, float w, float h, int size)
    {
        var obj = new GameObject("Label", typeof(RectTransform), typeof(Text)); obj.transform.SetParent(parent, false); Place(obj.GetComponent<RectTransform>(), x, y, w, h);
        var text = obj.GetComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.fontSize = size; text.color = new Color32(234, 248, 247, 255); text.supportRichText = false; text.raycastTarget = false; text.text = content; return text;
    }
    private static Button ActionButton(Transform parent, string label, float x, float y, float w, float h, UnityEngine.Events.UnityAction action)
    {
        var obj = Box(parent, label, new Color32(37, 84, 89, 255)); Place(obj.GetComponent<RectTransform>(), x, y, w, h);
        var button = obj.AddComponent<Button>(); button.onClick.AddListener(action); var colors = button.colors; colors.highlightedColor = colors.selectedColor = new Color32(74, 192, 185, 255); button.colors = colors;
        var text = Label(obj.transform, label, 4, 4, w - 8, h - 8, 18); text.alignment = TextAnchor.MiddleCenter; return button;
    }
    private void OnDestroy() { if (canvasObject != null) Destroy(canvasObject); }
}
