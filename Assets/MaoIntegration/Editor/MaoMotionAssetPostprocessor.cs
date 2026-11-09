#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public sealed class MaoMotionAssetPostprocessor : AssetPostprocessor
{
    private const string MotionFolder = "Assets/MaoIntegration/Motions/";
    private const string FemaleLocomotionFolder = MotionFolder + "FemaleLocomotion/";
    private const string PoseFbxFolder = "Assets/Resources/MikuPoses/Fbx/";
    private const string RunAsset = MotionFolder + "Medium Run.fbx";
    private const string JumpAsset = MotionFolder + "Jump.fbx";
    private const string MacaronFlightMotionAsset =
        "Assets/noirunn/KawaiiMacaronMotion/KawaiiMacaronMotion02.anim";
    private const string GuitarMotionAsset = MotionFolder + "Guitar Playing.fbx";
    private const string GuitarPropAsset = "Assets/Resources/MaoGuitar/Guitar.fbx";
    private static readonly string[] FemaleLocomotionAssets =
    {
        FemaleLocomotionFolder + "idle.fbx",
        FemaleLocomotionFolder + "walking.fbx",
        FemaleLocomotionFolder + "running.fbx",
        FemaleLocomotionFolder + "left turn.fbx",
        FemaleLocomotionFolder + "right turn.fbx",
        FemaleLocomotionFolder + "left strafe walk.fbx",
        FemaleLocomotionFolder + "right strafe walk.fbx",
        FemaleLocomotionFolder + "left strafe.fbx",
        FemaleLocomotionFolder + "right strafe.fbx",
        FemaleLocomotionFolder + "jump.fbx"
    };
    private static readonly string[] IdleActionAssets =
    {
        MotionFolder + "Angry.fbx",
        MotionFolder + "Jab Cross.fbx",
        MotionFolder + "Box Jump.fbx",
        MotionFolder + "Jog In Circle.fbx",
        MotionFolder + "Flying Kick.fbx"
    };
    private static readonly string[] IdleActionStates =
    {
        "Angry", "Jab Cross", "Box Jump", "Jog In Circle", "Flying Kick"
    };
    private static readonly string[] GravityAssets =
    {
        MotionFolder + "Falling.fbx",
        MotionFolder + "Falling Flat Impact.fbx",
        MotionFolder + "Standing Up.fbx",
        MotionFolder + "Hard Landing.fbx"
    };
    private static readonly string[] GravityStates =
    {
        "Falling", "Falling Flat Impact", "Standing Up", "Hard Landing"
    };
    private const string ControllerAsset = "Assets/MATE ENGINE - Animations/AvatarAnimatorControllerV2 1.controller";
    private const string LocomotionLayer = "Locomotion Layer";
    private const string GuitarLayer = "Mao Guitar Layer";
    private const string IdleActionLayer = "Mao Idle Actions";
    private const string GravityLayer = "Mao Gravity";
    private const string CursorFlightLayer = "Mao Cursor Flight";

    private void OnPreprocessModel()
    {
        if (string.Equals(assetPath, GuitarPropAsset, StringComparison.OrdinalIgnoreCase))
        {
            var propImporter = (ModelImporter)assetImporter;
            propImporter.importAnimation = false;
            propImporter.importCameras = false;
            propImporter.importLights = false;
            propImporter.isReadable = true;
            return;
        }
        bool isPoseFbx = assetPath.StartsWith(PoseFbxFolder, StringComparison.OrdinalIgnoreCase);
        if (!assetPath.StartsWith(MotionFolder, StringComparison.OrdinalIgnoreCase) && !isPoseFbx) return;

        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.importCameras = false;
        importer.importLights = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
        bool isFemaleLocomotion = assetPath.StartsWith(FemaleLocomotionFolder, StringComparison.OrdinalIgnoreCase);
        bool loop = isPoseFbx ||
                    (isFemaleLocomotion && !assetPath.EndsWith("turn.fbx", StringComparison.OrdinalIgnoreCase) &&
                     !assetPath.EndsWith("jump.fbx", StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(assetPath, RunAsset, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(assetPath, GuitarMotionAsset, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(assetPath, GravityAssets[0], StringComparison.OrdinalIgnoreCase);
        for (int index = 0; index < clips.Length; index++)
        {
            clips[index].loop = loop;
            clips[index].loopTime = loop;
            clips[index].loopPose = loop;
            clips[index].lockRootPositionXZ = true;
            clips[index].lockRootRotation = true;
            clips[index].lockRootHeightY = !string.Equals(assetPath, JumpAsset, StringComparison.OrdinalIgnoreCase);
        }
        if (clips.Length > 0) importer.clipAnimations = clips;
    }

    [MenuItem("Mao/Motions/Configure Advanced Female Locomotion")]
    public static void ConfigureAdvancedLocomotionAndValidate()
    {
        var clips = new AnimationClip[FemaleLocomotionAssets.Length];
        for (int index = 0; index < FemaleLocomotionAssets.Length; index++)
        {
            string path = FemaleLocomotionAssets[index];
            bool loop = index <= 2 || (index >= 5 && index <= 8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureClipSettings(path, loop, true);
            ValidateHumanoid(path);
            clips[index] = LoadClip(path);
        }

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerAsset);
        if (controller == null) throw new InvalidOperationException("Mao Animator Controller was not found.");
        AddFloatParameter(controller, "MaoMoveSpeed");
        AddFloatParameter(controller, "MaoMoveX");
        AddFloatParameter(controller, "MaoTurn");
        AddFloatParameter(controller, "MaoStrafe");
        AddBoolParameter(controller, "MaoGrounded");
        AddTriggerParameter(controller, "MaoGroundJump");

        AnimatorControllerLayer layer = GetOrCreateLayer(controller, LocomotionLayer);
        layer.iKPass = true;
        AnimatorState passthrough = UpsertState(
            layer.stateMachine,
            "Idle",
            null,
            false,
            new Vector3(220f, 20f));
        layer.stateMachine.defaultState = passthrough;

        BlendTree blend = GetOrCreateGroundBlendTree(controller);
        blend.blendType = BlendTreeType.Simple1D;
        blend.blendParameter = "MaoMoveSpeed";
        blend.useAutomaticThresholds = false;
        blend.children = new[]
        {
            new ChildMotion { motion = clips[0], threshold = 0f, timeScale = 1f },
            new ChildMotion { motion = clips[1], threshold = 0.45f, timeScale = 1f },
            new ChildMotion { motion = clips[2], threshold = 1f, timeScale = 1f }
        };

        UpsertState(layer.stateMachine, "Mao Ground Blend", blend, false, new Vector3(470f, 20f)).iKOnFeet = true;
        UpsertState(layer.stateMachine, "Mao Turn Left", clips[3], false, new Vector3(470f, 100f));
        UpsertState(layer.stateMachine, "Mao Turn Right", clips[4], false, new Vector3(470f, 170f));
        UpsertState(layer.stateMachine, "Mao Strafe Walk Left", clips[5], false, new Vector3(720f, 20f));
        UpsertState(layer.stateMachine, "Mao Strafe Walk Right", clips[6], false, new Vector3(720f, 90f));
        UpsertState(layer.stateMachine, "Mao Strafe Left", clips[7], false, new Vector3(720f, 160f));
        UpsertState(layer.stateMachine, "Mao Strafe Right", clips[8], false, new Vector3(720f, 230f));
        AnimatorState jump = UpsertState(
            layer.stateMachine,
            "Mao Ground Jump",
            clips[9],
            false,
            new Vector3(470f, 250f));
        jump.iKOnFeet = false;

        EditorUtility.SetDirty(blend);
        EditorUtility.SetDirty(layer.stateMachine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string reportPath = Path.Combine(projectRoot, "Temp", "MaoAdvancedLocomotionImportReport.txt");
        File.WriteAllLines(reportPath, clips.Select((clip, index) =>
            $"{Path.GetFileNameWithoutExtension(FemaleLocomotionAssets[index])}={clip.name};length={clip.length:0.000};loop={clip.isLooping};humanoid=true;root_baked=true"));
        Debug.Log("[Mao Advanced Locomotion] Ten Humanoid clips, blend tree, turns, strafes, and jump configured.");
    }

    [MenuItem("Mao/Motions/Configure Guitar Performance")]
    public static void ConfigureGuitarAndValidate()
    {
        AssetDatabase.ImportAsset(GuitarMotionAsset, ImportAssetOptions.ForceUpdate);
        AssetDatabase.ImportAsset(GuitarPropAsset, ImportAssetOptions.ForceUpdate);
        ConfigureClipSettings(GuitarMotionAsset, true, true);

        AnimationClip guitarClip = LoadClip(GuitarMotionAsset);
        ValidateHumanoid(GuitarMotionAsset);
        GameObject guitarProp = AssetDatabase.LoadAssetAtPath<GameObject>(GuitarPropAsset);
        if (guitarProp == null || guitarProp.GetComponentsInChildren<Renderer>(true).Length == 0)
            throw new InvalidOperationException("The guitar prop does not contain a renderable model: " + GuitarPropAsset);

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerAsset);
        if (controller == null) throw new InvalidOperationException("Mao Animator Controller was not found.");
        AddBoolParameter(controller, "MaoGuitarPlaying");

        AnimatorControllerLayer layer = GetOrCreateLayer(controller, GuitarLayer);
        layer.iKPass = true;
        AnimatorState idle = UpsertState(layer.stateMachine, "Idle", null, false, new Vector3(250f, 40f));
        AnimatorState playing = UpsertState(
            layer.stateMachine,
            "Play Guitar",
            guitarClip,
            false,
            new Vector3(520f, 40f));
        layer.stateMachine.defaultState = idle;
        EnsureAnyStateBoolTransition(layer.stateMachine, playing, "MaoGuitarPlaying", 0.25f);
        EnsureBoolExit(playing, idle, "MaoGuitarPlaying", 0.25f);

        EditorUtility.SetDirty(layer.stateMachine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string reportPath = Path.Combine(projectRoot, "Temp", "MaoGuitarImportReport.txt");
        File.WriteAllText(reportPath,
            $"motion={guitarClip.name};length={guitarClip.length:0.000};loop={guitarClip.isLooping}{Environment.NewLine}" +
            $"humanoid=true;root_rotation_baked=true;root_y_baked=true;root_xz_baked=true{Environment.NewLine}" +
            $"prop={guitarProp.name};renderers={guitarProp.GetComponentsInChildren<Renderer>(true).Length}{Environment.NewLine}" +
            $"controller=ok;layer={GuitarLayer};state=Play Guitar;ik_pass={layer.iKPass}");
        Debug.Log("[Mao Guitar] Motion, prop, Animator layer, and Humanoid avatar validated.");
    }

    [MenuItem("Mao/Motions/Configure Run And Jump")]
    public static void ConfigureAndValidate()
    {
        AssetDatabase.ImportAsset(RunAsset, ImportAssetOptions.ForceUpdate);
        AssetDatabase.ImportAsset(JumpAsset, ImportAssetOptions.ForceUpdate);
        ConfigureClipSettings(RunAsset, true, true);
        ConfigureClipSettings(JumpAsset, false, false);

        AnimationClip runClip = LoadClip(RunAsset);
        AnimationClip jumpClip = LoadClip(JumpAsset);
        ValidateHumanoid(RunAsset);
        ValidateHumanoid(JumpAsset);

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerAsset);
        if (controller == null) throw new InvalidOperationException("Mao Animator Controller was not found.");

        AddBoolParameter(controller, "RunLeft");
        AddBoolParameter(controller, "RunRight");
        AddTriggerParameter(controller, "MaoJump");

        AnimatorControllerLayer layer = controller.layers.FirstOrDefault(item => item.name == LocomotionLayer);
        if (layer == null) throw new InvalidOperationException("Locomotion Layer was not found.");

        AnimatorState idle = FindState(layer.stateMachine, "Idle");
        AnimatorState runRight = UpsertState(layer.stateMachine, "Run Right", runClip, false, new Vector3(520f, -20f));
        AnimatorState runLeft = UpsertState(layer.stateMachine, "Run Left", runClip, true, new Vector3(520f, 80f));
        AnimatorState jump = UpsertState(layer.stateMachine, "Mao Jump", jumpClip, false, new Vector3(520f, 180f));

        EnsureAnyStateBoolTransition(layer.stateMachine, runRight, "RunRight");
        EnsureAnyStateBoolTransition(layer.stateMachine, runLeft, "RunLeft");
        EnsureAnyStateTriggerTransition(layer.stateMachine, jump, "MaoJump");
        EnsureBoolExit(runRight, idle, "RunRight");
        EnsureBoolExit(runLeft, idle, "RunLeft");
        EnsureExitTime(jump, idle);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string reportPath = Path.Combine(projectRoot, "Temp", "MaoMotionImportReport.txt");
        File.WriteAllText(reportPath,
            $"run={runClip.name};length={runClip.length:0.000};loop={runClip.isLooping}{Environment.NewLine}" +
            $"jump={jumpClip.name};length={jumpClip.length:0.000};loop={jumpClip.isLooping}{Environment.NewLine}" +
            "controller=ok;states=Run Right,Run Left,Mao Jump");
        Debug.Log("[Mao Motions] Run and Jump clips configured and validated.");
    }

    [MenuItem("Mao/Motions/Configure Macaron Cursor Flight")]
    public static void ConfigureCursorFlightAndValidate()
    {
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(MacaronFlightMotionAsset);
        if (clip == null) throw new InvalidOperationException(
            "Kawaii Macaron flight motion was not found: " + MacaronFlightMotionAsset);
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerAsset);
        if (controller == null) throw new InvalidOperationException("Mao Animator Controller was not found.");
        AnimatorControllerLayer layer = GetOrCreateLayer(controller, CursorFlightLayer);
        layer.iKPass = false;
        foreach (ChildAnimatorState child in layer.stateMachine.states.ToArray())
            layer.stateMachine.RemoveState(child.state);
        foreach (AnimatorStateTransition transition in layer.stateMachine.anyStateTransitions.ToArray())
            layer.stateMachine.RemoveAnyStateTransition(transition);

        AnimatorState idle = layer.stateMachine.AddState("Idle", new Vector3(250f, 40f));
        layer.stateMachine.defaultState = idle;
        AnimatorState flying = layer.stateMachine.AddState("Macaron Flight", new Vector3(520f, 40f));
        flying.motion = clip;
        flying.writeDefaultValues = true;
        flying.iKOnFeet = false;

        EditorUtility.SetDirty(layer.stateMachine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string reportPath = Path.Combine(projectRoot, "Temp", "MaoCursorFlightImportReport.txt");
        File.WriteAllText(reportPath,
            $"Macaron Flight={clip.name};length={clip.length:0.000};legacy_flight_states_removed=true;foot_ik=false");
        Debug.Log("[Mao Macaron Flight] Macaron motion configured; old takeoff/floating/landing states removed.");
    }

    [MenuItem("Mao/Motions/Configure Idle Actions")]
    public static void ConfigureIdleActionsAndValidate()
    {
        var clips = new AnimationClip[IdleActionAssets.Length];
        for (int index = 0; index < IdleActionAssets.Length; index++)
        {
            AssetDatabase.ImportAsset(IdleActionAssets[index], ImportAssetOptions.ForceUpdate);
            ConfigureClipSettings(IdleActionAssets[index], false, true);
            ValidateHumanoid(IdleActionAssets[index]);
            clips[index] = LoadClip(IdleActionAssets[index]);
        }

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerAsset);
        if (controller == null) throw new InvalidOperationException("Mao Animator Controller was not found.");
        AnimatorControllerLayer layer = GetOrCreateLayer(controller, IdleActionLayer);
        layer.iKPass = false;
        AnimatorState idle = UpsertState(layer.stateMachine, "Idle", null, false, new Vector3(250f, 40f));
        layer.stateMachine.defaultState = idle;
        for (int index = 0; index < clips.Length; index++)
        {
            AnimatorState state = UpsertState(
                layer.stateMachine,
                IdleActionStates[index],
                clips[index],
                false,
                new Vector3(520f, 20f + index * 70f));
            EnsureExitTime(state, idle);
        }

        EditorUtility.SetDirty(layer.stateMachine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string reportPath = Path.Combine(projectRoot, "Temp", "MaoIdleActionImportReport.txt");
        File.WriteAllLines(reportPath, clips.Select((clip, index) =>
            $"{IdleActionStates[index]}={clip.name};length={clip.length:0.000};loop={clip.isLooping};humanoid=true;root_baked=true"));
        Debug.Log("[Mao Idle Actions] Five Humanoid actions configured and validated.");
    }

    [MenuItem("Mao/Motions/Configure Desktop Gravity")]
    public static void ConfigureGravityAndValidate()
    {
        var clips = new AnimationClip[GravityAssets.Length];
        for (int index = 0; index < GravityAssets.Length; index++)
        {
            AssetDatabase.ImportAsset(GravityAssets[index], ImportAssetOptions.ForceUpdate);
            ConfigureClipSettings(GravityAssets[index], index == 0, true);
            ValidateHumanoid(GravityAssets[index]);
            clips[index] = LoadClip(GravityAssets[index]);
        }

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerAsset);
        if (controller == null) throw new InvalidOperationException("Mao Animator Controller was not found.");
        AnimatorControllerLayer layer = GetOrCreateLayer(controller, GravityLayer);
        layer.iKPass = true;
        AnimatorState idle = UpsertState(layer.stateMachine, "Idle", null, false, new Vector3(250f, 40f));
        layer.stateMachine.defaultState = idle;
        for (int index = 0; index < clips.Length; index++)
        {
            AnimatorState state = UpsertState(
                layer.stateMachine,
                GravityStates[index],
                clips[index],
                false,
                new Vector3(520f, 20f + index * 90f));
            state.iKOnFeet = index == clips.Length - 1;
        }

        EditorUtility.SetDirty(layer.stateMachine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string reportPath = Path.Combine(projectRoot, "Temp", "MaoGravityImportReport.txt");
        File.WriteAllLines(reportPath, clips.Select((clip, index) =>
            $"{GravityStates[index]}={clip.name};length={clip.length:0.000};loop={clip.isLooping};humanoid=true;root_baked=true;foot_ik={index == clips.Length - 1}"));
        Debug.Log("[Mao Gravity] Falling, impact, standing-up, and hard-landing motions configured and validated.");
    }

    private static void ConfigureClipSettings(string path, bool loop, bool lockHeight)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) throw new InvalidOperationException("Model importer was not found: " + path);
        ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
        if (clips == null || clips.Length == 0)
            throw new InvalidOperationException("No default animation take was found: " + path);
        for (int index = 0; index < clips.Length; index++)
        {
            clips[index].loop = loop;
            clips[index].loopTime = loop;
            clips[index].loopPose = loop;
            clips[index].lockRootPositionXZ = true;
            clips[index].lockRootRotation = true;
            clips[index].lockRootHeightY = lockHeight;
        }
        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    private static AnimationClip LoadClip(string path)
    {
        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(path)
            .OfType<AnimationClip>()
            .FirstOrDefault(item => !item.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
        if (clip == null) throw new InvalidOperationException("No animation clip found in " + path);
        return clip;
    }

    private static void ValidateHumanoid(string path)
    {
        Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
            throw new InvalidOperationException("The FBX does not contain a valid Humanoid avatar: " + path);
    }

    private static void AddBoolParameter(AnimatorController controller, string name)
    {
        if (controller.parameters.Any(parameter => parameter.name == name)) return;
        controller.AddParameter(name, AnimatorControllerParameterType.Bool);
    }

    private static void AddFloatParameter(AnimatorController controller, string name)
    {
        if (controller.parameters.Any(parameter => parameter.name == name)) return;
        controller.AddParameter(name, AnimatorControllerParameterType.Float);
    }

    private static void AddTriggerParameter(AnimatorController controller, string name)
    {
        if (controller.parameters.Any(parameter => parameter.name == name)) return;
        controller.AddParameter(name, AnimatorControllerParameterType.Trigger);
    }

    private static AnimatorState FindState(AnimatorStateMachine machine, string name)
    {
        AnimatorState state = machine.states.Select(item => item.state).FirstOrDefault(item => item.name == name);
        if (state == null) throw new InvalidOperationException("Animator state was not found: " + name);
        return state;
    }

    private static AnimatorState UpsertState(
        AnimatorStateMachine machine,
        string name,
        Motion motion,
        bool mirror,
        Vector3 position)
    {
        AnimatorState state = machine.states.Select(item => item.state).FirstOrDefault(item => item.name == name);
        if (state == null) state = machine.AddState(name, position);
        state.motion = motion;
        state.mirror = mirror;
        state.writeDefaultValues = true;
        return state;
    }

    private static void EnsureAnyStateBoolTransition(
        AnimatorStateMachine machine,
        AnimatorState destination,
        string parameter,
        float duration = 0.12f)
    {
        if (machine.anyStateTransitions.Any(transition => transition.destinationState == destination)) return;
        AnimatorStateTransition transition = machine.AddAnyStateTransition(destination);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.canTransitionToSelf = false;
        transition.AddCondition(AnimatorConditionMode.If, 0f, parameter);
    }

    private static void EnsureAnyStateTriggerTransition(
        AnimatorStateMachine machine,
        AnimatorState destination,
        string parameter)
    {
        if (machine.anyStateTransitions.Any(transition => transition.destinationState == destination)) return;
        AnimatorStateTransition transition = machine.AddAnyStateTransition(destination);
        transition.hasExitTime = false;
        transition.duration = 0.08f;
        transition.canTransitionToSelf = false;
        transition.AddCondition(AnimatorConditionMode.If, 0f, parameter);
    }

    private static void EnsureBoolExit(
        AnimatorState source,
        AnimatorState idle,
        string parameter,
        float duration = 0.15f)
    {
        if (source.transitions.Any(transition => transition.destinationState == idle)) return;
        AnimatorStateTransition transition = source.AddTransition(idle);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, parameter);
    }

    private static void EnsureExitTime(AnimatorState source, AnimatorState idle)
    {
        if (source.transitions.Any(transition => transition.destinationState == idle)) return;
        AnimatorStateTransition transition = source.AddTransition(idle);
        transition.hasExitTime = true;
        transition.exitTime = 0.92f;
        transition.duration = 0.12f;
    }

    private static AnimatorControllerLayer GetOrCreateLayer(AnimatorController controller, string layerName)
    {
        AnimatorControllerLayer existing = controller.layers.FirstOrDefault(item => item.name == layerName);
        if (existing != null) return existing;

        var stateMachine = new AnimatorStateMachine { name = layerName };
        AssetDatabase.AddObjectToAsset(stateMachine, controller);
        var layer = new AnimatorControllerLayer
        {
            name = layerName,
            stateMachine = stateMachine,
            defaultWeight = 1f,
            blendingMode = AnimatorLayerBlendingMode.Override,
            iKPass = true
        };
        controller.AddLayer(layer);
        return controller.layers.First(item => item.name == layerName);
    }

    private static BlendTree GetOrCreateGroundBlendTree(AnimatorController controller)
    {
        const string blendName = "Mao Ground Locomotion Blend";
        BlendTree existing = AssetDatabase.LoadAllAssetsAtPath(ControllerAsset)
            .OfType<BlendTree>()
            .FirstOrDefault(tree => tree.name == blendName);
        if (existing != null) return existing;

        var blend = new BlendTree { name = blendName };
        AssetDatabase.AddObjectToAsset(blend, controller);
        return blend;
    }
}
#endif
