using Godot;

namespace Planet.Rendering;

/// <summary>
/// Tornade (étape 17) : une colonne d'air qui tourne très vite sous un orage, rendue visible par
/// la condensation (entonnoir gris qui descend de la base du nuage) et par la poussière et les
/// débris aspirés au sol (manchon ocre à la base). Positions relatives à la caméra.
/// </summary>
public partial class TornadoRenderer : Node3D
{
    private MeshInstance3D _funnel;
    private ShaderMaterial _material;

    private const string ShaderCode = @"
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;
uniform float intensity = 1.0;
uniform vec3 light = vec3(1.0);

float wave(vec2 p) {
    return 0.5 + 0.25 * sin(p.x) * sin(p.y) + 0.25 * sin(p.x * 2.3 + p.y * 1.7);
}

void fragment() {
    // UV.x : tour de la colonne ; UV.y : 0 en haut (nuage), 1 en bas (sol)
    float h = UV.y;
    float spin = UV.x * 6.2832 * 6.0 - TIME * 9.0 + h * 14.0;
    float streaks = wave(vec2(spin, h * 40.0 - TIME * 3.0));
    // Plus dense au centre de la colonne (vue de face), bord flou
    float edge = pow(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.7);
    vec3 cloud = vec3(0.42, 0.43, 0.45);
    vec3 dust = vec3(0.55, 0.45, 0.33);
    float bottom = smoothstep(0.75, 1.0, h);
    ALBEDO = mix(cloud, dust, bottom) * light * (0.75 + 0.35 * streaks);
    ALPHA = intensity * edge * (0.45 + 0.4 * streaks) * (0.55 + 0.45 * smoothstep(0.0, 0.15, h)) * (1.0 - 0.3 * smoothstep(0.95, 1.0, h));
}
";

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        // Entonnoir : large sous le nuage, étroit au sol (rayon 1 → mis à l'échelle)
        var mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 0.25f, Height = 1f, RadialSegments = 32, Rings = 16, CapTop = false, CapBottom = false };
        mesh.Material = _material;
        _funnel = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_funnel);
    }

    /// <summary>Montre la tornade : pied au sol (relatif à la caméra), direction verticale, largeur au sol et hauteur (m).</summary>
    public void Show(Vector3 ground, Vector3 up, float widthM, float heightM, float intensity, Color light)
    {
        if (_funnel == null)
            return;
        Vector3 x = up.Cross(Vector3.Right);
        if (x.LengthSquared() < 1e-6f)
            x = up.Cross(Vector3.Forward);
        x = x.Normalized();
        Vector3 z = x.Cross(up);
        float radius = widthM * 2f;   // (rayon du haut : 4 fois la largeur au sol)
        var basis = new Basis(x * radius, up * heightM, z * radius);
        _funnel.Transform = new Transform3D(basis, ground + up * (heightM * 0.5f));
        _funnel.Visible = intensity > 0.01f;
        _material.SetShaderParameter("intensity", intensity);
        _material.SetShaderParameter("light", new Vector3(light.R, light.G, light.B));
    }

    public void HideTornado()
    {
        if (_funnel != null)
            _funnel.Visible = false;
    }
}
