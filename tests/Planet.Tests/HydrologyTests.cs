using System;
using System.Linq;
using Planet.Simulation;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Rivières, lacs et glaciers (étape 7) sur des reliefs synthétiques.</summary>
public static class HydrologyTests
{
    private const double SecondsPerYear = 365.25 * 86400.0;

    private static (PlanetGrid grid, float[] elevation, float[] runoff, HydrologyResult result) Continent(float[] snow = null)
    {
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 10f, -40f, 40f, 11);
        var runoff = new float[grid.CellCount];
        for (int c = 0; c < grid.CellCount; c++)
            runoff[c] = elevation[c] > 0f ? 300f + 200f * MathF.Sin(5f * grid.Positions[c].X) : 0f;
        var result = HydrologyModel.Compute(grid, elevation, runoff, snow ?? new float[grid.CellCount]);
        return (grid, elevation, runoff, result);
    }

    [Test]
    public static void EveryLandCellDrainsToTheSea()
    {
        var (grid, elevation, _, result) = Continent();
        for (int c = 0; c < grid.CellCount; c++)
        {
            if (elevation[c] <= 0f)
                continue;
            int current = c, steps = 0;
            while (elevation[current] > 0f)
            {
                int down = result.Downstream[current];
                Assert.True(down >= 0, $"la cellule {current} n'a pas d'exutoire");
                Assert.True(grid.Neighbors[current].Contains(down), $"l'exutoire de {current} n'est pas une voisine");
                current = down;
                Assert.True(++steps <= grid.CellCount, $"boucle dans l'écoulement depuis {c}");
            }
        }
    }

    [Test]
    public static void FilledSurfaceAlwaysDescendsDownstream()
    {
        var (grid, elevation, _, result) = Continent();
        for (int c = 0; c < grid.CellCount; c++)
        {
            if (elevation[c] <= 0f)
                continue;
            Assert.True(result.FilledElevation[c] >= elevation[c] - 1e-6f, $"surface remplie sous le relief en {c}");
            int down = result.Downstream[c];
            if (elevation[down] > 0f)
                Assert.True(result.FilledElevation[down] < result.FilledElevation[c], $"l'eau remonte de {c} vers {down}");
        }
    }

    [Test]
    public static void DischargeAtMouthsEqualsTotalRunoff()
    {
        // Rien ne se perd ni ne se crée : tout le ruissellement des terres sort par les embouchures
        var (grid, elevation, runoff, result) = Continent();
        double cellArea = 4 * Math.PI * Math.Pow(PlanetConstants.RadiusKm * 1000.0, 2) / grid.CellCount;
        double totalRunoff = 0, atMouths = 0;
        int upstreamAtMouths = 0, landCells = 0;
        for (int c = 0; c < grid.CellCount; c++)
        {
            if (elevation[c] <= 0f)
                continue;
            landCells++;
            totalRunoff += runoff[c] * 1e-3 * cellArea / SecondsPerYear;
            if (elevation[result.Downstream[c]] <= 0f)
            {
                atMouths += result.Discharge[c];
                upstreamAtMouths += result.UpstreamCells[c];
            }
        }
        Assert.Near(atMouths, totalRunoff, totalRunoff * 1e-4, "débit aux embouchures (m³/s)");
        Assert.Equal(landCells, upstreamAtMouths, "cellules drainées par l'ensemble des embouchures");
        Assert.Near(result.LargestDischarge, Enumerable.Range(0, grid.CellCount)
            .Where(c => elevation[c] > 0f && elevation[result.Downstream[c]] <= 0f)
            .Max(c => result.Discharge[c]), 1e-3, "plus grand fleuve");
    }

    [Test]
    public static void ClosedBasinBecomesLake()
    {
        // Plateau à 1 km creusé d'une cuvette à 0,5 km : l'eau la remplit jusqu'au déversoir
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 0f, 0f, 40f, 5);
        var center = new Godot.Vector3(0, 0, 1);
        int pit = grid.NearestCell(center, 0);
        var basin = new[] { pit }.Concat(grid.Neighbors[pit]).ToArray();
        foreach (int c in basin)
            elevation[c] = 0.5f;
        foreach (int c in basin.SelectMany(b => grid.Neighbors[b]).Distinct().Except(basin))
            elevation[c] = Math.Max(elevation[c], 2.6f);
        var runoff = elevation.Select(e => e > 0f ? 2000f : 0f).ToArray();
        var result = HydrologyModel.Compute(grid, elevation, runoff, new float[grid.CellCount]);
        Assert.True(result.Lake[pit], "la cuvette doit contenir un lac");
        Assert.True(result.FilledElevation[pit] >= 2.6f - 1e-3f, $"niveau du lac {result.FilledElevation[pit]:0.00} km (déversoir à 2,6 km)");
        Assert.True(result.LakeCellCount >= basin.Length, "toute la cuvette est en eau");
    }

    [Test]
    public static void DeepSnowFormsGlaciersNotLakesOrRivers()
    {
        var grid = Fixtures.Grid(5);
        var snow = new float[grid.CellCount];
        for (int c = 0; c < grid.CellCount; c++)
            snow[c] = grid.Positions[c].Y > 0.3f ? 3000f : 0f;
        var (_, elevation, _, result) = Continent(snow);
        for (int c = 0; c < grid.CellCount; c++)
        {
            bool expected = elevation[c] > 0f && snow[c] >= AtmosphereModel.GlacierThresholdMm;
            Assert.Equal(expected, result.Glacier[c], $"glacier en {c}");
            if (result.Glacier[c])
                Assert.True(!result.Lake[c] && !result.River[c], $"lac ou rivière dessinés sur le glacier {c}");
        }
        Assert.True(result.GlacierCellCount > 0, "aucun glacier");
    }

    [Test]
    public static void RiversNeedDischargeAndBasin()
    {
        var (grid, _, _, result) = Continent();
        Assert.True(result.RiverCellCount > 0, "aucune rivière sur un continent arrosé");
        for (int c = 0; c < grid.CellCount; c++)
            if (result.River[c])
                Assert.True(result.Discharge[c] > HydrologyModel.RiverThreshold
                            && result.UpstreamCells[c] >= HydrologyModel.RiverMinUpstreamCells, $"rivière trop petite en {c}");
    }
}
