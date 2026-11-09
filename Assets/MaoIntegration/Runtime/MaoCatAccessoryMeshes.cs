using System.Collections.Generic;
using UnityEngine;

internal static class MaoCatAccessoryMeshes
{
    public static Mesh BuildEars(Matrix4x4 headFrame)
    {
        var vertices = new List<Vector3>(54);
        var outer = new List<int>(48);
        var inner = new List<int>(6);
        AddEar(-1f, headFrame, vertices, outer, inner);
        AddEar(1f, headFrame, vertices, outer, inner);
        var mesh = new Mesh { name = "Miku original low-poly cat ears", subMeshCount = 2 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(outer, 0); mesh.SetTriangles(inner, 1);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddEar(float side, Matrix4x4 frame, List<Vector3> vertices, List<int> outer, List<int> inner)
    {
        Vector3 center = new Vector3(side * 0.082f, 0.21f, -0.005f);
        Vector3 a = center + new Vector3(-0.042f, 0f, 0.024f);
        Vector3 b = center + new Vector3(0.042f, 0f, 0.024f);
        Vector3 c = center + new Vector3(side * 0.013f, 0.12f, 0.010f);
        Vector3 d = a + Vector3.back * 0.042f;
        Vector3 e = b + Vector3.back * 0.042f;
        Vector3 f = c + Vector3.back * 0.024f;
        Triangle(a, b, c, frame, vertices, outer);
        Triangle(e, d, f, frame, vertices, outer);
        Triangle(d, a, c, frame, vertices, outer); Triangle(d, c, f, frame, vertices, outer);
        Triangle(b, e, f, frame, vertices, outer); Triangle(b, f, c, frame, vertices, outer);
        Triangle(d, e, b, frame, vertices, outer); Triangle(d, b, a, frame, vertices, outer);
        // Front follows the outer triangle's sloped plane, offset 1 mm to avoid z-fighting.
        Triangle(center + new Vector3(-0.027f, 0.018f, 0.0229f),
            center + new Vector3(0.027f, 0.018f, 0.0229f),
            center + new Vector3(side * 0.010f, 0.095f, 0.0139f), frame, vertices, inner);
    }

    private static void Triangle(Vector3 a, Vector3 b, Vector3 c, Matrix4x4 frame,
        List<Vector3> vertices, List<int> triangles)
    {
        int start = vertices.Count;
        vertices.Add(frame.MultiplyVector(a)); vertices.Add(frame.MultiplyVector(b)); vertices.Add(frame.MultiplyVector(c));
        triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
    }

    public static Mesh BuildTail(Matrix4x4 hipsFrame)
    {
        const int segments = 16, sides = 8;
        int ringVertices = (segments + 1) * sides;
        var vertices = new Vector3[ringVertices + 2];
        var triangles = new int[segments * sides * 6 + sides * 6];
        Vector3 p0 = Vector3.zero, p1 = new Vector3(0f, -0.02f, -0.19f),
            p2 = new Vector3(0.28f, -0.72f, -0.26f), p3 = new Vector3(0.31f, -0.38f, -0.14f);
        for (int ring = 0; ring <= segments; ring++)
        {
            float t = ring / (float)segments, u = 1f - t;
            Vector3 point = u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
            Vector3 tangent = (3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2)).normalized;
            Vector3 normal = Vector3.Cross(tangent, Vector3.right).normalized;
            Vector3 right = Vector3.Cross(normal, tangent).normalized;
            float radius = ring == segments ? 0.0015f : 0.027f * (1f - 0.70f * t);
            for (int edge = 0; edge < sides; edge++)
            {
                float angle = edge * Mathf.PI * 2f / sides;
                Vector3 offset = (right * Mathf.Cos(angle) + normal * Mathf.Sin(angle)) * radius;
                vertices[ring * sides + edge] = hipsFrame.MultiplyVector(point + offset);
            }
        }
        vertices[ringVertices] = hipsFrame.MultiplyVector(p0);
        vertices[ringVertices + 1] = hipsFrame.MultiplyVector(p3);
        int index = 0;
        for (int ring = 0; ring < segments; ring++)
            for (int edge = 0; edge < sides; edge++)
            {
                int a = ring * sides + edge, b = ring * sides + (edge + 1) % sides,
                    c = a + sides, d = b + sides;
                triangles[index++] = a; triangles[index++] = b; triangles[index++] = c;
                triangles[index++] = b; triangles[index++] = d; triangles[index++] = c;
            }
        for (int edge = 0; edge < sides; edge++)
        {
            int next = (edge + 1) % sides;
            triangles[index++] = ringVertices; triangles[index++] = next; triangles[index++] = edge;
            triangles[index++] = ringVertices + 1; triangles[index++] = segments * sides + edge;
            triangles[index++] = segments * sides + next;
        }
        var mesh = new Mesh { name = "Miku original low-poly cat tail", vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }
}
