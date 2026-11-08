using System;
using System.Collections.Generic;
using System.Linq;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;

namespace Planet.Tests;

/// <summary>
/// Chaîne complète, comme dans le jeu : 400 Ma de tectonique avec érosion pilotée par le climat
/// estimé, puis plusieurs années de climat sur le relief figé (processeur, sans carte graphique).
/// </summary>
public static class IntegrationTests
{
    private static readonly Lazy<(TectonicPlates tectonics, ClimateSimulation climate, float[] yearlyMeans)> Simulation = new(() =>
    {
        var grid = Fixtures.Grid(5);
        var orbit = new OrbitalParameters();
        var tectonics = TectonicPlates.Generate(grid, 14, 1);
        double nextEstimate = 0;
        while (tectonics.TimeMyr < 400)
        {
            if (tectonics.TimeMyr >= nextEstimate)
            {
                tectonics.AnnualPrecipitation = FastClimateEstimator.Estimate(grid, tectonics.CellElevation, orbit).AnnualPrecipitation;
                nextEstimate += 25;
            }
            tectonics.Step(tectonics.MaxTimeStep());
        }

        var climate = new ClimateSimulation(grid, orbit, useGpu: false, startDay: 79.5);
        climate.SetSurface(tectonics.CellElevation);
        climate.WaitForHydrology();
        const int years = 6;
        var means = new float[years];
        for (int year = 0; year < years; year++)
        {
            while (climate.Statistics.CompletedYears < year + 1)
                climate.Step(1.0);
            means[year] = climate.Statistics.MeanTemperature.Average();
        }
        climate.WaitForHydrology();
        return (tectonics, climate, means);
    });

    [Test(Slow = true)]
    public static void ClimateIsStableAndEarthLike()
    {
        var (_, climate, means) = Simulation.Value;
        Console.WriteLine($"     moyennes annuelles : {string.Join(" ; ", means.Select(m => m.ToString("0.00")))} °C");
        Assert.InRange(means[^1], 10, 18, "température moyenne annuelle (°C)");
        Assert.True(Math.Abs(means[^1] - means[^2]) < 0.5, $"le climat dérive encore : {means[^2]:0.00} → {means[^1]:0.00} °C");
        double precipitation = climate.Statistics.AnnualPrecipitation.Average() / 365.25;
        Assert.InRange(precipitation, 2, 4.5, "précipitations mondiales (mm/jour)");
        Assert.Finite(climate.SurfaceTemperature, "températures");
    }

    [Test(Slow = true)]
    public static void LatitudeGradientIsRealistic()
    {
        var (tectonics, climate, _) = Simulation.Value;
        var grid = climate.Grid;
        double Band(float lo, float hi) => Enumerable.Range(0, grid.CellCount)
            .Where(c => tectonics.CellElevation[c] <= 0f)
            .Where(c => Math.Abs(Fixtures.LatitudeDeg(grid.Positions[c])) is var lat && lat >= lo && lat < hi)
            .Average(c => climate.Statistics.MeanTemperature[c]);
        double tropics = Band(0, 20), mid = Band(35, 55), polar = Band(70, 90);
        Console.WriteLine($"     océan : tropiques {tropics:0.0} °C, moyennes latitudes {mid:0.0} °C, pôles {polar:0.0} °C");
        Assert.InRange(tropics, 20, 32, "océan tropical (°C)");
        Assert.True(tropics > mid + 5 && mid > polar + 5, "la température doit baisser nettement vers les pôles");
        Assert.InRange(polar, -30, 5, "océan polaire (°C)");
    }

    [Test(Slow = true)]
    public static void BiomesAndRiversAreDiverse()
    {
        var (tectonics, climate, _) = Simulation.Value;
        var land = Enumerable.Range(0, climate.Grid.CellCount).Where(c => tectonics.CellElevation[c] > 0f).ToArray();
        var shares = land.GroupBy(c => climate.Biomes[c]).ToDictionary(g => g.Key, g => g.Count() / (double)land.Length);
        Console.WriteLine("     " + string.Join(", ", shares.OrderByDescending(s => s.Value).Select(s => $"{Biomes.Name(s.Key)} {s.Value:P0}")));
        Assert.True(shares.Count >= 8, $"seulement {shares.Count} biomes différents");
        Assert.True(shares.Values.Max() < 0.4, "un biome couvre plus de 40 % des terres");
        Assert.True(!shares.ContainsKey(Biome.Ocean), "biome océan sur une terre");
        Assert.True(shares.GetValueOrDefault(Biome.IceSheet) < 0.15, "trop de calottes glaciaires");

        var hydrology = climate.Hydrology;
        Assert.True(hydrology != null, "réseau hydrographique absent");
        Assert.True(hydrology.RiverCellCount > 50, $"seulement {hydrology.RiverCellCount} cellules de rivière");
        Assert.InRange(hydrology.LargestDischarge, 2e4, 1e6, "débit du plus grand fleuve (m³/s ; Amazone ≈ 2e5)");
        Assert.True(hydrology.LakeCellCount < 0.05 * land.Length, "trop de lacs");
    }

    [Test(Slow = true)]
    public static void RainBeltsFollowCirculation()
    {
        // ZCIT pluvieuse, déserts subtropicaux, rail des dépressions aux moyennes latitudes
        var (_, climate, _) = Simulation.Value;
        var grid = climate.Grid;
        double Band(float lo, float hi) => Enumerable.Range(0, grid.CellCount)
            .Where(c => Math.Abs(Fixtures.LatitudeDeg(grid.Positions[c])) is var lat && lat >= lo && lat < hi)
            .Average(c => climate.Statistics.AnnualPrecipitation[c]);
        double equator = Band(0, 8), subtropics = Band(20, 32), midLatitudes = Band(45, 62);
        Console.WriteLine($"     pluie : équateur {equator:0} mm, subtropiques {subtropics:0} mm, moyennes latitudes {midLatitudes:0} mm");
        Assert.True(equator > 2 * subtropics, "l'équateur doit être bien plus pluvieux que les subtropiques");
        Assert.True(midLatitudes > subtropics, "les moyennes latitudes doivent être plus pluvieuses que les subtropiques");
    }

    [Test(Slow = true)]
    public static void EveryDisplayLayerGivesValidColors()
    {
        var (tectonics, climate, _) = Simulation.Value;
        foreach (var mode in Enum.GetValues<DisplayMode>())
        {
            var colors = PlanetColorizer.Compute(tectonics, climate, mode);
            Assert.Equal(climate.Grid.CellCount, colors.Length, $"couleurs de la couche {mode}");
            foreach (var color in colors)
                Assert.True(float.IsFinite(color.R) && color.R is >= 0 and <= 1 && color.G is >= 0 and <= 1 && color.B is >= 0 and <= 1,
                            $"couleur invalide dans la couche {mode} : {color}");
        }
    }
}
