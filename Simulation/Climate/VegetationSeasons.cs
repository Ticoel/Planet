using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>
/// Saisons de la végétation (étape 12b), pour les couleurs du sol :
/// <list type="bullet">
/// <item><b>Feuillus</b> : quand la température moyenne des 10 derniers jours passe sous ~9 °C
/// (ou que le sol s'assèche, pour les forêts tropicales sèches), les feuilles jaunissent et
/// rougissent en ~2 semaines, puis tombent en ~1 mois ; elles repoussent, vertes, quand il refait plus de
/// 10 °C et que le sol est humide. L'écart entre les deux seuils évite que les forêts
/// clignotent d'une couleur à l'autre au gré de quelques jours doux.</item>
/// <item><b>Herbes</b> : elles verdissent quand le sol est humide et qu'il fait doux, jaunissent
/// en paille quand le sol sèche (été sec, saison sèche des savanes) ou en hiver.</item>
/// </list>
/// Les parts de forêt, de feuillus et d'herbe dépendent du biome.
/// </summary>
public sealed class VegetationSeasons
{
    private const float TemperatureMemoryDays = 10f;
    private const float AutumnCelsius = 9f;
    private const float SpringCelsius = 10f;
    private const float ColoringDays = 12f;
    private const float LeafFallDays = 15f;
    private const float LeafGrowthDays = 12f;
    private const float GrassDays = 15f;
    // Remplissage du sol (0 à 1) sous lequel les arbres perdent leurs feuilles (sécheresse), et
    // au-dessus duquel elles repoussent. (Avec 0,15 et 0,25, les savanes, au sol toujours assez
    // sec, gardaient des arbres nus toute l'année.)
    private const float DroughtSoil = 0.06f;
    private const float WetSoil = 0.12f;

    private readonly float[] _meanTemperature;
    private bool _initialised;

    /// <summary>Feuilles présentes sur les feuillus (0 : branches nues, 1 : feuillage complet).</summary>
    public float[] Leaves { get; }
    /// <summary>Couleur d'automne du feuillage (0 : vert, 1 : jaune-rouge).</summary>
    public float[] Autumn { get; }
    /// <summary>Herbe verte (0 : paille, 1 : verte).</summary>
    public float[] Grass { get; }

    public VegetationSeasons(int cellCount)
    {
        _meanTemperature = new float[cellCount];
        Leaves = new float[cellCount];
        Autumn = new float[cellCount];
        Grass = new float[cellCount];
    }

    /// <summary>Part de forêt (couvert des arbres) d'un biome.</summary>
    public static float ForestShare(Biome biome) => biome switch
    {
        Biome.TropicalRainforest => 1f,
        Biome.TemperateRainforest => 0.95f,
        Biome.TemperateForest => 0.85f,
        Biome.Taiga => 0.75f,
        Biome.TropicalSeasonalForest => 0.7f,
        Biome.Savanna => 0.2f,
        Biome.Steppe => 0.03f,
        _ => 0f,
    };

    /// <summary>Part des arbres qui perdent leurs feuilles.</summary>
    public static float DeciduousShare(Biome biome) => biome switch
    {
        Biome.TemperateForest => 0.8f,
        Biome.TropicalSeasonalForest => 0.6f,
        Biome.Savanna => 0.5f,
        Biome.Taiga => 0.15f,           // mélèzes, bouleaux
        Biome.TemperateRainforest => 0.1f,
        _ => 0f,
    };

    /// <summary>Part du sol couverte d'herbes (le reste : arbres, sol nu).</summary>
    public static float GrassShare(Biome biome) => biome switch
    {
        Biome.Steppe => 0.9f,
        Biome.Savanna => 0.8f,
        Biome.Tundra => 0.6f,
        Biome.TropicalSeasonalForest => 0.3f,
        Biome.TemperateForest => 0.15f,
        Biome.ColdDesert or Biome.HotDesert => 0.1f,
        Biome.Taiga => 0.1f,
        _ => 0f,
    };

    /// <summary>Avance de dtDays ; soilFraction : remplissage du sol en eau (0 à 1).</summary>
    public void Step(double dtDays, float[] surfaceCelsius, float[] soilFraction)
    {
        int count = Leaves.Length;
        float dt = (float)dtDays;
        if (!_initialised)
        {
            // Départ : végétation en accord avec le temps qu'il fait
            for (int c = 0; c < count; c++)
            {
                _meanTemperature[c] = surfaceCelsius[c];
                Leaves[c] = surfaceCelsius[c] > AutumnCelsius && soilFraction[c] > DroughtSoil ? 1f : 0f;
                Autumn[c] = 0f;
                Grass[c] = GrassTarget(surfaceCelsius[c], soilFraction[c]);
            }
            _initialised = true;
            return;
        }
        float memory = Rate(dt, TemperatureMemoryDays);
        float coloring = Rate(dt, ColoringDays), fall = Rate(dt, LeafFallDays), growth = Rate(dt, LeafGrowthDays);
        float grass = Rate(dt, GrassDays), greening = Rate(dt, 5f);
        for (int c = 0; c < count; c++)
        {
            float t = _meanTemperature[c] += (surfaceCelsius[c] - _meanTemperature[c]) * memory;
            float soil = soilFraction[c];
            bool autumn = t < AutumnCelsius || soil < DroughtSoil;
            bool spring = t > SpringCelsius && soil > WetSoil;
            if (autumn && Leaves[c] > 0f)
            {
                // D'abord les couleurs, puis la chute
                Autumn[c] += (1f - Autumn[c]) * coloring;
                if (Autumn[c] > 0.8f)
                    Leaves[c] -= Leaves[c] * fall;
            }
            else if (spring)
            {
                Leaves[c] += (1f - Leaves[c]) * growth;
                Autumn[c] -= Autumn[c] * greening;
            }
            Grass[c] += (GrassTarget(t, soil) - Grass[c]) * grass;
        }
    }

    private static float GrassTarget(float celsius, float soilFraction) =>
        Smooth(0.1f, 0.4f, soilFraction) * Smooth(0f, 8f, celsius);

    private static float Rate(float dtDays, float timeConstantDays) => 1f - MathF.Exp(-dtDays / timeConstantDays);

    private static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
