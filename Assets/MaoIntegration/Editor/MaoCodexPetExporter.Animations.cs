#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using VRM;

public static partial class MaoCodexPetExporter
{
    private static readonly string[] States = { "idle", "running-right", "running-left", "waving", "jumping", "failed", "waiting", "running", "review" };
    private static readonly int[] Counts = { 6, 8, 8, 4, 5, 8, 6, 6, 6 };
    private const float DragLift = 0.14f;

    private static void ExportAnimations(AnimationClip idle)
    {
        bool lookOnly = Environment.GetCommandLineArgs().Contains("--mao-pet-look-only");
        bool travelOnly = Environment.GetCommandLineArgs().Contains("--mao-pet-travel-only");
        if (lookOnly && travelOnly) throw new ArgumentException("Choose either look-only or travel-only.");
        int rendered = 0;
        for (int row = 0; !lookOnly && row < States.Length; row++)
        {
            string state = States[row];
            bool travel = state == "running-right" || state == "running-left";
            if (travelOnly && !travel) continue;
            string folder = Path.Combine(output, "raw", state); Directory.CreateDirectory(folder);
            ChangeClip(idle);
            for (int frame = 0; frame < Counts[row]; frame++)
            {
                float phase = (float)frame / Counts[row];
                Sample(0.2);
                Pose(state, frame, phase);
                Capture(Path.Combine(folder, frame.ToString("00") + ".png"));
                rendered++;
            }
            Debug.Log("[Miku Codex Pet] row_rendered=" + state + " frames=" + Counts[row]);
        }
        ChangeClip(idle);
        string lookFolder = Path.Combine(output, "raw", "look"); Directory.CreateDirectory(lookFolder);
        for (int index = 0; !travelOnly && index < 16; index++)
        {
            float degrees = index * 22.5f; float radians = degrees * Mathf.Deg2Rad;
            Sample(0.2); Face(0, 0.05f, 0);
            Look(Mathf.Sin(radians), Mathf.Cos(radians), true);
            Capture(Path.Combine(lookFolder, degrees.ToString("000.#", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
            rendered++;
        }
        File.WriteAllText(Path.Combine(output, "render-manifest.json"), JsonConvert.SerializeObject(new {
            method = "local-unity-vrm-render", sourcePixels = "actual existing avatar, not AI artwork", width = 384, height = 416,
            states = States.Select((s, index) => new { state = s, frames = Counts[index] }).ToArray(),
            lookDegrees = Enumerable.Range(0, 16).Select(i => i * 22.5).ToArray(),
            originalForward = "+Z", frontYaw = 180, screenRightYaw = 150, screenLeftYaw = 210,
            rootMotion = false, constantCamera = true, constantScale = true, lookOnly, travelOnly,
            dragPose = "suspended-no-steps", dragLiftMetres = DragLift,
            gazeYaw = 55, gazePitch = 40, gazeMechanics = "anchored hips and legs, chest follow, neck and head aim, physical eye bones"
        }, Formatting.Indented));
        Debug.Log("[Miku Codex Pet] all_frames_rendered=" + rendered);
    }

    private static void Pose(string state, int frame, float phase)
    {
        float cycle = Mathf.Sin(phase * Mathf.PI * 2);
        Face(state == "idle" && frame == 2 ? 0.95f : 0, state == "waving" ? 0.5f : 0.08f, state == "failed" ? 0.75f : 0);
        if (state == "running-right" || state == "running-left")
        {
            // User-requested picked-up pose: fixed dangling legs and hands,
            // with only a tiny head/hair sway while the app moves the sprite.
            Rotate(HumanBodyBones.Spine, Vector3.right, -12);
            Rotate(HumanBodyBones.Chest, Vector3.right, -30);
            Rotate(HumanBodyBones.LeftUpperLeg, Vector3.right, 8);
            Rotate(HumanBodyBones.RightUpperLeg, Vector3.right, 12);
            Rotate(HumanBodyBones.LeftLowerLeg, Vector3.right, -18);
            Rotate(HumanBodyBones.RightLowerLeg, Vector3.right, -24);
            Rotate(HumanBodyBones.LeftFoot, Vector3.right, -14);
            Rotate(HumanBodyBones.RightFoot, Vector3.right, -14);
            Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, new Vector3(-0.12f, -1, -0.12f));
            Aim(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, new Vector3(0.12f, -1, -0.12f));
            Aim(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, new Vector3(-0.02f, -1, -0.08f));
            Aim(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, new Vector3(0.02f, -1, -0.08f));
            Rotate(HumanBodyBones.Neck, Vector3.right, 22);
            Rotate(HumanBodyBones.Head, camera.transform.forward, -6 + cycle * 1.2f);
            model.transform.rotation = Quaternion.Euler(0, state == "running-right" ? 150 : 210, 0);
            model.transform.position = Vector3.up * DragLift;
            return;
        }
        Rotate(HumanBodyBones.Chest, camera.transform.forward, cycle * 0.6f);
        if (state == "idle") { Rotate(HumanBodyBones.Head, camera.transform.forward, cycle * 1.1f); return; }
        if (state == "waving")
        {
            float lift = new[] { 0.45f, 1f, 1f, 0.45f }[frame];
            Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, new Vector3(-0.75f, Mathf.Lerp(-0.5f, 0.65f, lift), 0));
            Aim(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, new Vector3(-0.3f + cycle * 0.3f, 1, -0.1f));
            Rotate(HumanBodyBones.RightHand, Vector3.forward, cycle * 18);
            Look(0.12f, 0.15f); return;
        }
        if (state == "jumping")
        {
            float jump = new[] { 0, 0.12f, 0.23f, 0.12f, 0 }[frame];
            model.transform.position += Vector3.up * jump;
            if (frame > 0 && frame < 4) { Rotate(HumanBodyBones.LeftLowerLeg, Vector3.right, 18); Rotate(HumanBodyBones.RightLowerLeg, Vector3.right, 18); }
            Rotate(HumanBodyBones.LeftUpperArm, Vector3.forward, -12); Rotate(HumanBodyBones.RightUpperArm, Vector3.forward, 12);
            Look(0, frame == 2 ? 0.3f : 0.1f); return;
        }
        if (state == "failed")
        {
            Look(cycle * 0.15f, -0.7f); Rotate(HumanBodyBones.Head, camera.transform.forward, -7 + cycle * 2);
            Rotate(HumanBodyBones.Chest, camera.transform.right, 5);
            Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, new Vector3(-0.25f, -1, 0));
            Aim(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, new Vector3(0.25f, -1, 0)); return;
        }
        if (state == "waiting")
        {
            Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, new Vector3(-0.5f, -0.7f, -0.2f));
            Aim(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, new Vector3(0.15f, 0.6f, -0.7f));
            Rotate(HumanBodyBones.RightHand, Vector3.forward, cycle * 8); Look(0.12f + cycle * 0.12f, 0.1f); return;
        }
        if (state == "running")
        {
            Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, new Vector3(-0.5f, -0.8f, -0.2f));
            Aim(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, new Vector3(0.5f, -0.8f, -0.2f));
            Aim(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, new Vector3(0.2f, cycle * 0.18f, -1));
            Aim(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, new Vector3(-0.2f, -cycle * 0.18f, -1));
            Rotate(HumanBodyBones.RightHand, Vector3.right, cycle * 12); Rotate(HumanBodyBones.LeftHand, Vector3.right, -cycle * 12);
            Look(cycle * 0.08f, -0.22f); return;
        }
        Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, new Vector3(-0.4f, -0.9f, -0.1f));
        Aim(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, new Vector3(0.25f, 0.85f, -0.6f));
        Rotate(HumanBodyBones.Head, camera.transform.forward, 5 + cycle * 2);
        Look(cycle * 0.15f, -0.3f);
    }

    private static void Rotate(HumanBodyBones id, Vector3 axis, float degrees)
    { var bone = animator.GetBoneTransform(id); if (bone != null) bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation; }
    private static void Aim(HumanBodyBones parent, HumanBodyBones child, Vector3 direction)
    {
        var bone = animator.GetBoneTransform(parent); var end = animator.GetBoneTransform(child);
        if (bone != null && end != null) bone.rotation = Quaternion.FromToRotation(end.position - bone.position, direction.normalized) * bone.rotation;
    }
    private static void Look(float horizontal, float vertical, bool cursor = false)
    {
        Quaternion delta = Quaternion.Euler(vertical * (cursor ? 40 : 27), -horizontal * (cursor ? 55 : 38), 0);
        if (cursor)
        {
            var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            if (chest != null) chest.rotation = Quaternion.Slerp(Quaternion.identity, delta, 0.15f) * chest.rotation;
        }
        var neck = animator.GetBoneTransform(HumanBodyBones.Neck); var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (neck != null) neck.rotation = Quaternion.Slerp(Quaternion.identity, delta, 0.2f) * neck.rotation;
        head.rotation = Quaternion.Slerp(Quaternion.identity, delta, cursor ? 0.65f : 0.8f) * head.rotation;
        foreach (var eyeId in new[] { HumanBodyBones.LeftEye, HumanBodyBones.RightEye })
        { var eye = animator.GetBoneTransform(eyeId); if (eye != null) eye.rotation = Quaternion.Euler(vertical * 7, -horizontal * 9, 0) * eye.rotation; }
    }
    private static void Face(float blink, float joy, float sorrow)
    {
        var proxy = model.GetComponent<VRMBlendShapeProxy>(); if (proxy == null || proxy.BlendShapeAvatar == null) return;
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++) renderer.SetBlendShapeWeight(i, 0);
        foreach (var clip in proxy.BlendShapeAvatar.Clips)
        {
            float weight = clip.Preset == BlendShapePreset.Blink ? blink : clip.Preset == BlendShapePreset.Joy ? joy : clip.Preset == BlendShapePreset.Sorrow ? sorrow : 0;
            if (weight == 0) continue;
            foreach (var binding in clip.Values)
            {
                var node = string.IsNullOrEmpty(binding.RelativePath) ? proxy.transform : proxy.transform.Find(binding.RelativePath);
                var renderer = node == null ? null : node.GetComponent<SkinnedMeshRenderer>();
                if (renderer != null) renderer.SetBlendShapeWeight(binding.Index, renderer.GetBlendShapeWeight(binding.Index) + binding.Weight * weight);
            }
        }
    }
}
#endif
