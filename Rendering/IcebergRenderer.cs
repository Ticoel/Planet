using System.Collections.Generic;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>
/// Icebergs (étape 17) : blocs de glace vêlés au front des glaciers qui atteignent la mer, qui
/// dérivent avec le vent et fondent peu à peu. Quatre formes de glace irrégulières, à facettes
/// (bosses, pinacles, flancs creusés), tournées et étirées au hasard ; la masse immergée, plus
/// large, se devine sous la surface. (Avant : une boîte plate qui dépassait de 7 m pour 100 m de
/// long.) Positions relatives à la caméra.
/// </summary>
public partial class IcebergRenderer : Node3D
{
    private const int MaxIcebergs = 80;
    private const int Shapes = 4;
    private readonly MultiMesh[] _meshes = new MultiMesh[Shapes];
    private readonly int[] _counts = new int[Shapes];

    public override void _Ready()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.88f, 0.94f, 0.99f),
            Roughness = 0.55f,
            // (la glace laisse passer un peu de lumière bleutée dans les creux)
            RimEnabled = true, Rim = 0.25f, RimTint = 0.6f,
        };
        for (int k = 0; k < Shapes; k++)
        {
            var mesh = BuildShape(k);
            mesh.SurfaceSetMaterial(0, material);
            _meshes[k] = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = MaxIcebergs, VisibleInstanceCount = 0 };
            AddChild(new MultiMeshInstance3D
            {
                Multimesh = _meshes[k],
                CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
            });
        }
    }

    /// <summary>
    /// Une forme de bloc de glace, de largeur ~1 (ligne de flottaison à y = 0) : sphère déformée
    /// par du bruit, partie émergée de 0,35 à 0,7 de haut selon la forme (avec pinacles), partie
    /// immergée trois fois plus profonde ; facettes nettes (normales par face).
    /// </summary>
    private static ArrayMesh BuildShape(int shape)
    {
        const int rings = 10, segments = 18;
        int seed = 900 + 17 * shape;
        float top = 0.35f + 0.12f * shape;           // hauteur émergée
        float pinnacles = shape % 2 == 1 ? 0.5f : 0.15f;
        var points = new Vector3[rings + 1, segments];
        for (int r = 0; r <= rings; r++)
        {
            float theta = Mathf.Pi * r / rings;      // 0 : sommet, π : fond
            for (int s = 0; s < segments; s++)
            {
                float phi = Mathf.Tau * s / segments;
                var d = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                double n1 = GradientNoise.Sample(d.X * 1.3, d.Y * 1.3, d.Z * 1.3, seed);
                double n2 = GradientNoise.Sample(d.X * 3.1, d.Y * 3.1, d.Z * 3.1, seed + 1);
                float radius = 0.5f * (1f + 0.28f * (float)n1 + 0.12f * (float)n2);
                float y = d.Y > 0
                    ? d.Y * top * (1f + pinnacles * Mathf.Max(0f, (float)n2) * 2f) * radius * 2f
                    : d.Y * 1.5f * radius * 2f;
                // (la masse immergée s'élargit un peu : la glace fond plus vite à l'air)
                float widen = d.Y < 0 ? 1.15f : 1f;
                points[r, s] = new Vector3(d.X * radius * widen, y, d.Z * radius * widen);
            }
        }
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetSmoothGroup(uint.MaxValue);       // facettes nettes
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int s1 = (s + 1) % segments;
                Vector3 a = points[r, s], b = points[r, s1], c = points[r + 1, s], d = points[r + 1, s1];
                // (sens des aiguilles d'une montre vu de dehors : la face avant pour Godot)
                if (r > 0)
                {
                    tool.AddVertex(a); tool.AddVertex(c); tool.AddVertex(b);
                }
                if (r < rings - 1)
                {
                    tool.AddVertex(b); tool.AddVertex(c); tool.AddVertex(d);
                }
            }
        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>Dessine les icebergs : position (direction), taille (m), cap (radians), niveau de la mer (m).</summary>
    public void UpdateIcebergs(Double3 camera, IReadOnlyList<(Double3 direction, float size, float heading)> icebergs, float seaLevel)
    {
        if (_meshes[0] == null)
            return;
        System.Array.Clear(_counts);
        int count = System.Math.Min(icebergs.Count, MaxIcebergs);
        for (int i = 0; i < count; i++)
        {
            var (direction, size, heading) = icebergs[i];
            // (forme et allongement propres à chaque iceberg, tirés de son cap)
            int shape = (int)(heading * 100f) % Shapes;
            float stretch = 0.65f + 0.35f * Mathf.Abs(Mathf.Sin(heading * 7.3f));
            Vector3 up = direction.ToVector3();
            Vector3 east = Vector3.Up.Cross(up);
            east = east.LengthSquared() < 1e-6f ? Vector3.Right : east.Normalized();
            Vector3 north = up.Cross(east);
            Vector3 x = east * Mathf.Cos(heading) + north * Mathf.Sin(heading);
            Vector3 z = x.Cross(up);
            var basis = new Basis(x * size, up * size, z * size * stretch);
            Double3 waterline = direction * (TerrainHeightField.RadiusMeters + seaLevel);
            _meshes[shape].SetInstanceTransform(_counts[shape]++, new Transform3D(basis, (waterline - camera).ToVector3()));
        }
        for (int k = 0; k < Shapes; k++)
            _meshes[k].VisibleInstanceCount = _counts[k];
    }
}
