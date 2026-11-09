#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UniGLTF;
using UnityEngine;
using VRM;

public static class MaoCatBindValidation
{
    public static void Inspect()
    {
        string path = Path.GetFullPath("AvatarAssets/hatsune-miku.vrm");
        using var data = new GlbBinaryParser(File.ReadAllBytes(path), path).Parse();
        using var importer = new VRMImporterContext(new VRMData(data));
        var instance = importer.LoadAsync(new ImmediateCaller()).GetAwaiter().GetResult();
        try
        {
            var animator = instance.Root.GetComponentInChildren<Animator>(true);
            var body = instance.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(renderer =>
                renderer.sharedMaterials.Any(material => material != null && material.name.StartsWith("Costume")) &&
                renderer.sharedMaterials.Any(material => material != null && material.name.StartsWith("Hair")));
            Debug.Log("[Mao Cat Bind] root_active=" + instance.Root.activeSelf + " human=" + animator.isHuman +
                " avatar_valid=" + animator.avatar.isValid + " readable=" + body.sharedMesh.isReadable +
                " bones=" + body.bones.Length + " bindposes=" + body.sharedMesh.bindposes.Length);
            foreach (var boneId in new[] { HumanBodyBones.Head, HumanBodyBones.Hips })
            {
                var bone = animator.GetBoneTransform(boneId);
                int index = Array.IndexOf(body.bones, bone);
                Debug.Log("[Mao Cat Bind] bone=" + boneId + " index=" + index + " local_scale=" + bone.localScale +
                    " determinant=" + (index < 0 ? float.NaN : body.sharedMesh.bindposes[index].determinant));
            }
            var accessories = instance.Root.AddComponent<MaoCatAccessories>();
            bool bound = accessories.Bind(body, animator);
            Debug.Log("[Mao Cat Bind] bound=" + bound + " ears=" + accessories.EarsAvailable +
                " tail=" + accessories.TailAvailable + " component_active=" + accessories.isActiveAndEnabled);
        }
        finally { UnityEngine.Object.DestroyImmediate(instance.Root); }
    }
}
#endif
