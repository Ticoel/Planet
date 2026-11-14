using System.Collections.Generic;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>
/// Icebergs (étape 17) : blocs de glace vêlés au front des glaciers qui atteignent la mer, qui
/// dérivent avec le vent et les courants et fondent peu à peu. Seul ~1/9 de leur volume dépasse
/// de l'eau (densité de la glace 0,92, de l'eau de mer 1,03). Positions relatives à la caméra.
/// </summary>
public partial class IcebergRenderer : Node3D
{
    private const int MaxIcebergs = 80;
    private MultiMesh _mesh;

    public override void _Ready()
    {
        var box = new BoxMesh { Size = Vector3.One };
        box.Material = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.93f, 0.98f), Roughness = 0.35f, Metallic = 0f };
        _mesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = box, InstanceCount = MaxIcebergs, VisibleInstanceCount = 0 };
        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _mesh,
            CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
        });
    }

    /// <summary>Dessine les icebergs : position (direction), taille (m, côté), cap (radians), niveau de la mer (m).</summary>
    public void UpdateIcebergs(Double3 camera, IReadOnlyList<(Double3 direction, float size, float heading)> icebergs, float seaLevel)
    {
        if (_mesh == null)
            return;
        int count = System.Math.Min(icebergs.Count, MaxIcebergs);
        for (int i = 0; i < count; i++)
        {
            var (direction, size, heading) = icebergs[i];
            Vector3 up = direction.ToVector3();
            Vector3 east = Vector3.Up.Cross(up);
            east = east.LengthSquared() < 1e-6f ? Vector3.Right : east.Normalized();
            Vector3 north = up.Cross(east);
            Vector3 x = east * Mathf.Cos(heading) + north * Mathf.Sin(heading);
            Vector3 z = x.Cross(up);
            // Partie émergée : ~1/9 de la hauteur totale, bloc tabulaire plus large que haut
            float height = size * 0.6f;
            var basis = new Basis(x * size, up * height, z * size * 0.7f);
            Double3 center = direction * (TerrainHeightField.RadiusMeters + seaLevel + height * (0.5f - 8f / 9f));
            _mesh.SetInstanceTransform(i, new Transform3D(basis, (center - camera).ToVector3()));
        }
        _mesh.VisibleInstanceCount = count;
    }
}
