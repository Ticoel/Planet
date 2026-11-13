using System;
using System.Collections.Generic;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>
/// Blocs et poussière des mouvements de terrain (étape 16a) : un bloc de roche par paquet de la
/// simulation (<see cref="Landslide"/>), qui roule sur la masse en mouvement, et des bouffées de
/// poussière soulevées là où ça va vite, qui s'élèvent, gonflent, dérivent avec le vent puis
/// retombent. Positions relatives à la caméra (comme le terrain).
/// </summary>
public partial class DebrisRenderer : Node3D
{
    private const int MaxBoulders = 2000;
    private const int MaxPuffs = 700;
    private const float PuffSeconds = 7f;

    private MultiMeshInstance3D _boulders, _dust;
    private MultiMesh _boulderMesh, _dustMesh;
    private StandardMaterial3D _dustMaterial;
    private readonly Random _random = new(5);

    private struct Puff
    {
        public Double3 Position;
        public Vector3 Velocity;
        public float Age, Size, Growth;
    }

    private readonly List<Puff> _puffs = new();

    public override void _Ready()
    {
        // Bloc : sphère à facettes, aplatie et tournée au hasard (allure de roche anguleuse)
        var rock = new SphereMesh { Radius = 0.5f, Height = 0.8f, RadialSegments = 7, Rings = 4 };
        rock.Material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.42f, 0.39f, 0.35f),
            Roughness = 1f,
        };
        _boulderMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = rock, InstanceCount = MaxBoulders, VisibleInstanceCount = 0 };
        _boulders = new MultiMeshInstance3D { Multimesh = _boulderMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        AddChild(_boulders);

        _dustMaterial = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,   // (sinon toutes les bouffées feraient 1 m)
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = PuffTexture(),
            Roughness = 1f,
            DisableReceiveShadows = true,
        };
        var quad = new QuadMesh { Size = Vector2.One, Material = _dustMaterial };
        _dustMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = quad,
            InstanceCount = MaxPuffs, VisibleInstanceCount = 0,
        };
        _dust = new MultiMeshInstance3D { Multimesh = _dustMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_dust);
        // (positions relatives à la caméra : la boîte englobante doit tout couvrir)
        var everywhere = new Aabb(new Vector3(-1e5f, -1e5f, -1e5f), new Vector3(2e5f, 2e5f, 2e5f));
        _boulders.CustomAabb = everywhere;
        _dust.CustomAabb = everywhere;
    }

    /// <summary>Tache ronde floue (alpha) pour les bouffées de poussière.</summary>
    private static ImageTexture PuffTexture()
    {
        const int size = 64;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp(1f - r, 0f, 1f);
                image.SetPixel(x, y, new Color(1, 1, 1, a * a * (3 - 2 * a)));
            }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Met à jour les blocs et la poussière.
    /// </summary>
    /// <param name="camera">Position de la caméra (m, repère de la planète).</param>
    /// <param name="particles">Paquets en mouvement de tous les glissements en cours.</param>
    /// <param name="wind">Vent au sol (m/s, repère de la planète).</param>
    /// <param name="light">Couleur de la lumière du moment (la poussière la prend).</param>
    /// <param name="dustColor">Teinte de la poussière (sol du lieu).</param>
    public void UpdateDebris(double delta, Double3 camera, IReadOnlyList<DebrisParticle> particles, Vector3 wind, Color light, Color dustColor)
    {
        double planet = TerrainHeightField.RadiusMeters;
        int count = Math.Min(particles.Count, MaxBoulders);
        float time = (float)(Time.GetTicksMsec() / 1000.0);
        for (int i = 0; i < count; i++)
        {
            var p = particles[i];
            Double3 up = p.Direction;
            Double3 position = up * (planet + p.Height + p.Size * 0.3);
            // Orientation propre à chaque bloc, qui tourne en roulant
            float seed = p.Id * 12.9898f;
            var axis = new Vector3(Mathf.Sin(seed), Mathf.Cos(seed * 1.3f), Mathf.Sin(seed * 0.7f)).Normalized();
            float roll = seed + time * p.Speed / Math.Max(0.3f, p.Size);
            var basis = new Basis(axis, roll).Scaled(new Vector3(p.Size, p.Size * (0.7f + 0.3f * Mathf.Abs(Mathf.Sin(seed))), p.Size * 1.2f));
            _boulderMesh.SetInstanceTransform(i, new Transform3D(basis, (position - camera).ToVector3()));

            // Poussière : là où ça va vite, d'autant plus que le bloc est gros
            if (p.Speed > 2f && _puffs.Count < MaxPuffs && _random.NextDouble() < delta * Math.Min(3.0, p.Speed * 0.15) * (count > 400 ? 400.0 / count : 1))
            {
                _puffs.Add(new Puff
                {
                    Position = position,
                    Velocity = up.ToVector3() * (0.6f + 0.6f * (float)_random.NextDouble()),
                    Size = p.Size * 2.5f + 2f,
                    Growth = 1.5f + p.Speed * 0.15f,
                });
            }
        }
        _boulderMesh.VisibleInstanceCount = count;

        // Bouffées : montent, gonflent, dérivent avec le vent, s'effacent
        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            var puff = _puffs[i];
            puff.Age += (float)delta;
            if (puff.Age > PuffSeconds)
            {
                _puffs.RemoveAt(i);
                continue;
            }
            puff.Velocity = puff.Velocity * (1f - 0.4f * (float)delta) + wind * (0.5f * (float)delta);
            puff.Position += Double3.From(puff.Velocity) * delta;
            puff.Size += puff.Growth * (float)delta;
            _puffs[i] = puff;
        }
        for (int i = 0; i < _puffs.Count; i++)
        {
            var puff = _puffs[i];
            float life = puff.Age / PuffSeconds;
            float alpha = 0.55f * Mathf.SmoothStep(0f, 0.08f, life) * (1f - Mathf.SmoothStep(0.4f, 1f, life));
            _dustMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * puff.Size), (puff.Position - camera).ToVector3()));
            var c = dustColor * light;
            _dustMesh.SetInstanceColor(i, new Color(c.R, c.G, c.B, alpha));
        }
        _dustMesh.VisibleInstanceCount = _puffs.Count;
    }

    /// <summary>Plus rien en mouvement : on efface tout (nouvelle planète).</summary>
    public void Clear()
    {
        _puffs.Clear();
        if (_boulderMesh != null)
        {
            _boulderMesh.VisibleInstanceCount = 0;
            _dustMesh.VisibleInstanceCount = 0;
        }
    }

    public int PuffCount => _puffs.Count;
}
