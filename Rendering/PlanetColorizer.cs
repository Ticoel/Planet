using Godot;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using BiomeTable = Planet.Simulation.Climate.Biomes;

namespace Planet.Rendering;

/// <summary>Modes d'affichage de la planète (couches de visualisation).</summary>
public enum DisplayMode
{
    /// <summary>Couleurs naturelles du terrain (calculées par le shader du terrain).</summary>
    Geology,
    Elevation,
    Plates,
    Boundaries,
    CrustAge,
    InstantInsolation,
    DailyInsolation,
    Temperature,
    WindSpeed,
    Pressure,
    RecentPrecipitation,
    AnnualPrecipitation,
    Humidity,
    Biomes,
    Realistic,
    /// <summary>Courants océaniques de surface (étape 11a).</summary>
    OceanCurrents,
    /// <summary>Remontées d'eau froide (étape 11c).</summary>
    Upwelling,
    /// <summary>Milieux hydrothermaux (étape 14a) : couleurs calculées par le jeu (Main.Habitats).</summary>
    VolcanicHabitats,
    /// <summary>Chimie (étape 16b) : couleurs calculées par le jeu (Main.Chemistry).</summary>
    ChemCarbon,
    ChemNitrogen,
    ChemPhosphorus,
    ChemSulfur,
    ChemIron,
}

/// <summary>Calcule la couleur de chaque cellule selon le mode d'affichage.</summary>
public static class PlanetColorizer
{
    private static readonly Color ConvergentColor = new(0.95f, 0.15f, 0.1f);
    private static readonly Color DivergentColor = new(0.1f, 0.6f, 1f);
    private static readonly Color TransformColor = new(1f, 0.85f, 0.1f);

    // Vitesse relative (cm/an) à partir de laquelle une frontière est affichée à pleine intensité
    private const float FullIntensityRate = 10f;

    // Croûte continentale dans les modes où seule la croûte océanique est colorée
    private static readonly Color ContinentNeutralColor = new(0.78f, 0.72f, 0.6f);

    // Palette d'altitude (km), façon carte physique : abysses bleu nuit, plateau continental
    // bleu clair, plaines vertes, plateaux ocre, hautes montagnes brunes puis enneigées
    private static readonly (float value, Color color)[] ElevationGradient =
    {
        (-10f, new Color(0.02f, 0.03f, 0.15f)),
        (-6f, new Color(0.05f, 0.1f, 0.35f)),
        (-3f, new Color(0.1f, 0.3f, 0.6f)),
        (-0.5f, new Color(0.3f, 0.6f, 0.85f)),
        (0f, new Color(0.55f, 0.8f, 0.9f)),
        (0.01f, new Color(0.2f, 0.48f, 0.22f)),
        (0.3f, new Color(0.35f, 0.6f, 0.28f)),
        (0.7f, new Color(0.6f, 0.68f, 0.35f)),
        (1.2f, new Color(0.78f, 0.7f, 0.42f)),
        (2f, new Color(0.7f, 0.52f, 0.3f)),
        (3f, new Color(0.52f, 0.36f, 0.22f)),
        (4.2f, new Color(0.55f, 0.5f, 0.47f)),
        (5.5f, new Color(0.97f, 0.97f, 1f)),
    };

    // Palette de l'âge de la croûte, façon cartes géologiques du plancher océanique :
    // rouge = toute jeune, puis jaune, vert, bleu, et bleu nuit pour la croûte ancienne
    private static readonly (float value, Color color)[] AgeGradient =
    {
        (0f, new Color(0.95f, 0.15f, 0.1f)),
        (15f, new Color(1f, 0.75f, 0.1f)),
        (40f, new Color(0.35f, 0.8f, 0.3f)),
        (90f, new Color(0.15f, 0.45f, 0.85f)),
        (TectonicPlates.InitialCrustAgeMyr, new Color(0.08f, 0.1f, 0.3f)),
    };

    // Palette de l'ensoleillement (W/m², ramené à [0, 1]) : nuit bleu nuit, puis violet,
    // rouge, orange, jaune et blanc sous le Soleil au zénith
    private static readonly (float value, Color color)[] InsolationGradient =
    {
        (0f, new Color(0.02f, 0.02f, 0.08f)),
        (0.08f, new Color(0.15f, 0.08f, 0.35f)),
        (0.25f, new Color(0.55f, 0.12f, 0.45f)),
        (0.45f, new Color(0.9f, 0.3f, 0.2f)),
        (0.7f, new Color(1f, 0.65f, 0.15f)),
        (0.9f, new Color(1f, 0.92f, 0.5f)),
        (1f, new Color(1f, 1f, 0.95f)),
    };

    // Palette des températures de surface (°C) : violet glacial, bleus, blanc au gel, puis vert,
    // jaune, orange et rouge brûlant
    private static readonly (float value, Color color)[] TemperatureGradient =
    {
        (-50f, new Color(0.35f, 0f, 0.45f)),
        (-30f, new Color(0.2f, 0.2f, 0.75f)),
        (-10f, new Color(0.45f, 0.7f, 1f)),
        (0f, new Color(0.95f, 0.97f, 1f)),
        (10f, new Color(0.45f, 0.8f, 0.4f)),
        (20f, new Color(1f, 0.9f, 0.3f)),
        (30f, new Color(1f, 0.5f, 0.1f)),
        (40f, new Color(0.75f, 0.05f, 0.05f)),
    };

    // Vitesse du vent (m/s) : calme bleu sombre, puis cyan, vert, jaune, rouge pour la tempête
    // Courants (m/s) : eaux calmes bleu nuit, courants forts jaunes puis rouges
    // Remontées (m/jour) : plongée en rouge, remontée d'eau froide en bleu puis cyan
    private static readonly (float value, Color color)[] UpwellingGradient =
    {
        (-1f, new Color(0.65f, 0.12f, 0.1f)),
        (-0.2f, new Color(0.45f, 0.25f, 0.3f)),
        (0f, new Color(0.18f, 0.2f, 0.3f)),
        (0.2f, new Color(0.15f, 0.45f, 0.75f)),
        (1f, new Color(0.4f, 0.9f, 1f)),
    };
    private static readonly (float value, Color color)[] CurrentGradient =
    {
        (0f, new Color(0.03f, 0.06f, 0.2f)),
        (0.03f, new Color(0.08f, 0.25f, 0.55f)),
        (0.08f, new Color(0.15f, 0.6f, 0.75f)),
        (0.15f, new Color(0.45f, 0.85f, 0.4f)),
        (0.25f, new Color(1f, 0.85f, 0.2f)),
        (0.4f, new Color(0.95f, 0.3f, 0.1f)),
    };

    private static readonly (float value, Color color)[] WindGradient =
    {
        (0f, new Color(0.05f, 0.08f, 0.25f)),
        (3f, new Color(0.1f, 0.35f, 0.7f)),
        (6f, new Color(0.2f, 0.75f, 0.8f)),
        (9f, new Color(0.45f, 0.85f, 0.35f)),
        (13f, new Color(1f, 0.85f, 0.2f)),
        (18f, new Color(0.95f, 0.35f, 0.1f)),
        (25f, new Color(0.7f, 0.05f, 0.2f)),
    };

    // Pression au niveau de la mer (hPa) : dépressions bleues, 1013 hPa blanc, anticyclones rouges
    private static readonly (float value, Color color)[] PressureGradient =
    {
        (990f, new Color(0.1f, 0.15f, 0.55f)),
        (1002f, new Color(0.35f, 0.55f, 0.9f)),
        (1013f, new Color(0.95f, 0.95f, 0.95f)),
        (1022f, new Color(0.95f, 0.6f, 0.35f)),
        (1032f, new Color(0.65f, 0.1f, 0.1f)),
    };

    // Précipitations (mm/an) : désert beige, steppe, puis verts et bleus de plus en plus foncés
    private static readonly (float value, Color color)[] PrecipitationGradient =
    {
        (0f, new Color(0.85f, 0.75f, 0.55f)),
        (250f, new Color(0.9f, 0.85f, 0.45f)),
        (500f, new Color(0.6f, 0.8f, 0.35f)),
        (1000f, new Color(0.2f, 0.65f, 0.3f)),
        (2000f, new Color(0.1f, 0.45f, 0.65f)),
        (3500f, new Color(0.15f, 0.2f, 0.6f)),
        (6000f, new Color(0.35f, 0.1f, 0.5f)),
    };

    // Humidité relative (0 à 1) : air sec brun, air saturé bleu
    private static readonly (float value, Color color)[] HumidityGradient =
    {
        (0f, new Color(0.55f, 0.35f, 0.15f)),
        (0.3f, new Color(0.85f, 0.7f, 0.4f)),
        (0.6f, new Color(0.85f, 0.9f, 0.85f)),
        (0.85f, new Color(0.35f, 0.65f, 0.9f)),
        (1f, new Color(0.1f, 0.25f, 0.7f)),
    };

    // Vue réaliste : océan selon la profondeur, roche nue en haute montagne, neige, banquise, lacs
    private static readonly (float value, Color color)[] OceanDepthGradient =
    {
        (-8f, new Color(0.02f, 0.05f, 0.18f)),
        (-4f, new Color(0.03f, 0.1f, 0.3f)),
        (-1f, new Color(0.05f, 0.2f, 0.42f)),
        (0f, new Color(0.12f, 0.38f, 0.55f)),
    };
    private static readonly Color RockColor = new(0.45f, 0.4f, 0.36f);
    private static readonly Color SnowColor = new(0.96f, 0.97f, 1f);
    private static readonly Color SeaIceColor = new(0.85f, 0.9f, 0.95f);
    private static readonly Color LakeColor = new(0.15f, 0.4f, 0.6f);

    // Échelles de l'ensoleillement : flux maximal instantané (Soleil au zénith au périhélie)
    // et maximal en moyenne journalière (pôle d'été)
    private const float InstantScaleWm2 = 1410f;
    private const float DailyScaleWm2 = 560f;

    // Part de la carte d'altitude conservée sous les couches d'ensoleillement, pour voir les côtes
    private const float ReliefBlend = 0.18f;

    public static Color[] Compute(TectonicPlates tectonics, ClimateSimulation climate, DisplayMode mode)
    {
        var colors = new Color[tectonics.Grid.CellCount];
        var plateColors = ComputePlateColors(tectonics.Plates.Length);

        // Chaque cellule est indépendante : calcul réparti sur tous les cœurs
        System.Threading.Tasks.Parallel.For(0, colors.Length, cell =>
        {
            Color plateColor = plateColors[tectonics.CellPlate[cell]];
            bool continental = tectonics.CellContinental[cell];
            colors[cell] = mode switch
            {
                DisplayMode.Elevation or DisplayMode.Geology => Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.InstantInsolation => Sample(InsolationGradient, climate.InstantInsolation[cell] / InstantScaleWm2)
                    .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend),
                DisplayMode.DailyInsolation => Sample(InsolationGradient, climate.DailyInsolation[cell] / DailyScaleWm2)
                    .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend),
                DisplayMode.Temperature => climate.HasTemperature
                    ? Sample(TemperatureGradient, climate.SurfaceTemperature[cell])
                        .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.WindSpeed => climate.HasTemperature
                    ? Sample(WindGradient, climate.Winds.Wind[cell].Length())
                        .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.Upwelling => climate.HasTemperature && tectonics.CellElevation[cell] < 0f
                    ? Sample(UpwellingGradient, climate.Ocean.Upwelling[cell] * 86400f)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]).Lerp(new Color(0.5f, 0.5f, 0.5f), 0.6f),
                DisplayMode.OceanCurrents => climate.HasTemperature && tectonics.CellElevation[cell] < 0f
                    ? Sample(CurrentGradient, climate.Ocean.Current[cell].Length())
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]).Lerp(new Color(0.5f, 0.5f, 0.5f), 0.6f),
                DisplayMode.Pressure => climate.HasTemperature
                    ? Sample(PressureGradient, climate.Winds.Pressure[cell])
                        .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.RecentPrecipitation => climate.HasTemperature
                    ? Sample(PrecipitationGradient, climate.Statistics.RecentPrecipitation[cell] * 365.25f)
                        .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.AnnualPrecipitation => climate.HasTemperature
                    ? Sample(PrecipitationGradient, climate.Statistics.AnnualPrecipitation[cell])
                        .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.Humidity => climate.HasTemperature
                    ? Sample(HumidityGradient, climate.RelativeHumidity[cell])
                        .Lerp(Sample(ElevationGradient, tectonics.CellElevation[cell]), ReliefBlend)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.Biomes => climate.HasTemperature
                    ? BiomeTable.ColorOf(climate.Biomes[cell])
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.Realistic => climate.HasTemperature
                    ? RealisticColor(tectonics, climate, cell)
                    : Sample(ElevationGradient, tectonics.CellElevation[cell]),
                DisplayMode.Boundaries => BoundaryColor(tectonics, cell, plateColor),
                DisplayMode.CrustAge => continental
                    ? ContinentNeutralColor
                    : Sample(AgeGradient, tectonics.CellCrustAge[cell]),
                // Continents en teinte claire de leur plaque, océans en teinte foncée
                _ => continental ? plateColor.Lightened(0.35f) : plateColor.Darkened(0.25f),
            };
        });
        return colors;
    }

    /// <summary>
    /// Couleur « vue de l'espace » : biome, assombri en forêt, roche nue au-dessus de 3 km,
    /// neige au sol, glaciers, banquise, lacs, et océan d'autant plus sombre qu'il est profond.
    /// </summary>
    /// <summary>
    /// Végétation dessinée dans la vue réaliste. Avant l'apparition de la vie (étape 15), il n'y
    /// a ni forêt ni prairie : le sol est minéral (faux par défaut). Vrai : végétation
    /// « imaginée » d'après le climat, en attendant que la vie la fasse pousser.
    /// </summary>
    public static bool Vegetation { get; set; }

    /// <summary>
    /// Sol nu d'une planète sans vie, selon le climat : graviers et roches gélives des régions
    /// froides, sols d'altération bruns des régions humides, sombres sous les tropiques
    /// (sans oxygène, le fer ne rougit pas les sols), sables des déserts.
    /// </summary>
    public static Color BarrenColor(Biome biome) => biome switch
    {
        // (sols nus sombres : albédo de 0,15 à 0,25 ; seuls les sables des déserts sont clairs)
        Biome.Tundra or Biome.ColdDesert => new Color(0.31f, 0.29f, 0.27f),
        Biome.Taiga => new Color(0.28f, 0.26f, 0.23f),
        Biome.Steppe => new Color(0.37f, 0.32f, 0.25f),
        Biome.TemperateForest or Biome.TemperateRainforest => new Color(0.29f, 0.24f, 0.19f),
        Biome.HotDesert => new Color(0.58f, 0.45f, 0.30f),
        Biome.Savanna => new Color(0.41f, 0.33f, 0.24f),
        _ => new Color(0.26f, 0.22f, 0.19f),
    };

    private static Color RealisticColor(TectonicPlates tectonics, ClimateSimulation climate, int cell)
    {
        float elevation = tectonics.CellElevation[cell];
        if (elevation <= 0f)
        {
            Color water = Sample(OceanDepthGradient, elevation);
            // Banquise qui dérive avec les courants et le vent (étape 11e)
            return water.Lerp(SeaIceColor, climate.SeaIce.Concentration[cell]);
        }

        var hydrology = climate.Hydrology;
        if (hydrology != null && hydrology.Lake[cell])
            return LakeColor;

        Biome biome = climate.Biomes[cell];
        Color ground = BiomeTable.ColorOf(biome);
        if (biome == Biome.IceSheet || (hydrology != null && hydrology.Glacier[cell]))
            return SnowColor;
        ground = ground.Darkened(0.15f);
        if (Vegetation)
            ground = SeasonalTint(ground, biome, climate.Seasons, cell);
        else
            ground = BarrenColor(biome);
        // Roche nue en haute montagne
        ground = ground.Lerp(RockColor, Mathf.Clamp((elevation - 2.5f) / 2f, 0f, 0.8f));
        // La neige au sol est dessinée par le shader du terrain, point par point selon l'altitude
        return ground;
    }

    private static readonly Color AutumnColor = new(0.72f, 0.42f, 0.13f);
    private static readonly Color BareTreesColor = new(0.40f, 0.35f, 0.29f);
    private static readonly Color StrawColor = new(0.72f, 0.64f, 0.40f);

    /// <summary>
    /// Saisons (étape 12b) : feuillus colorés à l'automne puis nus, herbes qui jaunissent en
    /// saison sèche ou en hiver, selon la part de forêt, de feuillus et d'herbe du biome.
    /// </summary>
    public static Color SeasonalTint(Color ground, Biome biome, VegetationSeasons seasons, int cell)
    {
        float forest = VegetationSeasons.ForestShare(biome) * VegetationSeasons.DeciduousShare(biome);
        float leaves = seasons.Leaves[cell], autumn = seasons.Autumn[cell];
        ground = ground.Lerp(AutumnColor, forest * leaves * autumn * 0.85f);
        ground = ground.Lerp(BareTreesColor, forest * (1f - leaves) * 0.8f);
        ground = ground.Lerp(StrawColor, VegetationSeasons.GrassShare(biome) * (1f - seasons.Grass[cell]) * 0.8f);
        return ground;
    }

    /// <summary>Teintes réparties par le nombre d'or : deux plaques voisines dans la liste
    /// ont toujours des couleurs bien distinctes.</summary>
    public static Color[] ComputePlateColors(int plateCount)
    {
        const float goldenRatioConjugate = 0.618034f;
        var colors = new Color[plateCount];
        for (int p = 0; p < plateCount; p++)
        {
            float hue = Mathf.PosMod(0.12f + p * goldenRatioConjugate, 1f);
            float value = p % 2 == 0 ? 0.85f : 0.65f;
            colors[p] = Color.FromHsv(hue, 0.5f, value);
        }
        return colors;
    }

    /// <summary>Interpolation linéaire dans une palette (valeur, couleur) triée.</summary>
    private static Color Sample((float value, Color color)[] gradient, float value)
    {
        if (value <= gradient[0].value)
            return gradient[0].color;
        for (int i = 1; i < gradient.Length; i++)
        {
            var (stop, color) = gradient[i];
            if (value <= stop)
            {
                var (previousStop, previousColor) = gradient[i - 1];
                return previousColor.Lerp(color, (value - previousStop) / (stop - previousStop));
            }
        }
        return gradient[^1].color;
    }

    private static Color BoundaryColor(TectonicPlates tectonics, int cell, Color plateColor)
    {
        // Intérieur des plaques assombri pour faire ressortir les frontières
        Color background = plateColor.Darkened(0.55f);

        Color boundaryColor;
        switch (tectonics.CellBoundary[cell])
        {
            case BoundaryType.Convergent: boundaryColor = ConvergentColor; break;
            case BoundaryType.Divergent: boundaryColor = DivergentColor; break;
            case BoundaryType.Transform: boundaryColor = TransformColor; break;
            default: return background;
        }

        float intensity = Mathf.Clamp(tectonics.CellBoundaryRate[cell] / FullIntensityRate, 0.35f, 1f);
        return background.Lerp(boundaryColor, intensity);
    }
}
