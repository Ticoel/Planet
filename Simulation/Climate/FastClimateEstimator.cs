using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>Climat moyen estimé de chaque cellule (grille complète).</summary>
public sealed class FastClimateEstimate
{
    public float[] MeanTemperature { get; init; }
    public float[] ColdestMonth { get; init; }
    public float[] WarmestMonth { get; init; }
    public float[] AnnualPrecipitation { get; init; }
    public float[] AnnualRunoff { get; init; }
    public float[] AnnualSnowfall { get; init; }
    /// <summary>Neige au sol à l'équilibre (mm d'équivalent eau).</summary>
    public float[] Snow { get; init; }
}

/// <summary>
/// Estimation rapide du climat moyen (quelques dixièmes de seconde), sans saisons, sur une grille
/// grossière : températures d'équilibre de l'ensoleillement annuel moyen, vents correspondants,
/// puis cycle de l'eau pendant une année simulée. Sert :
/// <list type="bullet">
/// <item>à donner des biomes et des rivières dès l'entrée dans l'horloge climatique, avant
/// qu'une année complète ait été simulée ;</item>
/// <item>à l'horloge géologique, pour que l'érosion dépende du climat (étape 7) : il pleut
/// beaucoup plus sur une chaîne côtière face aux vents d'ouest que dans un désert.</item>
/// </list>
/// L'amplitude saisonnière, absente de ce calcul sans saisons, est approchée d'après la latitude
/// et l'éloignement de la mer ; elle n'influence que la classification initiale des biomes.
/// </summary>
public static class FastClimateEstimator
{
    private const int CoarseLevel = 4;
    private const int SpinUpDays = 180;
    private const int MeasureDays = 180;

    public static FastClimateEstimate Estimate(PlanetGrid grid, float[] elevationKm, OrbitalParameters orbit)
    {
        int level = Math.Min(CoarseLevel, grid.Subdivisions);
        var coarseGrid = level == grid.Subdivisions ? grid : PlanetGrid.Create(level);
        int coarseCount = coarseGrid.CellCount;

        // Les premiers sommets de la grille fine sont ceux de la grille grossière : même relief
        var coarseElevation = new float[coarseCount];
        Array.Copy(elevationKm, coarseElevation, coarseCount);

        var model = new AtmosphereModel(coarseGrid);
        model.SetSurface(coarseElevation);
        var winds = new WindModel(coarseGrid);
        var annual = AtmosphereModel.AnnualMeanInsolation(coarseGrid, orbit);

        var state = model.ComputeInitialState(orbit);
        var next = new float[state.Length];
        var accumulation = new float[state.Length];
        var temperature = new float[coarseCount];

        float dt = model.MaxSubstepSeconds;
        int substepsPerDay = (int)Math.Ceiling(86400.0 / dt);
        dt = 86400f / substepsPerDay;

        for (int day = 0; day < SpinUpDays + MeasureDays; day++)
        {
            for (int c = 0; c < coarseCount; c++)
                temperature[c] = state[c * 4];
            winds.Update(temperature);
            if (day == SpinUpDays)
                Array.Clear(accumulation);
            for (int k = 0; k < substepsPerDay; k++)
            {
                model.StepCpu(state, next, accumulation, annual, winds.WindPacked, null, dt);
                (state, next) = (next, state);
            }
        }

        SpreadPrecipitation(coarseGrid, accumulation);

        // Report sur la grille fine (cellule grossière la plus proche)
        int count = grid.CellCount;
        var estimate = new FastClimateEstimate
        {
            MeanTemperature = new float[count],
            ColdestMonth = new float[count],
            WarmestMonth = new float[count],
            AnnualPrecipitation = new float[count],
            AnnualRunoff = new float[count],
            AnnualSnowfall = new float[count],
            Snow = new float[count],
        };
        float toAnnual = (float)(orbit.YearLengthDays / MeasureDays);
        int start = 0;
        for (int cell = 0; cell < count; cell++)
        {
            start = ReferenceEquals(coarseGrid, grid) ? cell : coarseGrid.NearestCell(grid.Positions[cell], start);
            int c = start;
            bool ocean = elevationKm[cell] <= 0f;
            // Température de surface avec l'altitude propre de la cellule fine
            float surfaceT = state[c * 4] - AtmosphereModel.LapseRateKPerKm * MathF.Max(elevationKm[cell], 0f);
            float sinLatitude = MathF.Abs(grid.Positions[cell].Y);
            float amplitude = ocean ? 2f + 6f * sinLatitude : 4f + 22f * sinLatitude;
            estimate.MeanTemperature[cell] = surfaceT;
            estimate.ColdestMonth[cell] = surfaceT - amplitude;
            estimate.WarmestMonth[cell] = surfaceT + amplitude;
            estimate.AnnualPrecipitation[cell] = accumulation[c * 4] * toAnnual;
            estimate.AnnualRunoff[cell] = ocean ? 0f : accumulation[c * 4 + 2] * toAnnual;
            estimate.AnnualSnowfall[cell] = accumulation[c * 4 + 3] * toAnnual;
            estimate.Snow[cell] = ocean ? 0f : state[c * 4 + 3];
        }
        return estimate;
    }

    /// <summary>
    /// Sans saisons, la pluie tombait en bandes étroites, toujours aux mêmes endroits (la moitié
    /// des terres sous 100 mm/an, des cellules à 20 ou 40 m/an). Au fil de l'année, les ceintures
    /// de pluie se déplacent avec le Soleil : la pluie est étalée sur les voisines (quatre passes),
    /// total conservé, puis plafonnée à ~12 m/an (record terrestre : Cherrapunji, Mawsynram).
    /// Ruissellement et neige suivent la même proportion.
    /// </summary>
    private static void SpreadPrecipitation(PlanetGrid grid, float[] accumulation)
    {
        int n = grid.CellCount;
        var rain = new float[n];
        for (int c = 0; c < n; c++)
            rain[c] = accumulation[c * 4];
        var original = (float[])rain.Clone();
        var next = new float[n];
        for (int pass = 0; pass < 4; pass++)
        {
            for (int c = 0; c < n; c++)
            {
                float sum = 0;
                foreach (int nb in grid.Neighbors[c])
                    sum += rain[nb];
                next[c] = 0.5f * rain[c] + 0.5f * sum / grid.Neighbors[c].Length;
            }
            (rain, next) = (next, rain);
        }
        // Plafond : 12 m/an, rapporté aux 180 jours mesurés
        float cap = 12000f * MeasureDays / 365.25f;
        for (int c = 0; c < n; c++)
        {
            float value = MathF.Min(rain[c], cap);
            float ratio = original[c] > 1e-6f ? value / original[c] : 1f;
            accumulation[c * 4] = value;
            accumulation[c * 4 + 2] = MathF.Min(accumulation[c * 4 + 2] * ratio, value);
            accumulation[c * 4 + 3] = MathF.Min(accumulation[c * 4 + 3] * ratio, value);
        }
    }
}
