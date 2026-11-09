using System;
using System.Collections.Generic;
using System.IO;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using VRM;

/// <summary>
/// Converts the Humanoid AnimationClips used by Mate Engine into VRM Animation.
/// The desktop app runs Three.js rather than Unity, so this is the bridge that
/// preserves Mate's authored Humanoid motion instead of approximating it bone by bone.
/// </summary>
public static class MaoMateVrmaBatchExporter
{
    private const float ExportFrequency = 30f;

    private static readonly (string OutputName, string AssetPath)[] Clips =
    {
        ("MateIdle01.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE.anim"),
        ("MateIdle02.vrma", "Assets/MATE ENGINE - Animations/UPDATE_2/PET_IDLE_UPDATE2_01.anim"),
        ("MateIdle03.vrma", "Assets/MATE ENGINE - Animations/UPDATE_2/PET_IDLE_UPDATE2_02.anim"),
        ("MateIdle04.vrma", "Assets/MATE ENGINE - Animations/UPDATE_2/PET_IDLE_UPDATE2_03.anim"),
        ("MateIdle05.vrma", "Assets/MATE ENGINE - Animations/UPDATE_2/PET_IDLE_UPDATE2_04.anim"),
        ("MateIdle06.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_2.anim"),
        ("MateIdle07.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_14.anim"),
        ("MateIdle08.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_4.anim"),
        ("MateIdle09.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_3.anim"),
        ("MateIdle10.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_5.anim"),
        ("MateIdle11.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_15.anim"),
        ("MateIdle12.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_13.anim"),
        ("MateIdle13.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_9.anim"),
        ("MateIdle14.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_12.anim"),
        ("MateIdle15.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_6.anim"),
        ("MateIdle16.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_10.anim"),
        ("MateIdle17.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_11.anim"),
        ("MateIdle18.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_7.anim"),
        ("MateIdle19.vrma", "Assets/MATE ENGINE - Animations/PET_IDLE/PET_IDLE_8.anim"),
        ("MateWalkLeft.vrma", "Assets/MATE ENGINE - Animations/PET_LOCOMOTION/PET_WALK_LEFT.anim"),
        ("MateWalkRight.vrma", "Assets/MATE ENGINE - Animations/PET_LOCOMOTION/PET_WALK_RIGHT.anim"),
        ("MateDragging.vrma", "Assets/MATE ENGINE - Animations/PET_MISC/PET_DRAGGING.anim"),
        ("MateTaskbarSit.vrma", "Assets/MATE ENGINE - Animations/PET_SITTING/ME_02/sit 1.anim"),
        ("MateWindowSit01.vrma", "Assets/MATE ENGINE - Animations/PET_SITTING/ME_02/sit 1.anim"),
        ("MateDance01.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING.anim"),
        ("MateDance02.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_2.anim"),
        ("MateDance03.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_3.anim"),
        ("MateDance04.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_4.anim"),
        ("MateDance05.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_5.anim"),
        ("MateDance06.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_6.anim"),
        ("MateDance07.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_7.anim"),
        ("MateDance08.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_8.anim"),
        ("MateDance09.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_9.anim"),
        ("MateDance10.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_10.anim"),
        ("MateDance11.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_11.anim"),
        ("MateDance12.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_12.anim"),
        ("MateDance13.vrma", "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_13.anim"),
    };

    public static void ExportAll()
    {
        var modelPath = Environment.GetEnvironmentVariable("MAO_VRM_SOURCE");
        var outputDirectory = Environment.GetEnvironmentVariable("MAO_VRMA_OUTPUT");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            Fail("MAO_VRM_SOURCE must point to Mao's VRM file.");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            Fail("MAO_VRMA_OUTPUT must point to the destination motion directory.");
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        RuntimeGltfInstance instance = null;
        try
        {
            using (var gltf = new GlbFileParser(modelPath).Parse())
            using (var importer = new VRMImporterContext(new VRMData(gltf)))
            {
                instance = importer.Load();
            }

            var sourceAnimator = instance.Root.GetComponent<Animator>();
            if (sourceAnimator == null || sourceAnimator.avatar == null || !sourceAnimator.avatar.isValid || !sourceAnimator.avatar.isHuman)
            {
                throw new InvalidOperationException("Mao VRM did not produce a valid Humanoid Animator/Avatar.");
            }

            var animator = CreateAnimatorOnlyReference(sourceAnimator);
            try
            {
                Debug.Log($"[MaoMateExport] Animator enabled={animator.enabled}, avatar={animator.avatar.name}, human={animator.isHuman}");
                foreach (var (outputName, assetPath) in Clips)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                    if (clip == null)
                    {
                        throw new FileNotFoundException($"Mate clip is missing: {assetPath}");
                    }

                    if (!clip.isHumanMotion)
                    {
                        throw new InvalidOperationException($"Mate clip is not Humanoid: {assetPath}");
                    }

                    var destination = Path.Combine(outputDirectory, outputName);
                    File.WriteAllBytes(destination, CreateVrma(animator, clip));
                    Debug.Log($"[MaoMateExport] {clip.name} -> {destination} ({clip.length:F3}s)");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(animator.gameObject);
            }

            File.WriteAllText(
                Path.Combine(outputDirectory, "MATE-ENGINE-MOTION-NOTICE.txt"),
                "Mate Engine animation derivatives for this local personal installation.\r\n" +
                "Source: https://github.com/shinyflvre/Mate-Engine\r\n" +
                "Source commit: 2c5ea6b8f4cf5e1773a0816b46d9267cda5174d4\r\n" +
                "Copyright (c) 2025 Shiny. See the source repository LICENSE.md and NOTICE.txt.\r\n" +
                "Conversion approach adapted from https://github.com/malaybaku/AnimationClipToVrmaSample (MIT).\r\n" +
                "Controller reference: Assets/MATE ENGINE - Animations/AvatarAnimatorControllerV2 1.controller\r\n" +
                "Mappings: all 19 unique Female Idle BlendTree clips (IdleIndex 0-18), PET_WALK_LEFT/RIGHT, PET_DRAGGING,\r\n" +
                "sit 1 (stable taskbar/window edge sit), PET_DANCING.\r\n" +
                "Not for redistribution or commercial use.\r\n");

            AssetDatabase.Refresh();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
        finally
        {
            if (instance != null && instance.Root != null)
            {
                UnityEngine.Object.DestroyImmediate(instance.Root);
            }
        }
    }

    private static byte[] CreateVrma(Animator animator, AnimationClip clip)
    {
        var data = new ExportingGltfData();
        using var exporter = new VrmAnimationExporter(data, new GltfExportSettings());
        exporter.Prepare(animator.gameObject);

        exporter.Export(animation =>
        {
            var bones = CollectBones(animator);
            var avatarRoot = animator.avatarRoot != null ? animator.avatarRoot : animator.transform;
            animation.SetPositionBoneAndParent(bones[HumanBodyBones.Hips], avatarRoot);
            foreach (var pair in bones)
            {
                var vrmBone = Vrm10HumanoidBoneSpecification.ConvertFromUnityBone(pair.Key);
                var parent = FindParentBone(bones, vrmBone) ?? avatarRoot;
                animation.AddRotationBoneAndParent(pair.Key, pair.Value, parent);
            }

            var frameCount = Math.Max(2, Mathf.CeilToInt(clip.length * ExportFrequency) + 1);
            for (var frame = 0; frame < frameCount; frame++)
            {
                var time = Mathf.Min(clip.length, frame / ExportFrequency);
                clip.SampleAnimation(animator.gameObject, time);
                animation.AddFrame(TimeSpan.FromSeconds(time));
            }
        });

        return data.ToGlbBytes();
    }

    // Adapted from the MIT-licensed AnimationClipToVrmaSample HumanoidBuilder.
    // Exporting an animator-only skeleton keeps Mao's mesh and textures out of VRMA files.
    private static Animator CreateAnimatorOnlyReference(Animator source)
    {
        var sourceBones = new Dictionary<HumanBodyBones, Transform>();
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone == HumanBodyBones.LastBone || bone == HumanBodyBones.Jaw ||
                bone == HumanBodyBones.LeftEye || bone == HumanBodyBones.RightEye)
            {
                continue;
            }

            var sourceBone = source.GetBoneTransform(bone);
            if (sourceBone != null)
            {
                sourceBones.Add(bone, sourceBone);
            }
        }

        var transforms = new Dictionary<HumanBodyBones, Transform>();
        foreach (var pair in sourceBones)
        {
            var boneObject = new GameObject(pair.Key.ToString());
            if (pair.Key == HumanBodyBones.Hips)
            {
                // Keep the source hips transform on the temporary hierarchy.  The
                // HumanDescription still uses a zeroed hips SkeletonBone below,
                // but VrmAnimationExporter reads the live transform prepared here
                // to establish the VRMA's reference height.  Zeroing this value
                // makes three-vrm divide by a zero rest-hips height while
                // retargeting, which poisons every animated transform with NaN.
                boneObject.transform.localPosition = pair.Value.localPosition;
                boneObject.transform.localRotation = pair.Value.localRotation;
            }
            else
            {
                var vrmBone = Vrm10HumanoidBoneSpecification.ConvertFromUnityBone(pair.Key);
                var sourceParent = FindParentBone(sourceBones, vrmBone);
                if (sourceParent == null)
                {
                    throw new InvalidOperationException($"Mapped parent is missing for {pair.Key}.");
                }
                boneObject.transform.localPosition = sourceParent.InverseTransformPoint(pair.Value.position);
                boneObject.transform.localRotation = Quaternion.Inverse(sourceParent.rotation) * pair.Value.rotation;
            }
            transforms.Add(pair.Key, boneObject.transform);
        }

        foreach (var pair in transforms)
        {
            if (pair.Key == HumanBodyBones.Hips)
            {
                continue;
            }

            var vrmBone = Vrm10HumanoidBoneSpecification.ConvertFromUnityBone(pair.Key);
            while (vrmBone != Vrm10HumanoidBones.Hips)
            {
                var parentVrmBone = Vrm10HumanoidBoneSpecification.GetDefine(vrmBone).ParentBone.Value;
                var parentUnityBone = Vrm10HumanoidBoneSpecification.ConvertToUnityBone(parentVrmBone);
                if (transforms.TryGetValue(parentUnityBone, out var parentTransform))
                {
                    pair.Value.SetParent(parentTransform, false);
                    break;
                }
                vrmBone = parentVrmBone;
            }
        }

        var root = new GameObject("root");
        transforms[HumanBodyBones.Hips].SetParent(root.transform, false);
        var skeleton = new List<SkeletonBone>();
        var human = new List<HumanBone>();
        foreach (var pair in transforms)
        {
            skeleton.Add(new SkeletonBone
            {
                name = pair.Value.name,
                position = pair.Key == HumanBodyBones.Hips ? Vector3.zero : pair.Value.localPosition,
                rotation = pair.Key == HumanBodyBones.Hips ? Quaternion.identity : pair.Value.localRotation,
                scale = pair.Value.localScale,
            });
            var humanBone = new HumanBone
            {
                boneName = pair.Value.name,
                humanName = HumanTrait.BoneName[(int)pair.Key],
            };
            humanBone.limit.useDefaultValues = true;
            human.Add(humanBone);
        }

        var description = new HumanDescription
        {
            skeleton = skeleton.ToArray(),
            human = human.ToArray(),
            armStretch = .05f,
            legStretch = .05f,
            upperArmTwist = .5f,
            lowerArmTwist = .5f,
            upperLegTwist = .5f,
            lowerLegTwist = .5f,
            feetSpacing = 0,
            hasTranslationDoF = false,
        };
        var animator = root.AddComponent<Animator>();
        animator.avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
        {
            UnityEngine.Object.DestroyImmediate(root);
            throw new InvalidOperationException("Failed to build the animator-only Humanoid reference.");
        }
        return animator;
    }

    private static Dictionary<HumanBodyBones, Transform> CollectBones(Animator animator)
    {
        var result = new Dictionary<HumanBodyBones, Transform>();
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone == HumanBodyBones.LastBone)
            {
                continue;
            }

            var transform = animator.GetBoneTransform(bone);
            if (transform != null)
            {
                result.Add(bone, transform);
            }
        }

        if (!result.ContainsKey(HumanBodyBones.Hips))
        {
            throw new InvalidOperationException("Humanoid hips mapping is missing.");
        }

        return result;
    }

    private static Transform FindParentBone(
        IReadOnlyDictionary<HumanBodyBones, Transform> bones,
        Vrm10HumanoidBones bone)
    {
        while (bone != Vrm10HumanoidBones.Hips)
        {
            var parent = Vrm10HumanoidBoneSpecification.GetDefine(bone).ParentBone.Value;
            var unityParent = Vrm10HumanoidBoneSpecification.ConvertToUnityBone(parent);
            if (bones.TryGetValue(unityParent, out var transform))
            {
                return transform;
            }

            bone = parent;
        }

        return null;
    }

    private static void Fail(string message)
    {
        Debug.LogError("[MaoMateExport] " + message);
        EditorApplication.Exit(1);
    }
}
