using Godot;
using Planet.Simulation;

namespace Planet.Rendering;

/// <summary>
/// Repères communs pour dessiner des données « une valeur par cellule » : disposition des
/// textures de cellules (une cellule = un pixel) et hauteur des tracés au-dessus du relief.
/// </summary>
public static class CellColorLayer
{
    /// <summary>Largeur des textures de cellules (pixels) : la cellule n se trouve en (n % largeur, n / largeur).</summary>
    public const int TextureWidth = 1024;

    // Rayon de la planète en km

    /// <summary>
    /// Hauteur (en fraction du rayon) du relief simulé d'une cellule, pour placer des tracés
    /// (flèches, fleuves, vents) au-dessus du sol. La mer compte comme un sol à 0 km.
    /// </summary>
    public static float ReliefOffset(float elevationKm) => Mathf.Max(elevationKm, 0f) / PlanetConstants.RadiusKm;
}
