using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

internal static class MaoAppearanceGarment
{
    public static Mesh Build(SkinnedMeshRenderer body, Animator animator)
    {
        Mesh source = body.sharedMesh;
        Vector3[] positions = source.vertices, normals = source.normals;
        Vector2[] uv = source.uv; BoneWeight[] weights = source.boneWeights;
        var roles = new HashSet<Transform>();
        var names = new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm };
        foreach (var role in names) { var bone = animator.GetBoneTransform(role); if (bone != null) roles.Add(bone); }
        Transform[] bones = body.bones;
        var allowed = new bool[positions.Length];
        for (int i = 0; i < allowed.Length; i++)
        {
            BoneWeight w = weights[i]; float score = 0f;
            if (w.boneIndex0 < bones.Length && roles.Contains(bones[w.boneIndex0])) score += w.weight0;
            if (w.boneIndex1 < bones.Length && roles.Contains(bones[w.boneIndex1])) score += w.weight1;
            if (w.boneIndex2 < bones.Length && roles.Contains(bones[w.boneIndex2])) score += w.weight2;
            if (w.boneIndex3 < bones.Length && roles.Contains(bones[w.boneIndex3])) score += w.weight3;
            allowed[i] = score > 0.72f;
        }
        var vertices = new List<Vector3>(); var ns = new List<Vector3>(); var uvs = new List<Vector2>();
        var ws = new List<BoneWeight>(); var triangles = new List<int>(); var map = new Dictionary<int, int>();
        Material[] materials = body.sharedMaterials;
        for (int sub = 0; sub < source.subMeshCount && sub < materials.Length; sub++)
        {
            if (materials[sub] == null || !(materials[sub].name.StartsWith("Body") || materials[sub].name.StartsWith("Costume"))) continue;
            int[] indices = source.GetTriangles(sub);
            for (int j = 0; j < indices.Length; j += 3)
            {
                if (!allowed[indices[j]] || !allowed[indices[j + 1]] || !allowed[indices[j + 2]]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int old = indices[j + k];
                    if (!map.TryGetValue(old, out int index))
                    {
                        index = vertices.Count; map.Add(old, index);
                        vertices.Add(positions[old] + normals[old] * 0.009f); ns.Add(normals[old]);
                        uvs.Add(uv.Length > old ? uv[old] : Vector2.zero); ws.Add(weights[old]);
                    }
                    triangles.Add(index);
                }
            }
        }
        if (triangles.Count == 0) return null;
        var mesh = new Mesh { name = "Tomaru fitted jacket", indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(vertices); mesh.SetNormals(ns); mesh.SetUVs(0, uvs); mesh.boneWeights = ws.ToArray();
        mesh.bindposes = source.bindposes; mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
        return mesh;
    }
}
