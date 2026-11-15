using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>
/// Grottes en volume (étape 18) : seules les grottes proches de la caméra sont construites (en
/// tâche de fond) et dessinées ; le relief détaillé n'est pas dessiné dans leur ouverture.
/// Éclairage propre : lumière du jour qui entre par l'ouverture et s'éteint vers le fond, et une
/// lampe portée par la caméra (sans elle, le fond d'une grotte est noir).
/// </summary>
public partial class CaveRenderer : Node3D
{
    public const int MaxHoles = 4;
    private const double BuildDistanceM = 1500, DrawDistanceM = 1200;

    private sealed class Entry
    {
        public CaveSite Site;
        public Task<(Cave cave, CaveMesh mesh)> Build;
        public Cave Cave;
        public MeshInstance3D Instance;
        public ShaderMaterial Material;
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<CaveSite, Entry> _built = new();
    private TerrainHeightField _field;
    private Shader _shader;

    /// <summary>Grottes dont le maillage est prêt.</summary>
    public IEnumerable<Cave> ReadyCaves => _built.Values.Where(e => e.Cave != null).Select(e => e.Cave);
    public IReadOnlyList<CaveSite> Sites { get; private set; } = Array.Empty<CaveSite>();

    public override void _Ready() => _shader = new Shader { Code = ShaderCode };

    /// <summary>Nouveaux emplacements, ou nouveau relief : les grottes seront reconstruites.</summary>
    public void SetSites(IReadOnlyList<CaveSite> sites, TerrainHeightField field)
    {
        if (field == _field && sites.SequenceEqual(Sites))
            return;
        bool newField = field != _field;
        _field = field;
        Sites = sites;
        foreach (var (site, entry) in _built.ToArray())
            if (newField || !sites.Contains(site))
            {
                entry.Instance?.QueueFree();
                _built.Remove(site);
            }
    }

    public void Clear() => SetSites(Array.Empty<CaveSite>(), null);

    /// <summary>Construit les grottes proches, place celles qui sont prêtes, rend les ouvertures au relief.</summary>
    public Vector4[] Update(Double3 camera, Vector3 sunDirection, bool lamp)
    {
        var holes = new Vector4[MaxHoles];
        if (_field == null)
            return holes;
        double radius = TerrainHeightField.RadiusMeters;
        Double3 up = camera.Normalized();
        // Construction des grottes à moins de 1,5 km (deux à la fois au plus)
        int building = _built.Values.Count(e => e.Build != null && !e.Build.IsCompleted);
        foreach (var site in Sites.OrderByDescending(s => s.Direction.Dot(up)))
        {
            if ((site.Direction - up).Length() * radius > BuildDistanceM)
                break;
            if (_built.ContainsKey(site) || building >= 2)
                continue;
            var field = _field;
            var entry = new Entry { Site = site };
            entry.Build = Task.Run(() =>
            {
                var hints = new ThreadLocal<int[]>(() => new[] { -1 });
                Func<Double3, double> height = d => field.Sample(d, 0.5, ref hints.Value[0]).Height;
                var cave = site.Kind == CaveKind.Karst
                    ? Cave.Karst(site.Direction, site.Seed, height)
                    : Cave.LavaTube(site.Direction, site.Downhill, site.Seed, height);
                return (cave, cave.BuildMesh());
            });
            _built[site] = entry;
            building++;
        }

        int hole = 0;
        foreach (var entry in _built.Values.OrderBy(e => (e.Site.Direction - up).Length()))
        {
            if (entry.Build != null && entry.Build.IsCompleted)
            {
                if (entry.Build.IsCompletedSuccessfully)
                    Show(entry, entry.Build.Result.cave, entry.Build.Result.mesh);
                else
                    GD.PushWarning($"Grotte : {entry.Build.Exception?.GetBaseException().Message}");
                entry.Build = null;
            }
            if (entry.Cave == null)
                continue;
            double distance = entry.Cave.Origin.DistanceTo(camera);
            bool visible = distance < DrawDistanceM;
            entry.Instance.Visible = visible;
            if (!visible)
                continue;
            entry.Instance.Position = (entry.Cave.Origin - camera).ToVector3();
            float day = Mathf.Clamp((entry.Cave.Up.ToVector3().Dot(sunDirection) + 0.05f) / 0.25f, 0f, 1f);
            entry.Material.SetShaderParameter("daylight", day);
            entry.Material.SetShaderParameter("sun_direction", sunDirection);
            entry.Material.SetShaderParameter("lamp", lamp ? 1f : 0f);
            if (hole < MaxHoles)
            {
                Vector3 center = (entry.Cave.Origin - camera).ToVector3();
                holes[hole++] = new Vector4(center.X, center.Y, center.Z, (float)entry.Cave.HoleRadius - 0.5f);
            }
        }
        return holes;
    }

    /// <summary>Espace libre de la grotte où se trouve ce point (null : hors des grottes).</summary>
    public CaveSpace? SpaceAt(Double3 position)
    {
        foreach (var cave in ReadyCaves)
        {
            if (cave.Origin.DistanceTo(position) > 400)
                continue;
            var space = cave.SpaceAt(position);
            if (space.HasValue)
                return space;
        }
        return null;
    }

    /// <summary>La caméra est-elle sous terre, dans une grotte ?</summary>
    public bool Underground(Double3 position)
    {
        foreach (var cave in ReadyCaves)
        {
            if (cave.Origin.DistanceTo(position) > 400)
                continue;
            Double3 q = cave.ToLocal(position);
            if (cave.Contains(q) && q.Z < cave.GroundZ(q.X, q.Y) - 1)
                return true;
        }
        return false;
    }

    private void Show(Entry entry, Cave cave, CaveMesh mesh)
    {
        entry.Cave = cave;
        if (mesh.Indices.Length == 0)
            return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = mesh.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = mesh.Normals;
        arrays[(int)Mesh.ArrayType.Color] = mesh.Attributes;
        arrays[(int)Mesh.ArrayType.Index] = mesh.Indices;
        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        entry.Material = new ShaderMaterial { Shader = _shader };
        bool karst = cave.Kind == CaveKind.Karst;
        entry.Material.SetShaderParameter("rock_color", karst ? new Color(0.66f, 0.61f, 0.52f) : new Color(0.16f, 0.14f, 0.13f));
        entry.Material.SetShaderParameter("band_color", karst ? new Color(0.55f, 0.42f, 0.30f) : new Color(0.38f, 0.17f, 0.10f));
        entry.Material.SetShaderParameter("wet", karst ? 1f : 0.2f);
        arrayMesh.SurfaceSetMaterial(0, entry.Material);
        entry.Instance = new MeshInstance3D { Mesh = arrayMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        // (grand rayon de visibilité : le maillage est relatif à l'entrée)
        entry.Instance.ExtraCullMargin = 50;
        AddChild(entry.Instance);
    }

    // La caméra est à l'origine (rendu relatif à la caméra) : la lampe est en (0, 0, 0)
    private const string ShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform vec3 rock_color : source_color = vec3(0.66, 0.61, 0.52);
uniform vec3 band_color : source_color = vec3(0.55, 0.42, 0.30);
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
uniform float daylight = 1.0;
uniform float lamp = 1.0;
uniform float wet = 1.0;

varying vec3 v_normal;
varying vec3 v_world;
varying vec4 v_attr;

float hash(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }

void vertex() {
    v_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_attr = COLOR;
}

void fragment() {
    if (v_attr.a < 0.5)
        discard;
    vec3 n = normalize(v_normal);
    vec3 to_camera = -v_world;
    float d = length(to_camera);
    if (dot(n, to_camera) < 0.0) n = -n;
    // Strates de la roche (calcaires en bancs ; basalte oxydé en rouge par endroits)
    float band = 0.5 + 0.5 * sin(v_attr.z * 1.7 + 2.0 * sin(v_attr.z * 0.23));
    vec3 albedo = mix(rock_color, band_color, 0.35 * band);
    albedo *= 0.85 + 0.3 * hash(floor((v_world + vec3(1000.0)) * 2.0) * 0.01);
    // Lumière du jour par l'ouverture, ciel et soleil, éteinte vers le fond
    float open = v_attr.r;
    float sun = max(dot(n, sun_direction), 0.0);
    float day = daylight * open * (0.35 + 0.9 * sun);
    // Lampe : cône dans l'axe du regard, décroissance en 1/d²
    vec3 view_dir = normalize(to_camera);
    float cone = smoothstep(0.55, 0.95, dot(-view_dir, normalize((INV_VIEW_MATRIX * vec4(0.0, 0.0, -1.0, 0.0)).xyz)));
    float torch = lamp * max(dot(n, view_dir), 0.0) * (0.25 + 0.75 * cone) * 60.0 / (d * d + 20.0);
    float light = day + torch + 0.01;
    vec3 color = albedo * light;
    // Parois humides : reflet de la lampe
    float spec = pow(max(dot(n, view_dir), 0.0), 40.0) * torch * 0.4 * wet;
    ALBEDO = color + vec3(spec);
}
";
}
