using System;
using System.Linq;
using Godot;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Marées (11b) et niveau de la mer (11d).</summary>
public static class TideTests
{
    private static readonly OrbitalParameters Earth = new();

    [Test]
    public static void EquilibriumAmplitudesMatchTheTextbook()
    {
        // K = 3/2 (m/M)(R/d)³ R : ~0,54 m pour la Lune, ~0,25 m pour le Soleil
        double moon = Tides.EquilibriumAmplitude(0.0123, 384400);
        double sun = Tides.EquilibriumAmplitude(332946, 149597870.7);
        Console.WriteLine($"     amplitude d'équilibre : Lune {moon:0.000} m, Soleil {sun:0.000} m, rapport {sun / moon:0.00}");
        Assert.Near(moon, 0.535, 0.01, "Lune (m)");
        Assert.Near(sun / moon, 0.46, 0.01, "Soleil / Lune");
    }

    [Test]
    public static void TwoHighTidesPerLunarDay()
    {
        // En un point fixe : une marée haute toutes les ~12 h 25
        var place = new Vector3(0.7f, 0.2f, 0.68f).Normalized();
        var highs = new System.Collections.Generic.List<double>();
        double step = 1.0 / 1440.0 * 5;   // 5 min
        float before = 0, previous = 0;
        for (double day = 10; day < 25; day += step)
        {
            float height = Tides.Compute(day, Earth).HeightAt(place);
            if (day > 10 + 2 * step && previous > before && previous > height)
                highs.Add(day - step);
            before = previous;
            previous = height;
        }
        double meanHours = (highs[^1] - highs[0]) / (highs.Count - 1) * 24;
        Console.WriteLine($"     {highs.Count} marées hautes en 15 jours, intervalle moyen {Math.Floor(meanHours)} h {(meanHours % 1) * 60:00} min");
        Assert.Near(meanHours, 12.42, 0.1, "intervalle entre marées hautes (h)");
    }

    [Test]
    public static void SpringTidesAreLargerThanNeapTides()
    {
        // Marnage quotidien à l'équateur au fil d'un mois : vives-eaux à la pleine et à la
        // nouvelle lune, mortes-eaux aux quartiers ; rapport attendu (K_L + K_S) / (K_L − K_S) ≈ 2,7
        var place = new Vector3(1, 0, 0);
        double maxRange = 0, minRange = double.MaxValue;
        for (int day = 0; day < 30; day++)
        {
            float high = float.MinValue, low = float.MaxValue;
            for (int k = 0; k < 150; k++)
            {
                float h = Tides.Compute(day + k / 150.0, Earth).HeightAt(place);
                high = MathF.Max(high, h);
                low = MathF.Min(low, h);
            }
            maxRange = Math.Max(maxRange, high - low);
            minRange = Math.Min(minRange, high - low);
        }
        Console.WriteLine($"     marnage à l'équateur : vives-eaux {maxRange:0.00} m, mortes-eaux {minRange:0.00} m");
        Assert.InRange(maxRange / minRange, 2.0, 3.6, "vives-eaux / mortes-eaux");
        Assert.InRange(maxRange, 1.8, 3.0, "marnage de vives-eaux (m, amplification côtière comprise)");
    }

    [Test]
    public static void TidesConserveWater()
    {
        // Le terme −1/3 : autant d'eau dans les bourrelets que de creux ailleurs
        var grid = Fixtures.Grid(5);
        var areas = grid.ComputeCellAreas();
        var tide = Tides.Compute(3.7, Earth);
        double mean = Fixtures.AreaSum(areas, c => tide.HeightAt(grid.Positions[c])) / areas.Sum();
        Assert.Near(mean, 0, 0.002, "niveau moyen (m)");
    }

    [Test]
    public static void LandIceLowersTheSea()
    {
        // 10 m d'eau en glace sur un tiers de la surface (les terres) → la mer baisse de 5 m
        var grid = Fixtures.Grid(4);
        var areas = grid.ComputeCellAreas();
        var elevation = new float[grid.CellCount];
        var snow = new float[grid.CellCount];
        double land = 0, total = areas.Sum();
        for (int c = 0; c < grid.CellCount; c++)
        {
            bool isLand = grid.Positions[c].Y > 1f / 3f;   // calotte : 1/3 de la sphère
            elevation[c] = isLand ? 1f : -3f;
            snow[c] = isLand ? 10000f : 0f;
            if (isLand) land += areas[c];
        }
        float level = SeaLevel.FromLandIce(snow, elevation, areas);
        double expected = -10.0 * land / (total - land);
        Console.WriteLine($"     terres {land / total * 100:0} %, mer {level:0.00} m (attendu {expected:0.00} m)");
        Assert.Near(level, expected, 0.01, "niveau de la mer (m)");
    }
}
