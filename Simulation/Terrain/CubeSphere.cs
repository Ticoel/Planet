using System;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Identifiant d'un morceau de terrain dans le quadtree : face du cube (0 à 5), niveau de
/// subdivision (0 = face entière) et position (x, y) parmi les 2^niveau × 2^niveau morceaux de
/// la face.
/// </summary>
public readonly record struct ChunkKey(int Face, int Level, int X, int Y)
{
    /// <summary>Clé compacte pour les dictionnaires (face 3 bits, niveau 5 bits, x et y 24 bits).</summary>
    public long Packed => ((long)Face << 61) | ((long)Level << 56) | ((long)X << 28) | (long)Y;

    public ChunkKey Child(int index) => new(Face, Level + 1, 2 * X + (index & 1), 2 * Y + (index >> 1));
    public ChunkKey Parent => new(Face, Level - 1, X / 2, Y / 2);

    /// <summary>Taille du morceau en coordonnées de face (la face va de −1 à 1).</summary>
    public double FaceSize => 2.0 / (1 << Level);
    public double MinS => -1.0 + X * FaceSize;
    public double MinT => -1.0 + Y * FaceSize;

    // Sans cela, l'affichage automatique d'un record suivrait la propriété Parent à l'infini
    public override string ToString() => $"F{Face} N{Level} ({X}, {Y})";
}

/// <summary>
/// Sphère cubique : chaque face d'un cube est une grille carrée projetée sur la sphère.
/// Projection « à angles égaux » (tangente) : les cellules ont presque toutes la même taille,
/// au lieu d'être deux fois plus petites aux coins du cube qu'en son centre.
/// </summary>
public static class CubeSphere
{
    // Pour chaque face : normale, axe s, axe t (s × t = normale, repère direct)
    private static readonly Double3[] Normals =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };
    private static readonly Double3[] SAxes =
    {
        new(0, 0, -1), new(0, 0, 1), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(-1, 0, 0),
    };
    private static readonly Double3[] TAxes =
    {
        new(0, 1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(0, 1, 0), new(0, 1, 0),
    };

    public const int FaceCount = 6;

    /// <summary>Longueur d'arc d'un morceau de niveau 0 (une face) au centre de la face (m).</summary>
    public static double RootSizeMeters => Math.PI / 2 * TerrainHeightField.RadiusMeters;

    /// <summary>Taille approximative d'un morceau (m).</summary>
    public static double ChunkSizeMeters(int level) => RootSizeMeters / (1 << level);

    /// <summary>Direction (vecteur unitaire) d'un point (s, t) ∈ [−1, 1]² de la face.</summary>
    public static Double3 Direction(int face, double s, double t)
    {
        double a = Math.Tan(s * Math.PI / 4);
        double b = Math.Tan(t * Math.PI / 4);
        return (Normals[face] + SAxes[face] * a + TAxes[face] * b).Normalized();
    }

    /// <summary>Face et coordonnées (s, t) d'une direction (inverse de <see cref="Direction"/>).</summary>
    public static (int face, double s, double t) FromDirection(Double3 direction)
    {
        double ax = Math.Abs(direction.X), ay = Math.Abs(direction.Y), az = Math.Abs(direction.Z);
        int face = ax >= ay && ax >= az ? (direction.X > 0 ? 0 : 1)
                 : ay >= az ? (direction.Y > 0 ? 2 : 3)
                 : (direction.Z > 0 ? 4 : 5);
        double n = direction.Dot(Normals[face]);
        double a = direction.Dot(SAxes[face]) / n;
        double b = direction.Dot(TAxes[face]) / n;
        return (face, Math.Atan(a) * 4 / Math.PI, Math.Atan(b) * 4 / Math.PI);
    }

    /// <summary>Direction du centre d'un morceau.</summary>
    public static Double3 CenterDirection(ChunkKey key) =>
        Direction(key.Face, key.MinS + key.FaceSize / 2, key.MinT + key.FaceSize / 2);
}
