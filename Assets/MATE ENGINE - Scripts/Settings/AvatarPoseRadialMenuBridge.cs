using System.Collections;
using System.Collections.Generic;
using MaoIntegration;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public sealed class AvatarPoseRadialMenuBridge : MonoBehaviour
{
    static readonly Color ActiveColor = new Color32(46, 205, 214, 255);
    static readonly string[] ExpressionIds =
        { "neutral", "happy", "curious", "shy", "surprised", "pout", "angry", "sad", "sleepy" };

    Xamin.CircleSelector radialMenu;
    readonly List<GameObject> mainButtons = new List<GameObject>();
    readonly List<GameObject> poseButtons = new List<GameObject>();
    readonly List<GameObject> poseChoiceButtons = new List<GameObject>();
    readonly List<GameObject> expressionButtons = new List<GameObject>();
    readonly List<Xamin.Button> poseChoiceControls = new List<Xamin.Button>();
    readonly List<Xamin.Button> expressionControls = new List<Xamin.Button>();
    readonly List<Xamin.Button> seatControls = new List<Xamin.Button>();
    readonly List<GameObject> definitions = new List<GameObject>();
    readonly List<GameObject> seatButtons = new List<GameObject>();
    Xamin.Button poseEntry;
    GameObject seatEntryDefinition;
    Xamin.Button seatEntryButton;
    GameObject expressionEntryDefinition;
    Xamin.Button expressionEntryButton;
    GameObject previousPageDefinition;
    GameObject nextPageDefinition;
    GameObject stopDefinition;
    GameObject backDefinition;
    Xamin.Button previousPageButton;
    Xamin.Button nextPageButton;
    Xamin.Button stopButton;
    AvatarPoseController poseController;
    // Scene lookups are performed at a bounded cadence and the active
    // controller is reused by the hot button-state path.  This avoids a
    // FindObjectsByType allocation every time the radial menu is refreshed.
    AvatarTaskbarController taskbarController;
    MaoReactionDirector reactionDirector;
    Coroutine reopenRoutine;
    float nextControllerScan;
    int posePage;

    public void Initialize(Xamin.CircleSelector menu)
    {
        if (menu == null || radialMenu == menu) return;
        radialMenu = menu;
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--mao-smoke-seating") >= 0)
            StartCoroutine(InspectSeating());

        GameObject template = FindTemplate(menu.Buttons);
        if (template == null) return;

        mainButtons.AddRange(menu.Buttons);
        RemoveLegacySeatEntries();
        GameObject entry = CreateButton(template, "poses", "POSE", "MikuPoses/Console/Pose", OpenPoseMenu);
        poseEntry = entry.GetComponent<Xamin.Button>();
        poseEntry.hideIfAnimatorBool = AvatarPoseSelectionLogic.ConsoleEntryHiddenAnimatorBools();
        poseEntry.hideIfStateName = AvatarPoseSelectionLogic.ConsoleEntryHiddenAnimatorStates();

        int jumpOffIndex = mainButtons.FindIndex(IsJumpOffButton);
        mainButtons.Insert(jumpOffIndex >= 0 ? jumpOffIndex : mainButtons.Count, entry);
        // Seating is a pose choice, not three competing top-level actions.  Keep
        // the main wheel readable and put all seat variants behind the POSE entry.
        seatEntryDefinition = CreateButton(template, "pose-seating", "NGỒI", "MikuPoses/Console/Pose", OpenSeatMenu);
        seatEntryButton = seatEntryDefinition.GetComponent<Xamin.Button>();
        expressionEntryDefinition = CreateButton(template, "pose-expression", "MẶT", "MikuPoses/Console/Pose", OpenExpressionMenu);
        expressionEntryButton = expressionEntryDefinition.GetComponent<Xamin.Button>();
        CreateExpressionChoices(template);
        reactionDirector = FindReactionDirector();
        taskbarController = FindActiveTaskbarController();
        AddSeatButton(template, "seat-chair", "GHẾ", "MikuPoses/Console/Pose", () => ToggleSeat(3));
        AddSeatButton(template, "seat-pet", "PET", "MikuPoses/Console/Pose", () => ToggleSeat(2));
        AddSeatButton(template, "seat-plain", "NGỒI", "MikuPoses/Console/Pose", () => ToggleSeat(1));
        AddSeatButton(template, "seat-stop", "ĐỨNG DẬY", "MikuPoses/Console/Stop", () => SelectSeat(0));
        seatButtons.Add(CreateButton(template, "seat-back", "BACK", "MikuPoses/Console/Previous", ReturnToMainMenu));

        previousPageDefinition = CreateButton(template, "pose-page-previous", "PAGE -", "MikuPoses/Console/Previous", PreviousPosePage);
        nextPageDefinition = CreateButton(template, "pose-page-next", "PAGE +", "MikuPoses/Console/Next", NextPosePage);
        stopDefinition = CreateButton(template, "pose-stop", "STOP", "MikuPoses/Console/Stop", StopPose);
        backDefinition = CreateButton(template, "pose-back", "BACK", "MikuPoses/Console/Previous", ReturnToMainMenu);
        previousPageButton = previousPageDefinition.GetComponent<Xamin.Button>();
        nextPageButton = nextPageDefinition.GetComponent<Xamin.Button>();
        stopButton = stopDefinition.GetComponent<Xamin.Button>();

        var appearance = GetComponent<MaoAppearanceRadialMenu>();
        if (appearance == null) appearance = gameObject.AddComponent<MaoAppearanceRadialMenu>();
        mainButtons.Add(appearance.Initialize(
            (id, label, action) => CreateButton(template, id, label, "MikuPoses/Console/Pose", action),
            page => { SetButtons(page); ReopenRadialNextFrame(); }, ReturnToMainMenu));
        mainButtons.Add(CreateButton(template, "miku-permissions", "QUYỀN", "MikuPoses/Console/Pose", () =>
        {
            var harness = Object.FindFirstObjectByType<MaoHarnessRuntime>();
            if (harness != null) harness.ShowPermissions();
        }));
        SetButtons(mainButtons);
        Debug.Log("[Mao Seating Menu] pose_submenu=true appearance_submenu=true main_buttons=" + mainButtons.Count);
        RefreshPoseController(true);
    }

    public void PrepareMainMenu()
    {
        if (radialMenu != null) SetButtons(mainButtons);
    }

    private IEnumerator InspectSeating()
    {
        yield return new WaitForSecondsRealtime(15);
        // AvatarTaskbarController is attached lazily after the VRM swap. Ask
        // the same public seat path used by the radial button to create the
        // adapter, then allow one frame for Unity to register it before
        // reporting the smoke result. This avoids a false controllers=0
        // warning during the normal avatar lifecycle race.
        SelectSeat(3);
        yield return null;
        yield return new WaitForSecondsRealtime(0.25f);
        var seats = Object.FindObjectsByType<AvatarTaskbarController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log("[Mao Seating Smoke] controllers=" + seats.Length);
        foreach (var seat in seats) Debug.Log("[Mao Seating Smoke] controller=" + seat.name + " active=" + seat.isActiveAndEnabled);
    }

    private void OpenSeatMenu()
    {
        SetButtons(seatButtons);
        ReopenRadialNextFrame();
    }

    private void OpenExpressionMenu()
    {
        SetButtons(expressionButtons);
        ReopenRadialNextFrame();
    }

    private void ReturnToPoseMenu()
    {
        BuildPosePage();
        SetButtons(poseButtons);
        ReopenRadialNextFrame();
    }

    private void SelectExpression(string emotion)
    {
        MaoReactionDirector director = reactionDirector != null && reactionDirector.isActiveAndEnabled
            ? reactionDirector
            : (reactionDirector = FindReactionDirector());
        bool applied = director != null && director.SetManualExpression(emotion);
        Debug.Log("[MikuExpressionMenu] selected=" + emotion + " applied=" + applied);
        ReturnToPoseMenu();
    }

    private void CreateExpressionChoices(GameObject template)
    {
        expressionButtons.Clear();
        expressionControls.Clear();
        string[] emotions = { "neutral", "happy", "curious", "shy", "surprised", "pout", "angry", "sad", "sleepy" };
        string[] labels = { "BÌNH THƯỜNG", "VUI", "TÒ MÒ", "NGẠI", "BẤT NGỜ", "DỖI", "GIẬN", "BUỒN", "NGỦ" };
        for (int index = 0; index < emotions.Length; index++)
        {
            string selected = emotions[index];
            GameObject definition = CreateButton(template, "expression-" + selected, labels[index],
                "MikuPoses/Console/Pose", () => SelectExpression(selected));
            expressionButtons.Add(definition);
            expressionControls.Add(definition.GetComponent<Xamin.Button>());
        }
        expressionButtons.Add(CreateButton(template, "expression-back", "BACK",
            "MikuPoses/Console/Previous", ReturnToPoseMenu));
    }

    void AddSeatButton(GameObject template, string id, string label, string iconPath, UnityAction action)
    {
        GameObject definition = CreateButton(template, id, label, iconPath, action);
        seatButtons.Add(definition);
        seatControls.Add(definition.GetComponent<Xamin.Button>());
    }

    private void SelectSeat(int choice)
    {
        RefreshRuntimeControllers();
        AvatarTaskbarController controller = taskbarController;
        if (controller == null)
        {
            var pose = FindActivePoseController();
            if (pose != null && pose.avatarAnimator != null)
            {
                var added = pose.avatarAnimator.gameObject.AddComponent<AvatarTaskbarController>();
                added.SetAnimator(pose.avatarAnimator);
                controller = taskbarController = added;
            }
        }
        if (controller != null && controller.isActiveAndEnabled && controller.avatarAnimator != null)
        {
            controller.SelectSeat(choice);
        }
        ReopenRadialNextFrame();
    }

    private void ToggleSeat(int choice)
    {
        RefreshRuntimeControllers();
        if (taskbarController != null && taskbarController.isActiveAndEnabled && taskbarController.ManualSeat == choice)
        {
            SelectSeat(0);
            return;
        }
        SelectSeat(choice);
    }

    void Update()
    {
        if (Time.unscaledTime < nextControllerScan) return;
        nextControllerScan = Time.unscaledTime + 0.25f;
        RefreshRuntimeControllers();
        RefreshPoseController(false);
    }

    void RefreshRuntimeControllers()
    {
        if (reactionDirector == null || !reactionDirector.isActiveAndEnabled)
            reactionDirector = FindReactionDirector();
        AvatarTaskbarController foundTaskbar = FindActiveTaskbarController();
        if (foundTaskbar != taskbarController)
        {
            taskbarController = foundTaskbar;
            // A taskbar controller can be attached after the radial menu was
            // initialized.  Refresh once when that lifecycle changes so seat
            // availability and active highlighting stay accurate.
            ApplyButtonState();
        }
    }

    void OnDestroy()
    {
        if (poseController != null) poseController.PoseChanged -= OnPoseChanged;
        for (int i = 0; i < definitions.Count; i++)
            if (definitions[i] != null)
            {
                if (Application.isPlaying) Destroy(definitions[i]);
                else DestroyImmediate(definitions[i]);
            }
    }

    void OpenPoseMenu()
    {
        RefreshPoseController(true);
        posePage = poseController != null && poseController.IsPoseActive
            ? poseController.CurrentPoseIndex / AvatarPoseSelectionLogic.PoseMenuPageCapacity
            : 0;
        BuildPosePage();
        SetButtons(poseButtons);
        Debug.Log($"[MikuPoseMenu] opened=true page={posePage + 1} choices={poseChoiceButtons.Count}");
        ReopenRadialNextFrame();
    }

    void PlayPose(int poseIndex)
    {
        // A HumanPose is applied in LateUpdate and must not compete with
        // cursor locomotion. Stop follow before applying the frame so the
        // coordinator can grant the pose owner immediately rather than one
        // frame later.
        if (reactionDirector == null || !reactionDirector.isActiveAndEnabled)
            reactionDirector = FindReactionDirector();
        if (reactionDirector != null && reactionDirector.CursorFollowEnabled)
            reactionDirector.SetCursorFollowEnabled(false);
        bool applied = poseController != null && poseController.TogglePose(poseIndex);
        string poseName = poseController != null ? poseController.GetPoseName(poseIndex) : string.Empty;
        Debug.Log($"[MikuPoseMenu] selected={poseIndex} name={poseName} applied={applied}");
        ApplyButtonState();
        ReopenRadialNextFrame();
    }

    void PreviousPosePage()
    {
        if (posePage > 0) posePage--;
        BuildPosePage();
        SetButtons(poseButtons);
        ReopenRadialNextFrame();
    }

    void NextPosePage()
    {
        int pageCount = AvatarPoseSelectionLogic.PosePageCount(poseChoiceButtons.Count);
        if (posePage + 1 < pageCount) posePage++;
        BuildPosePage();
        SetButtons(poseButtons);
        ReopenRadialNextFrame();
    }

    void StopPose()
    {
        if (poseController != null) poseController.StopPose();
        ReturnToMainMenu();
    }

    void ReturnToMainMenu()
    {
        SetButtons(mainButtons);
        ReopenRadialNextFrame();
    }

    void BuildPosePage()
    {
        poseButtons.Clear();
        int pageCount = AvatarPoseSelectionLogic.PosePageCount(poseChoiceButtons.Count);
        if (pageCount == 0) posePage = 0;
        else posePage = Mathf.Clamp(posePage, 0, pageCount - 1);

        int[] indices = AvatarPoseSelectionLogic.PoseIndicesForPage(posePage, poseChoiceButtons.Count);
        for (int index = 0; index < indices.Length; index++) poseButtons.Add(poseChoiceButtons[indices[index]]);
        if (seatEntryDefinition != null) poseButtons.Add(seatEntryDefinition);
        if (expressionEntryDefinition != null) poseButtons.Add(expressionEntryDefinition);
        if (posePage > 0) poseButtons.Add(previousPageDefinition);
        if (posePage + 1 < pageCount) poseButtons.Add(nextPageDefinition);
        poseButtons.Add(stopDefinition);
        poseButtons.Add(backDefinition);
        ApplyButtonState();
    }

    void ReopenRadialNextFrame()
    {
        if (reopenRoutine != null) StopCoroutine(reopenRoutine);
        reopenRoutine = StartCoroutine(ReopenAfterSelection());
    }

    IEnumerator ReopenAfterSelection()
    {
        yield return null;
        reopenRoutine = null;
        if (radialMenu != null && isActiveAndEnabled) radialMenu.Open();
    }

    void RefreshPoseController(bool force)
    {
        AvatarPoseController found = FindActivePoseController();
        bool controllerChanged = found != poseController;
        if (controllerChanged)
        {
            if (poseController != null) poseController.PoseChanged -= OnPoseChanged;
            poseController = found;
            if (poseController != null) poseController.PoseChanged += OnPoseChanged;
            RebuildPoseChoices();
        }
        else if (force && poseController != null && poseChoiceButtons.Count != poseController.PoseCount)
        {
            RebuildPoseChoices();
        }
        ApplyButtonState();
    }

    void RebuildPoseChoices()
    {
        for (int index = 0; index < poseChoiceButtons.Count; index++)
        {
            GameObject oldDefinition = poseChoiceButtons[index];
            definitions.Remove(oldDefinition);
            DestroyDefinition(oldDefinition);
        }
        poseChoiceButtons.Clear();
        poseChoiceControls.Clear();
        if (poseController == null || radialMenu == null || mainButtons.Count == 0) return;

        GameObject template = FindTemplate(mainButtons);
        if (template == null) return;
        for (int poseIndex = 0; poseIndex < poseController.PoseCount; poseIndex++)
        {
            int selectedPose = poseIndex;
            string poseName = poseController.GetPoseName(poseIndex);
            GameObject choice = CreateButton(
                template,
                "pose-choice-" + poseIndex,
                AvatarPoseSelectionLogic.PoseMenuLabel(poseName),
                "MikuPoses/Console/Pose",
                () => PlayPose(selectedPose));
            poseChoiceButtons.Add(choice);
            poseChoiceControls.Add(choice.GetComponent<Xamin.Button>());
        }
        BuildPosePage();
    }

    void OnPoseChanged(int poseIndex, string poseName)
    {
        ApplyButtonState();
    }

    void ApplyButtonState()
    {
        bool poseAvailable = poseController != null && poseController.PoseCount > 0;
        // A pose controller is enough to create the taskbar seating adapter on
        // demand. The active taskbar reference is refreshed at a bounded
        // cadence by RefreshRuntimeControllers; do not scan the scene here.
        bool seatAvailable = taskbarController != null || poseController != null;
        bool available = poseAvailable || seatAvailable;
        bool active = poseAvailable && poseController.IsPoseActive;
        if (poseEntry != null)
        {
            poseEntry.unlocked = available;
            poseEntry.useCustomColor = active;
            poseEntry.customColor = ActiveColor;
        }
        for (int index = 0; index < poseChoiceButtons.Count; index++)
        {
            Xamin.Button choice = index < poseChoiceControls.Count ? poseChoiceControls[index] : null;
            if (choice == null) continue;
            choice.unlocked = poseAvailable && index < poseController.PoseCount;
            choice.useCustomColor = poseAvailable && active && poseController.CurrentPoseIndex == index;
            choice.customColor = ActiveColor;
        }
        int pageCount = AvatarPoseSelectionLogic.PosePageCount(poseChoiceButtons.Count);
        if (previousPageButton != null) previousPageButton.unlocked = available && posePage > 0;
        if (nextPageButton != null) nextPageButton.unlocked = available && posePage + 1 < pageCount;
        if (stopButton != null) stopButton.unlocked = active;
        if (seatEntryButton != null)
        {
            seatEntryButton.unlocked = seatAvailable;
            seatEntryButton.useCustomColor = ActiveSeat() != 0;
            seatEntryButton.customColor = ActiveColor;
        }
        if (expressionEntryButton != null)
        {
            expressionEntryButton.unlocked = reactionDirector != null && reactionDirector.isActiveAndEnabled;
            expressionEntryButton.useCustomColor = reactionDirector != null && reactionDirector.ExpressionActive;
            expressionEntryButton.customColor = ActiveColor;
        }
        int activeSeat = ActiveSeat();
        for (int index = 0; index < seatButtons.Count && index < 4; index++)
        {
            Xamin.Button seat = index < seatControls.Count ? seatControls[index] : null;
            if (seat == null) continue;
            int choice = index < 3 ? index + 1 : 0;
            seat.unlocked = seatAvailable;
            seat.useCustomColor = choice != 0 && activeSeat == choice;
            seat.customColor = ActiveColor;
        }
        string activeExpression = reactionDirector != null ? reactionDirector.CurrentExpression : "neutral";
        for (int index = 0; index < expressionControls.Count && index < ExpressionIds.Length; index++)
        {
            Xamin.Button expression = expressionControls[index];
            if (expression == null) continue;
            expression.unlocked = reactionDirector != null && reactionDirector.isActiveAndEnabled;
            expression.useCustomColor = reactionDirector != null && reactionDirector.ExpressionActive &&
                string.Equals(activeExpression, ExpressionIds[index], System.StringComparison.OrdinalIgnoreCase);
            expression.customColor = ActiveColor;
        }
    }

    int ActiveSeat()
    {
        return taskbarController != null && taskbarController.isActiveAndEnabled
            ? taskbarController.ManualSeat
            : 0;
    }

    GameObject CreateButton(GameObject template, string id, string label, string iconPath, UnityAction action)
    {
        GameObject definition = AvatarPoseRadialButtonFactory.Create(template, transform, id, label, iconPath, action);
        definitions.Add(definition);
        return definition;
    }

    void SetButtons(List<GameObject> source)
    {
        radialMenu.Buttons.Clear();
        radialMenu.Buttons.AddRange(source);
    }

    static void DestroyDefinition(GameObject definition)
    {
        if (definition == null) return;
        if (Application.isPlaying) Destroy(definition);
        else DestroyImmediate(definition);
    }

    static GameObject FindTemplate(List<GameObject> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i] != null && buttons[i].GetComponent<Xamin.Button>() != null) return buttons[i];
        return null;
    }

    static bool IsJumpOffButton(GameObject button)
    {
        Xamin.Button component = button != null ? button.GetComponent<Xamin.Button>() : null;
        return component != null && component.id == "jumpoff";
    }

    void RemoveLegacySeatEntries()
    {
        for (int index = mainButtons.Count - 1; index >= 0; index--)
        {
            Xamin.Button button = mainButtons[index] != null ? mainButtons[index].GetComponent<Xamin.Button>() : null;
            string id = button != null ? (button.id ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
            if (id == "ghe" || id == "ghế" || id == "pet" || id == "ngoi" || id == "ngồi")
                mainButtons.RemoveAt(index);
        }
    }

    static AvatarPoseController FindActivePoseController()
    {
        AvatarPoseController[] controllers = Object.FindObjectsByType<AvatarPoseController>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < controllers.Length; i++)
            if (controllers[i] != null && controllers[i].isActiveAndEnabled && controllers[i].gameObject.activeInHierarchy)
                return controllers[i];
        return null;
    }

    static AvatarTaskbarController FindActiveTaskbarController()
    {
        AvatarTaskbarController[] controllers = Object.FindObjectsByType<AvatarTaskbarController>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int index = 0; index < controllers.Length; index++)
            if (controllers[index] != null && controllers[index].isActiveAndEnabled && controllers[index].gameObject.activeInHierarchy)
                return controllers[index];
        return null;
    }

    static MaoReactionDirector FindReactionDirector()
    {
        MaoReactionDirector[] directors = Object.FindObjectsByType<MaoReactionDirector>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int index = 0; index < directors.Length; index++)
            if (directors[index] != null && directors[index].isActiveAndEnabled)
                return directors[index];
        return null;
    }
}
