using System;
using System.Collections.Generic;
using MaoIntegration;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public sealed class MaoAppearanceRadialMenu : MonoBehaviour
{
    private sealed class Choice
    {
        public Xamin.Button Button;
        public TextMeshProUGUI Label;
        public string Text;
        public MaoAppearancePart Part;
        public int Id;
        public Xamin.Button VisibleButton;
        public TextMeshProUGUI VisibleLabel;
    }

    private Func<string, string, UnityAction, GameObject> create;
    private Action<List<GameObject>> show;
    private Action back;
    private readonly List<GameObject> root = new List<GameObject>();
    private readonly List<GameObject> hair = new List<GameObject>();
    private readonly List<GameObject> outfits = new List<GameObject>();
    private readonly List<GameObject> face = new List<GameObject>();
    private readonly List<GameObject> accessories = new List<GameObject>();
    private readonly List<Choice> choices = new List<Choice>();
    private Xamin.CircleSelector radialMenu;
    private GameObject entry;
    private float nextRefresh;

    public GameObject Initialize(Func<string, string, UnityAction, GameObject> factory,
        Action<List<GameObject>> display, Action returnToMain)
    {
        if (entry != null) return entry;
        create = factory; show = display; back = returnToMain;
        var menuActions = GetComponent<MenuActions>();
        radialMenu = menuActions != null && menuActions.radialMenuObject != null
            ? menuActions.radialMenuObject.GetComponent<Xamin.CircleSelector>()
            : GetComponent<Xamin.CircleSelector>();
        entry = create("appearance-entry", "NGOẠI HÌNH", () => Show(root));
        root.Add(create("appearance-hair", "TÓC", () => Show(hair)));
        root.Add(create("appearance-outfit", "TRANG PHỤC", () => Show(outfits)));
        root.Add(create("appearance-face", "KHUÔN MẶT", () => Show(face)));
        root.Add(create("appearance-accessories", "PHỤ KIỆN", () => Show(accessories)));
        root.Add(create("appearance-reset", "KHÔI PHỤC", () => { MaoAppearanceController.Active?.ResetAppearance(); Show(root); }));
        root.Add(create("appearance-import", "MODEL VRM", () =>
        {
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader != null) loader.OpenFileDialogAndLoadVRM();
        }));
        root.Add(create("appearance-back", "BACK", () => back()));
        BuildChoices();
        Refresh();
        return entry;
    }

    private void Show(List<GameObject> page) { Refresh(); show(page); }
    private void Update()
    {
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.25f;
        Refresh();
    }

    private void BuildChoices()
    {
        AddChoices(hair, MaoAppearancePart.Hair, new[] { "TÓC GỐC", "TÓC NGẮN", "TÓC LỆCH" });
        AddChoices(hair, MaoAppearancePart.HairColor, new[] { "NGỌC LỤC", "HỒNG", "XANH LAM" });
        AddChoices(outfits, MaoAppearancePart.Outfit, new[] { "NT GỐC", "STAGE ĐEN", "PASTEL", "ÁO KHOÁC" });
        AddChoices(face, MaoAppearancePart.Face, new[] { "MẶT GỐC", "MÁ BẦU", "V-LINE NHẸ" });
        AddChoices(face, MaoAppearancePart.EyeColor, new[] { "MẮT GỐC", "MẮT TÍM", "MẮT VÀNG" });
        AddChoices(accessories, MaoAppearancePart.CatEars, new[] { "TAI: TẮT", "TAI: BẬT" });
        AddChoices(accessories, MaoAppearancePart.CatTail, new[] { "ĐUÔI: TẮT", "ĐUÔI: BẬT" });
        hair.Add(create("appearance-hair-back", "BACK", () => Show(root)));
        outfits.Add(create("appearance-outfit-back", "BACK", () => Show(root)));
        face.Add(create("appearance-face-back", "BACK", () => Show(root)));
        accessories.Add(create("appearance-accessories-back", "BACK", () => Show(root)));
    }

    private void AddChoices(List<GameObject> page, MaoAppearancePart part, string[] labels)
    {
        for (int index = 0; index < labels.Length; index++)
        {
            int id = index;
            var definition = create("appearance-" + part + "-" + id, labels[id], () =>
            {
                var controller = MaoAppearanceController.Active;
                if (controller != null && controller.isActiveAndEnabled && controller.Supported &&
                    controller.Profile != null && controller.CanSelect(part)) controller.Select(part, id);
                Show(page);
            });
            if (definition == null) continue;
            var label = definition.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
            if (label != null)
            {
                label.rectTransform.sizeDelta = new Vector2(92f, 38f);
                label.enableAutoSizing = true;
                label.fontSizeMin = 10f;
                label.fontSizeMax = 13f;
            }
            choices.Add(new Choice
            {
                Button = definition.GetComponent<Xamin.Button>(), Label = label,
                Text = labels[id], Part = part, Id = id
            });
            page.Add(definition);
        }
    }

    private void Refresh()
    {
        var controller = MaoAppearanceController.Active;
        bool supported = controller != null && controller.isActiveAndEnabled && controller.Supported &&
                         controller.Profile != null;
        foreach (var choice in choices)
        {
            if (choice.Button == null) continue;
            bool available = supported && controller.CanSelect(choice.Part);
            bool selected = available && controller.Profile.Get(choice.Part) == choice.Id;
            string text = choice.Text + (!available ? "\nKHÔNG HỖ TRỢ" : selected ? "\n[ĐANG CHỌN]" : string.Empty);
            SetChoiceState(choice.Button, choice.Label, available, selected, text);

            // Open clones definitions; refresh the live copy without rebuilding the wheel.
            var visible = radialMenu != null ? radialMenu.GetButtonWithId(choice.Button.id) : null;
            if (choice.VisibleButton != visible)
            {
                choice.VisibleButton = visible;
                choice.VisibleLabel = visible != null
                    ? visible.transform.Find("Label")?.GetComponent<TextMeshProUGUI>() : null;
            }
            if (visible != null && visible != choice.Button)
                SetChoiceState(visible, choice.VisibleLabel, available, selected, text);
        }
    }

    private static void SetChoiceState(Xamin.Button button, TextMeshProUGUI label,
        bool available, bool selected, string text)
    {
        button.unlocked = available;
        button.useCustomColor = selected;
        button.customColor = new Color(0.22f, 0.86f, 0.83f);
        if (label != null && label.text != text) label.text = text;
    }
}
