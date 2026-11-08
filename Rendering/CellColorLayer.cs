using System;
using Godot;

namespace Planet.Rendering;

/// <summary>
/// Données d'affichage des cellules, stockées dans deux textures (une cellule = un pixel) lues
/// par le shader de la planète :
/// <list type="bullet">
/// <item>la couleur, selon la couche affichée ;</item>
/// <item>l'altitude en km (float 32 bits), qui sert au shader à soulever le relief.</item>
/// </list>
/// Mettre à jour l'affichage revient à renvoyer deux petites images à la carte graphique :
/// moins de 2 Mo même pour 163 842 cellules. Le relief est entièrement calculé par la carte
/// graphique, il ne coûte rien au processeur.
/// </summary>
public sealed class CellColorLayer
{
    public const int TextureWidth = 1024;

    // Rayon de la planète en km : le shader convertit les altitudes en fraction du rayon
    private const float PlanetRadiusKm = 6371f;

    private const string ShaderCode = @"
shader_type spatial;
render_mode cull_disabled;

// Une cellule par pixel, lecture exacte sans filtrage
uniform sampler2D cell_colors : source_color, filter_nearest;
uniform sampler2D cell_elevation : filter_nearest;
uniform int texture_width = 1024;
// Altitude (km) → déplacement en fraction du rayon, exagération comprise
uniform float relief_scale = 0.0;

ivec2 texel_of(float id) {
    int cell = int(id + 0.5);
    return ivec2(cell % texture_width, cell / texture_width);
}

float elevation_of(float id) {
    return texelFetch(cell_elevation, texel_of(id), 0).r;
}

void vertex() {
    if (relief_scale > 0.0) {
        // Moyenne des trois cellules partagées par ce sommet (voir PlanetMeshBuilder)
        float h = (elevation_of(UV.y) + elevation_of(UV2.x) + elevation_of(UV2.y)) / 3.0;
        // Les océans restent à la surface de l'eau : seules les terres sont soulevées
        VERTEX += NORMAL * max(h, 0.0) * relief_scale;
    }
}

void fragment() {
    ALBEDO = texelFetch(cell_colors, texel_of(UV.x), 0).rgb;
    ROUGHNESS = 1.0;
    if (relief_scale > 0.0) {
        // Normale de la facette, recalculée après déplacement, pour que les reliefs
        // soient éclairés d'un côté et dans l'ombre de l'autre
        vec3 facet = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
        NORMAL = dot(facet, VIEW) < 0.0 ? -facet : facet;
    }
}
";

    private readonly int _height;
    private readonly byte[] _colorPixels;
    private readonly byte[] _elevationPixels;
    private readonly float[] _elevationValues;
    private readonly Image _colorImage;
    private readonly Image _elevationImage;
    private readonly ImageTexture _colorTexture;
    private readonly ImageTexture _elevationTexture;

    public ShaderMaterial Material { get; }

    public CellColorLayer(int cellCount)
    {
        _height = (cellCount + TextureWidth - 1) / TextureWidth;
        int pixelCount = TextureWidth * _height;

        _colorPixels = new byte[pixelCount * 4];
        _colorImage = Image.CreateFromData(TextureWidth, _height, false, Image.Format.Rgba8, _colorPixels);
        _colorTexture = ImageTexture.CreateFromImage(_colorImage);

        _elevationValues = new float[pixelCount];
        _elevationPixels = new byte[pixelCount * sizeof(float)];
        _elevationImage = Image.CreateFromData(TextureWidth, _height, false, Image.Format.Rf, _elevationPixels);
        _elevationTexture = ImageTexture.CreateFromImage(_elevationImage);

        Material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        Material.SetShaderParameter("cell_colors", _colorTexture);
        Material.SetShaderParameter("cell_elevation", _elevationTexture);
        Material.SetShaderParameter("texture_width", TextureWidth);
    }

    /// <summary>Exagération du relief : 1 = échelle réelle, 0 = globe lisse.</summary>
    public void SetReliefExaggeration(float exaggeration)
    {
        Material.SetShaderParameter("relief_scale", exaggeration / PlanetRadiusKm);
    }

    /// <summary>Déplacement (en fraction du rayon) d'un point d'altitude donnée, pour placer
    /// d'autres objets (flèches) au-dessus du relief.</summary>
    public static float ReliefOffset(float elevationKm, float exaggeration) =>
        Mathf.Max(elevationKm, 0f) * exaggeration / PlanetRadiusKm;

    public void UpdateColors(Color[] cellColors)
    {
        for (int cell = 0; cell < cellColors.Length; cell++)
        {
            Color c = cellColors[cell];
            int offset = cell * 4;
            _colorPixels[offset] = (byte)c.R8;
            _colorPixels[offset + 1] = (byte)c.G8;
            _colorPixels[offset + 2] = (byte)c.B8;
            _colorPixels[offset + 3] = 255;
        }
        _colorImage.SetData(TextureWidth, _height, false, Image.Format.Rgba8, _colorPixels);
        _colorTexture.Update(_colorImage);
    }

    public void UpdateElevation(float[] elevationKm)
    {
        Array.Copy(elevationKm, _elevationValues, elevationKm.Length);
        Buffer.BlockCopy(_elevationValues, 0, _elevationPixels, 0, _elevationPixels.Length);
        _elevationImage.SetData(TextureWidth, _height, false, Image.Format.Rf, _elevationPixels);
        _elevationTexture.Update(_elevationImage);
    }
}
