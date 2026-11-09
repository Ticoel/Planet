using System;
using System.Linq;
using Godot;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Banquise (11e) et saisons de la végétation (12b).</summary>
public static class SeaIceTests
{
    [Test]
    public static void IceDriftsWithTheCurrentAndMeltsInWarmWater()
    {
        // Océan global ; eaux glaciales à l'ouest de la longitude 0, tièdes à l'est ; courant
        // d'ouest en est de 0,2 m/s : la glace part dans le courant et fond en chemin
        var grid = Fixtures.Grid(5);
        var elevation = Enumerable.Repeat(-4f, grid.CellCount).ToArray();
        var temperature = new float[grid.CellCount];
        var current = new Vector3[grid.CellCount];
        int cold = 0, warm = 0;
        for (int c = 0; c < grid.CellCount; c++)
        {
            Vector3 p = grid.Positions[c];
            double lon = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
            temperature[c] = lon < 0 ? -20f : 2f;
            current[c] = Fixtures.East(p) * 0.2f * (Math.Abs(p.Y) < 0.5f ? 1f : 0f);
        }
        var ice = new SeaIce(grid);
        ice.Reset(elevation, temperature);
        double before = ice.Concentration.Where((a, c) => IsEastOfZero(grid, c)).Sum();
        for (int day = 0; day < 20; day++)
            ice.Step(1.0, temperature, current, null);
        // Cellules tièdes juste à l'aval de la limite (0 à 10° E) : glace apportée par le courant
        double downstream = 0, upstream = 0;
        for (int c = 0; c < grid.CellCount; c++)
        {
            Vector3 p = grid.Positions[c];
            if (Math.Abs(p.Y) > 0.3f) continue;
            double lon = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
            if (lon > 1 && lon < 10) { downstream += ice.Concentration[c]; warm++; }
            if (lon < -1 && lon > -10) { upstream += ice.Concentration[c]; cold++; }
        }
        Console.WriteLine($"     glace moyenne près de la limite : amont (froid) {upstream / cold:0.00}, aval (tiède) {downstream / warm:0.00} ; avant : {before:0}");
        Assert.True(downstream / warm > 0.05, "la glace dérive dans le courant au-delà de la limite du gel");
        Assert.True(downstream / warm < upstream / cold, "et fond dans les eaux tièdes");
        Assert.True(ice.Concentration.All(a => a >= 0f && a <= 1f), "concentration entre 0 et 1");
    }

    private static bool IsEastOfZero(Planet.Simulation.PlanetGrid grid, int c) => Math.Atan2(grid.Positions[c].X, grid.Positions[c].Z) > 0;

    [Test]
    public static void WindPushesIceToTheRightInTheNorth()
    {
        // Règle de Nansen : ~2 % du vent, déviée vers la droite au nord, vers la gauche au sud
        var up = new Vector3(0, 0.8f, 0.6f).Normalized();
        var east = Fixtures.East(up);
        Vector3 drift = SeaIce.DriftVelocity(up, Vector3.Zero, east * 10f);
        float rightward = drift.Dot(-Fixtures.North(up));
        Console.WriteLine($"     vent d'ouest de 10 m/s : dérive {drift.Length():0.000} m/s, dont {rightward:0.000} vers le sud");
        Assert.Near(drift.Length(), 0.2, 0.01, "vitesse de dérive (m/s)");
        Assert.True(rightward > 0.05f, "déviée vers la droite au nord");
        var south = new Vector3(0, -0.8f, 0.6f).Normalized();
        Vector3 driftSouth = SeaIce.DriftVelocity(south, Vector3.Zero, Fixtures.East(south) * 10f);
        Assert.True(driftSouth.Dot(Fixtures.North(south)) > 0.05f, "vers la gauche au sud");
    }

    [Test]
    public static void DeciduousForestsTurnAndShedTheirLeaves()
    {
        // Une année de température sinusoïdale (−5 °C en janvier, 22 °C en juillet), sol humide :
        // feuilles vertes l'été, colorées à l'automne, absentes l'hiver, de retour au printemps
        var seasons = new VegetationSeasons(1);
        var soil = new[] { 0.6f };
        float[] TemperatureOn(double day) => new[] { (float)(8.5 - 13.5 * Math.Cos(2 * Math.PI * day / 365)) };
        seasons.Step(0, TemperatureOn(180), soil);
        float summerLeaves = 0, octoberColor = 0, winterLeaves = 1, mayLeaves = 0;
        for (double day = 180; day < 180 + 365 + 150; day += 1)
        {
            seasons.Step(1, TemperatureOn(day), soil);
            int d = (int)(day % 365);
            if (d == 200) summerLeaves = seasons.Leaves[0];
            if (d == 300) octoberColor = MathF.Max(octoberColor, seasons.Autumn[0] * seasons.Leaves[0]);
            if (d == 15) winterLeaves = seasons.Leaves[0];
            if (d == 140) mayLeaves = seasons.Leaves[0];
        }
        Console.WriteLine($"     feuilles : été {summerLeaves:0.00}, couleurs d'automne fin octobre {octoberColor:0.00}, hiver {winterLeaves:0.00}, mai {mayLeaves:0.00}");
        Assert.True(summerLeaves > 0.95f, "feuillage complet l'été");
        Assert.True(octoberColor > 0.3f, "feuillage coloré à l'automne");
        Assert.True(winterLeaves < 0.1f, "branches nues l'hiver");
        Assert.True(mayLeaves > 0.8f, "feuilles revenues au printemps");
    }

    [Test]
    public static void GrassDriesInADrySeason()
    {
        var seasons = new VegetationSeasons(1);
        var warm = new[] { 25f };
        seasons.Step(0, warm, new[] { 0.6f });
        float wet = seasons.Grass[0];
        for (int day = 0; day < 60; day++)
            seasons.Step(1, warm, new[] { 0.05f });
        Console.WriteLine($"     herbe verte : saison humide {wet:0.00}, après 60 jours de sécheresse {seasons.Grass[0]:0.00}");
        Assert.True(wet > 0.9f && seasons.Grass[0] < 0.1f, "l'herbe jaunit quand le sol sèche");
    }
}
