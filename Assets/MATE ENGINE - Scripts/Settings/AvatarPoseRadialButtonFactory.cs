using TMPro;
using UnityEngine;
using UnityEngine.Events;

internal static class AvatarPoseRadialButtonFactory
{
    public static GameObject Create(
        GameObject template,
        Transform owner,
        string id,
        string labelText,
        string iconPath,
        UnityAction action)
    {
        GameObject definition = Object.Instantiate(template, owner);
        definition.name = "Pose Console - " + labelText;
        definition.hideFlags = HideFlags.HideInHierarchy;
        definition.transform.localPosition = Vector3.zero;
        definition.transform.localScale = Vector3.one;

        Xamin.Button button = definition.GetComponent<Xamin.Button>();
        button.action = new UnityEvent();
        button.action.AddListener(action);
        button.id = id;
        button.image = Resources.Load<Sprite>(iconPath);
        button.unlocked = true;
        button.hideIfAnimatorBool = new string[0];
        button.hideIfStateName = new string[0];
        button.showOnlyIfAnimatorBool = new string[0];
        button.showOnlyIfStateName = new string[0];

        AddLabel(definition.transform, labelText);
        if (id.StartsWith("seat-direct-", System.StringComparison.Ordinal))
        {
            // Selection uses the radial segment, so the icon can be replaced by
            // readable text without changing the circle's mouse hit detection.
            definition.GetComponent<UnityEngine.UI.Image>().enabled = false;
            var label = definition.transform.Find("Label").GetComponent<TextMeshProUGUI>();
            label.rectTransform.anchoredPosition = Vector2.zero;
            label.rectTransform.sizeDelta = new Vector2(64f, 34f);
            label.fontSize = 16f;
            label.fontStyle = FontStyles.Bold;
        }
        return definition;
    }

    static void AddLabel(Transform parent, string text)
    {
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelObject.layer = parent.gameObject.layer;
        RectTransform rect = labelObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchoredPosition = new Vector2(0f, -42f);
        rect.sizeDelta = new Vector2(92f, 22f);

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = 13f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
    }
}
