using System;
using Godot;
using Planet.Simulation;

namespace Planet.Tests;

/// <summary>Grilles et outils partagés par les tests (créés une seule fois).</summary>
public static class Fixtures
{
    private static readonly Lazy<PlanetGrid>[] Grids =
    {
        new(() => PlanetGrid.Create(0)), new(() => PlanetGrid.Create(1)), new(() => PlanetGrid.Create(2)),
        new(() => PlanetGrid.Create(3)), new(() => PlanetGrid.Create(4)), new(() => PlanetGrid.Create(5)),
    };

    public static PlanetGrid Grid(int subdivisions) => Grids[subdivisions].Value;

    public static float LatitudeDeg(Vector3 position) =>
        Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(position.Y, -1f, 1f)));

    /// <summary>Direction de l'est (tangente), comme dans le modèle de vent.</summary>
    public static Vector3 East(Vector3 position)
    {
        Vector3 east = Vector3.Up.Cross(position);
        return east.LengthSquared() < 1e-8f ? Vector3.Right : east.Normalized();
    }

    public static Vector3 North(Vector3 position) => position.Cross(East(position)).Normalized();

    /// <summary>Somme pondérée par l'aire des cellules.</summary>
    public static double AreaSum(float[] areas, Func<int, double> value)
    {
        double sum = 0;
        for (int cell = 0; cell < areas.Length; cell++)
            sum += areas[cell] * value(cell);
        return sum;
    }

    /// <summary>
    /// Relief synthétique : un continent en forme de dôme centré sur (lat, lon), bruité de façon
    /// déterministe, avec des cuvettes ; le reste en océan profond.
    /// </summary>
    public static float[] DomeContinent(PlanetGrid grid, float centerLatDeg, float centerLonDeg, float radiusDeg, int seed)
    {
        float lat = Mathf.DegToRad(centerLatDeg), lon = Mathf.DegToRad(centerLonDeg);
        var center = new Vector3(Mathf.Cos(lat) * Mathf.Sin(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Cos(lon));
        float radius = Mathf.DegToRad(radiusDeg);
        var random = new Random(seed);
        var elevation = new float[grid.CellCount];
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            float distance = grid.Positions[cell].AngleTo(center);
            float dome = 1f - distance / radius;
            elevation[cell] = dome > 0f
                ? 0.05f + 2.5f * dome + 0.3f * (float)(random.NextDouble() - 0.5)
                : -4f;
        }
        return elevation;
    }
}
