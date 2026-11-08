using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>Grands types de milieux naturels.</summary>
public enum Biome : byte
{
    Ocean,
    IceSheet,
    Tundra,
    Taiga,
    ColdDesert,
    Steppe,
    TemperateForest,
    TemperateRainforest,
    HotDesert,
    Savanna,
    TropicalSeasonalForest,
    TropicalRainforest,
}

/// <summary>
/// Biomes (étape 8), déduits du climat de chaque cellule selon une version simplifiée de la
/// classification de Köppen :
/// <list type="bullet">
/// <item>calotte de glace si la neige ne fond plus ou si même le mois le plus chaud gèle ;</item>
/// <item>toundra si le mois le plus chaud reste sous 10 °C (pas d'arbres) ;</item>
/// <item>désert ou steppe si les précipitations sont sous le seuil d'aridité de Köppen
/// (20 × T + 280 mm : plus il fait chaud, plus il faut de pluie, l'évaporation étant forte) ;</item>
/// <item>tropical si le mois le plus froid dépasse 18 °C : forêt dense, forêt saisonnière ou
/// savane selon la pluie ;</item>
/// <item>sinon forêts tempérées, ou taïga si l'hiver est rude.</item>
/// </list>
/// Chaque biome agit en retour sur le climat : albédo (désert clair, forêt sombre, et une forêt
/// enneigée reste sombre car les arbres dépassent de la neige), réserve
/// d'eau du sol (racines profondes des forêts) et évapotranspiration (les forêts recyclent la
/// pluie vers l'atmosphère).
/// </summary>
public static class Biomes
{
    private readonly record struct Properties(string Name, Color Color, float Albedo, float SoilCapacityMm, float Evapotranspiration, float SnowAlbedo);

    // Albédos planétaires (nuages compris), d'où de faibles écarts entre biomes
    private static readonly Properties[] Table =
    {
        new("Océan", new Color(0.1f, 0.25f, 0.5f), AtmosphereModel.OceanAlbedo, 0f, 0f, 0.00f),
        new("Calotte glaciaire", new Color(0.95f, 0.96f, 0.98f), 0.33f, 50f, 0.5f, 0.60f),
        new("Toundra", new Color(0.55f, 0.58f, 0.48f), 0.33f, 80f, 0.8f, 0.50f),
        new("Taïga", new Color(0.14f, 0.33f, 0.24f), 0.29f, 150f, 1.2f, 0.36f),
        new("Désert froid", new Color(0.68f, 0.63f, 0.53f), 0.36f, 50f, 0.6f, 0.50f),
        new("Steppe", new Color(0.76f, 0.72f, 0.45f), 0.33f, 100f, 1.0f, 0.48f),
        new("Forêt tempérée", new Color(0.22f, 0.5f, 0.2f), 0.30f, 200f, 1.4f, 0.40f),
        new("Forêt tempérée humide", new Color(0.1f, 0.4f, 0.25f), 0.29f, 250f, 1.5f, 0.38f),
        new("Désert chaud", new Color(0.9f, 0.78f, 0.5f), 0.37f, 40f, 0.6f, 0.50f),
        new("Savane", new Color(0.72f, 0.68f, 0.3f), 0.32f, 120f, 1.1f, 0.46f),
        new("Forêt tropicale saisonnière", new Color(0.32f, 0.55f, 0.16f), 0.30f, 220f, 1.4f, 0.40f),
        new("Forêt tropicale humide", new Color(0.05f, 0.38f, 0.1f), 0.28f, 300f, 1.6f, 0.38f),
    };

    public static string Name(Biome biome) => Table[(int)biome].Name;
    public static Color ColorOf(Biome biome) => Table[(int)biome].Color;
    public static int Count => Table.Length;

    public static Biome Classify(bool ocean, float meanTemperature, float coldestMonth, float warmestMonth,
                                 float annualPrecipitationMm, float maxSnowMm)
    {
        if (ocean)
            return Biome.Ocean;
        if (maxSnowMm >= AtmosphereModel.GlacierThresholdMm || warmestMonth < 0f)
            return Biome.IceSheet;
        if (warmestMonth < 10f)
            return Biome.Tundra;

        float aridity = 20f * meanTemperature + 280f;
        bool hot = meanTemperature >= 18f;
        if (annualPrecipitationMm < 0.5f * aridity)
            return hot ? Biome.HotDesert : Biome.ColdDesert;
        if (annualPrecipitationMm < aridity)
            return hot ? Biome.Savanna : Biome.Steppe;

        if (coldestMonth >= 18f)
        {
            if (annualPrecipitationMm >= 1800f) return Biome.TropicalRainforest;
            if (annualPrecipitationMm >= 1000f) return Biome.TropicalSeasonalForest;
            return Biome.Savanna;
        }
        if (coldestMonth < -3f && meanTemperature < 2f)
            return Biome.Taiga;
        return annualPrecipitationMm > 2000f ? Biome.TemperateRainforest : Biome.TemperateForest;
    }

    /// <summary>Classe toutes les cellules et calcule les propriétés de surface qui en découlent.</summary>
    public static void ClassifyAll(AtmosphereModel model, ClimateStatistics statistics, Biome[] biomes,
                                   float[] albedo, float[] soilCapacity, float[] evapotranspiration, float[] snowAlbedo)
    {
        for (int cell = 0; cell < biomes.Length; cell++)
        {
            var biome = Classify(model.IsOcean(cell), statistics.MeanTemperature[cell],
                statistics.ColdestMonth[cell], statistics.WarmestMonth[cell],
                statistics.AnnualPrecipitation[cell], statistics.MaxSnow[cell]);
            biomes[cell] = biome;
            var properties = Table[(int)biome];
            albedo[cell] = properties.Albedo;
            soilCapacity[cell] = properties.SoilCapacityMm;
            evapotranspiration[cell] = properties.Evapotranspiration;
            snowAlbedo[cell] = properties.SnowAlbedo;
        }
    }
}
