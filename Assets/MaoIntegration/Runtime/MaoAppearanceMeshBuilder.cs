using System;
using System.Collections.Generic;
using MaoIntegration;
using UnityEngine;

internal static class MaoAppearanceMeshBuilder
{
    public static Mesh Build(SkinnedMeshRenderer renderer, Transform head, int hair, int face)
    {
        Mesh original = renderer.sharedMesh;
        Mesh mesh = UnityEngine.Object.Instantiate(original);
        mesh.name = original.name + " - Appearance";
        Vector3[] vertices = original.vertices;
        Vector3[] sourceVertices = (Vector3[])vertices.Clone();
        BoneWeight[] weights = original.boneWeights;
        Transform[] bones = renderer.bones;
        Matrix4x4[] binds = original.bindposes;
        int headIndex = Array.IndexOf(bones, head);
        Vector3 headPoint = headIndex >= 0 ? binds[headIndex].inverse.MultiplyPoint3x4(Vector3.zero) : Vector3.zero;
        bool[] skin = MaterialVertices(renderer, "Body");
        bool[] hidden = new bool[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            int dominant = DominantBone(weights[i]);
            if (dominant >= 0 && dominant < bones.Length && bones[dominant] != null &&
                bones[dominant].name.IndexOf("TwinTail", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (hair == 2 && bones[dominant].name.EndsWith(".L", StringComparison.Ordinal)) hidden[i] = true;
                if (hair == 1)
                {
                    int root = dominant;
                    Transform parent = bones[dominant].parent;
                    while (parent != null && parent.name.IndexOf("TwinTail", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int index = Array.IndexOf(bones, parent);
                        if (index >= 0) root = index;
                        parent = parent.parent;
                    }
                    Vector3 anchor = binds[root].inverse.MultiplyPoint3x4(Vector3.zero);
                    vertices[i] = MaoAppearanceGeometry.ShortenTail(vertices[i], anchor, 0.42f);
                    weights[i] = new BoneWeight { boneIndex0 = root, weight0 = 1f };
                }
            }
            if (face != 0 && headIndex >= 0 && skin[i])
                vertices[i] = MaoAppearanceGeometry.ShapeFace(vertices[i], headPoint, face);
        }
        mesh.vertices = vertices;
        mesh.boneWeights = weights;
        if (hair == 2)
        {
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                int[] indices = original.GetTriangles(sub);
                var keep = new List<int>(indices.Length);
                for (int i = 0; i < indices.Length; i += 3)
                    if (!hidden[indices[i]] && !hidden[indices[i + 1]] && !hidden[indices[i + 2]])
                    { keep.Add(indices[i]); keep.Add(indices[i + 1]); keep.Add(indices[i + 2]); }
                mesh.SetTriangles(keep, sub, false);
            }
        }
        if (face != 0 && headIndex >= 0)
        {
            mesh.ClearBlendShapes();
            var dv = new Vector3[vertices.Length]; var dn = new Vector3[vertices.Length]; var dt = new Vector3[vertices.Length];
            for (int shape = 0; shape < original.blendShapeCount; shape++)
                for (int frame = 0; frame < original.GetBlendShapeFrameCount(shape); frame++)
                {
                    original.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                    for (int i = 0; i < dv.Length; i++)
                        if (skin[i]) dv[i] = MaoAppearanceGeometry.ShapeFace(sourceVertices[i] + dv[i], headPoint, face) -
                                            MaoAppearanceGeometry.ShapeFace(sourceVertices[i], headPoint, face);
                    mesh.AddBlendShapeFrame(original.GetBlendShapeName(shape), original.GetBlendShapeFrameWeight(shape, frame), dv, dn, dt);
                }
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    private static int DominantBone(BoneWeight w)
    {
        int index = w.boneIndex0; float weight = w.weight0;
        if (w.weight1 > weight) { index = w.boneIndex1; weight = w.weight1; }
        if (w.weight2 > weight) { index = w.boneIndex2; weight = w.weight2; }
        if (w.weight3 > weight) index = w.boneIndex3;
        return index;
    }

    private static bool[] MaterialVertices(SkinnedMeshRenderer renderer, string name)
    {
        var result = new bool[renderer.sharedMesh.vertexCount];
        var materials = renderer.sharedMaterials;
        for (int sub = 0; sub < materials.Length && sub < renderer.sharedMesh.subMeshCount; sub++)
            if (materials[sub] != null && materials[sub].name.StartsWith(name, StringComparison.Ordinal))
                foreach (int vertex in renderer.sharedMesh.GetTriangles(sub)) result[vertex] = true;
        return result;
    }
}
