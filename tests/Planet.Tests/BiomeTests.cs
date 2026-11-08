using System;
using System.Linq;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Classification des biomes (étape 8) et statistiques annuelles.</summary>
public static class BiomeTests
{
    // (océan, T moyenne, mois le plus froid, mois le plus chaud, pluie mm/an, neige max mm) → biome
    private static readonly (bool ocean, float mean, float cold, float warm, float rain, float snow, Biome expected)[] Cases =
    {
        (true, 15, 10, 20, 1000, 0, Biome.Ocean),
        (false, -20, -35, -2, 200, 500, Biome.IceSheet),        // même l'été gèle
        (false, 5, 0, 10, 2000, 2500, Biome.IceSheet),          // neige qui ne fond plus
        (false, -8, -25, 6, 300, 200, Biome.Tundra),            // été sous 10 °C
        (false, 25, 15, 33, 100, 0, Biome.HotDesert),           // Sahara
        (false, 8, -5, 22, 120, 30, Biome.ColdDesert),          // Gobi
        (false, 10, -8, 25, 450, 50, Biome.Steppe),             // seuil d'aridité 480 mm
        (false, 26, 22, 29, 700, 0, Biome.Savanna),             // trop sec pour la forêt
        (false, 26, 24, 28, 1300, 0, Biome.TropicalSeasonalForest),
        (false, 27, 25, 28, 2600, 0, Biome.TropicalRainforest), // Amazonie
        (false, -2, -18, 16, 500, 150, Biome.Taiga),
        (false, 11, 2, 20, 800, 10, Biome.TemperateForest),
        (false, 10, 4, 16, 2500, 0, Biome.TemperateRainforest),
    };

    [Test]
    public static void KoppenCases()
    {
        foreach (var (ocean, mean, cold, warm, rain, snow, expected) in Cases)
        {
            var biome = Biomes.Classify(ocean, mean, cold, warm, rain, snow);
            Assert.Equal(expected, biome, $"T {mean} °C ({cold} à {warm}), {rain} mm, neige {snow} mm");
        }
    }

    [Test]
    public static void EveryBiomeHasNameColorAndProperties()
    {
        Assert.Equal(Enum.GetValues<Biome>().Length, Biomes.Count, "taille de la table des biomes");
        foreach (var biome in Enum.GetValues<Biome>())
        {
            Assert.True(!string.IsNullOrWhiteSpace(Biomes.Name(biome)), $"nom manquant pour {biome}");
            var color = Biomes.ColorOf(biome);
            Assert.True(color.R is >= 0 and <= 1 && color.G is >= 0 and <= 1 && color.B is >= 0 and <= 1, $"couleur invalide pour {biome}");
        }
        Assert.True(Enum.GetValues<Biome>().Select(Biomes.Name).Distinct().Count() == Biomes.Count, "noms en double");
    }

    [Test]
    public static void ForestsMaskSnowAndHoldMoreWater()
    {
        // ClassifyAll sur trois cellules : forêt boréale, toundra, désert chaud
        var grid = Fixtures.Grid(0);   // 12 cellules suffisent
        var model = new AtmosphereModel(grid);
        model.SetSurface(Enumerable.Repeat(0.2f, grid.CellCount).ToArray());
        var statistics = new ClimateStatistics(grid.CellCount, 365.25f);
        int n = grid.CellCount;
        var estimate = new FastClimateEstimate
        {
            MeanTemperature = new float[n], ColdestMonth = new float[n], WarmestMonth = new float[n],
            AnnualPrecipitation = new float[n], AnnualRunoff = new float[n], AnnualSnowfall = new float[n], Snow = new float[n],
        };
        void Set(int c, float mean, float cold, float warm, float rain)
        {
            estimate.MeanTemperature[c] = mean; estimate.ColdestMonth[c] = cold;
            estimate.WarmestMonth[c] = warm; estimate.AnnualPrecipitation[c] = rain;
        }
        for (int c = 0; c < n; c++) Set(c, 11, 2, 20, 800);
        Set(0, -2, -18, 16, 500);   // taïga
        Set(1, -8, -25, 6, 300);    // toundra
        Set(2, 25, 15, 33, 100);    // désert chaud
        statistics.Reset(estimate);

        var biomes = new Biome[n];
        var albedo = new float[n]; var soil = new float[n]; var et = new float[n]; var snowAlbedo = new float[n];
        Biomes.ClassifyAll(model, statistics, biomes, albedo, soil, et, snowAlbedo);
        Assert.Equal(Biome.Taiga, biomes[0], "cellule 0");
        Assert.Equal(Biome.Tundra, biomes[1], "cellule 1");
        Assert.Equal(Biome.HotDesert, biomes[2], "cellule 2");
        Assert.True(snowAlbedo[0] < snowAlbedo[1] - 0.1f, "une forêt enneigée doit rester plus sombre que la toundra enneigée");
        Assert.True(albedo[2] > albedo[0], "le désert doit être plus clair que la forêt");
        Assert.True(soil[0] > soil[2] && et[0] > et[2], "la forêt retient et recycle plus d'eau que le désert");
    }

    [Test]
    public static void StatisticsRecoverASyntheticYear()
    {
        // Température sinusoïdale (10 ± 15 °C), 2 mm de pluie par jour, neige max 40 mm
        const float yearLength = 365.25f;
        var statistics = new ClimateStatistics(1, yearLength);
        var temperature = new float[1];
        var state = new float[4];
        var accumulation = new float[4];
        int completed = 0;
        double time = 0;
        for (int day = 0; day < 366; day++)
        {
            double mid = time + 0.5;
            temperature[0] = 10f + 15f * MathF.Sin(2f * MathF.PI * (float)(mid / yearLength));
            state[3] = 40f * MathF.Max(0f, -MathF.Sin(2f * MathF.PI * (float)(mid / yearLength)));
            accumulation[0] = 2f;
            accumulation[2] = 0.5f;
            statistics.Add(temperature, state, accumulation, 1.0, mid);
            if (statistics.YearJustCompleted) completed++;
            time += 1.0;
        }
        Assert.Equal(1, completed, "années bouclées");
        Assert.Equal(1, statistics.CompletedYears, "compteur d'années");
        Assert.Near(statistics.MeanTemperature[0], 10, 0.3, "température moyenne (°C)");
        // Moyenne mensuelle autour du creux : entre 10 − 15 × 0,99 et 10 − 15 × 0,95 selon le calage des mois
        Assert.Near(statistics.ColdestMonth[0], -4.6, 0.8, "mois le plus froid (°C)");
        Assert.Near(statistics.WarmestMonth[0], 24.6, 0.8, "mois le plus chaud (°C)");
        Assert.Near(statistics.AnnualPrecipitation[0], 2 * yearLength, 2, "précipitations annuelles (mm)");
        Assert.Near(statistics.AnnualRunoff[0], 0.5 * yearLength, 1, "ruissellement annuel (mm)");
        Assert.Near(statistics.MaxSnow[0], 40, 0.5, "neige maximale (mm)");
    }
}
