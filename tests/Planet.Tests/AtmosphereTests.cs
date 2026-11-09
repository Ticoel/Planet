using System;
using System.Linq;
using Godot;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Températures, cycle de l'eau, neige et sols (étapes 4, 6 et 7).</summary>
public static class AtmosphereTests
{
    // Une cellule de terre à 0 m : capacité, albédo, altitude, océan ; puis sol
    private static readonly Vector4 LandSurface = new(AtmosphereModel.LandHeatCapacity, AtmosphereModel.LandAlbedo, 0f, 0f);
    private static readonly Vector4 OceanSurface = new(AtmosphereModel.OceanHeatCapacity, AtmosphereModel.OceanAlbedo, 0f, 1f);
    private static readonly Vector4 Soil = new(150f, 1.3f, AtmosphereModel.SnowAlbedo, 0f);
    private const float Dt = 3600f;

    /// <summary>Un sous-pas d'une cellule isolée (voisines identiques, sans vent).</summary>
    private static Vector4 Step(Vector4 state, Vector4 surface, float insolation, out Vector4 accumulation) =>
        AtmosphereModel.StepCell(state, state.X, state.Y, 0f, 0f, 0f, 0f, insolation, surface, Soil, Vector3.Zero,
                                 Vector4.Zero, 0f, 0f, Dt, out accumulation);

    [Test]
    public static void SaturationIncreasesWithTemperatureAndIsCapped()
    {
        float previous = 0f;
        for (float t = -60f; t <= 60f; t += 1f)
        {
            float s = AtmosphereModel.Saturation(t);
            Assert.True(s >= previous, $"saturation décroissante à {t} °C");
            previous = s;
        }
        Assert.Near(AtmosphereModel.Saturation(AtmosphereModel.SaturationReferenceCelsius), AtmosphereModel.SaturationReferenceMm, 1e-3, "saturation de référence");
        Assert.Near(AtmosphereModel.Saturation(60f), AtmosphereModel.MaxSaturationMm, 1e-3, "plafond de saturation");
        // Clausius-Clapeyron : environ +7 % par degré
        double ratio = AtmosphereModel.Saturation(11f) / AtmosphereModel.Saturation(10f);
        Assert.Near(ratio, 1.067, 0.005, "augmentation par degré");
    }

    [Test]
    public static void ColdPrecipitationFallsAsSnowAndAccumulates()
    {
        // Air sursaturé à −15 °C : il neige, la neige reste au sol, rien ne ruisselle
        float t = -15f;
        var state = new Vector4(t, 2f * AtmosphereModel.Saturation(t), 50f, 0f);
        var next = Step(state, LandSurface, 0f, out var accumulation);
        Assert.True(accumulation.X > 0f, "il doit précipiter");
        Assert.Near(accumulation.W, accumulation.X, 1e-6, "toute la précipitation est de la neige");
        Assert.Near(next.W, accumulation.W, 1e-5, "la neige tombée s'ajoute au sol");
        Assert.Near(accumulation.Z, 0f, 1e-9, "pas de ruissellement");
    }

    [Test]
    public static void WarmWeatherMeltsSnowIntoSoil()
    {
        // +8 °C, 100 mm de neige, air sec : la neige fond (4 mm/j/K au-dessus de −2 °C) et remplit le sol
        float t = 8f;
        var state = new Vector4(t, 0f, 20f, 100f);
        var next = Step(state, LandSurface, 0f, out var accumulation);
        float expectedMelt = AtmosphereModel.MeltMmPerDayPerKelvin * (t - AtmosphereModel.MeltBaseCelsius) * Dt / 86400f;
        Assert.Near(state.W - next.W, expectedMelt, 1e-4, "fonte (mm)");
        Assert.True(next.Z > state.Z, "l'eau de fonte entre dans le sol");
        Assert.Near(accumulation.W, 0f, 1e-9, "pas de neige");
    }

    [Test]
    public static void FullSoilOverflowsAsRunoff()
    {
        // Sol plein et forte pluie à 20 °C : tout le surplus ruisselle
        float t = 20f;
        var state = new Vector4(t, 3f * AtmosphereModel.Saturation(t), 150f, 0f);
        var next = Step(state, LandSurface, 0f, out var accumulation);
        Assert.Near(next.Z, 150f, 1e-3, "le sol reste plein");
        Assert.True(accumulation.Z > 0f, "le surplus doit ruisseler");
        Assert.Near(accumulation.Z, accumulation.X - accumulation.Y, 1e-3, "ruissellement = pluie − évaporation");
    }

    [Test]
    public static void OceanHasNoSoilNoSnowAndEvaporatesWhenDry()
    {
        var state = new Vector4(25f, 0.3f * AtmosphereModel.Saturation(25f), 0f, 0f);
        var next = Step(state, OceanSurface, 300f, out var accumulation);
        Assert.Near(next.Z, 0f, 1e-9, "sol sur l'océan");
        Assert.Near(next.W, 0f, 1e-9, "neige sur l'océan");
        Assert.True(accumulation.Y > 0f, "un océan chaud sous un air sec doit s'évaporer");
        Assert.True(next.Y > state.Y, "la vapeur augmente");
    }

    [Test]
    public static void EnergyBalanceReachesAnalyticEquilibrium()
    {
        // Sans voisines ni vent : (1 − α) Q = A + B T ⇒ T = ((1 − α) Q − A) / B
        float q = 400f;
        var state = new Vector4(0f, 0f, 150f, 0f);
        for (int i = 0; i < 24 * 365 * 2; i++)   // constante de temps C/B ≈ 55 jours
            state = Step(state, LandSurface, q, out _);
        double expected = ((1 - AtmosphereModel.LandAlbedo) * q - AtmosphereModel.OutgoingA) / AtmosphereModel.OutgoingB;
        Assert.Near(state.X, expected, 0.05, "température d'équilibre (°C)");
    }

    // --- Sur toute la grille ------------------------------------------------------------------

    private static (AtmosphereModel model, WindModel winds, float[] state, float[] insolation, float dt) Planet(int n)
    {
        var grid = Fixtures.Grid(n);
        var model = new AtmosphereModel(grid);
        model.SetSurface(Fixtures.DomeContinent(grid, 30f, 20f, 45f, 7));
        var orbit = new OrbitalParameters();
        var state = model.ComputeInitialState(orbit);
        var winds = new WindModel(grid);
        return (model, winds, state, AtmosphereModel.AnnualMeanInsolation(grid, orbit), model.MaxSubstepSeconds);
    }

    private static void UpdateWinds(WindModel winds, float[] state)
    {
        var temperature = new float[state.Length / 4];
        for (int c = 0; c < temperature.Length; c++)
            temperature[c] = state[c * 4];
        winds.Update(temperature);
    }

    [Test]
    public static void WaterIsConserved()
    {
        // Eau totale (vapeur + sol + neige) : ne change que par les échanges avec l'océan
        // (évaporation − précipitations sur l'océan) et le ruissellement des terres vers la mer
        var (model, winds, state, insolation, dt) = Planet(4);
        var grid = model.Grid;
        float[] areas = grid.ComputeCellAreas();
        double Total(float[] s) => Fixtures.AreaSum(areas, c => s[c * 4 + 1] + s[c * 4 + 2] + s[c * 4 + 3]);

        var next = new float[state.Length];
        double start = Total(state), expected = 0, throughput = 0;
        for (int day = 0; day < 30; day++)
        {
            UpdateWinds(winds, state);
            var accumulation = new float[state.Length];
            for (int k = 0; k < (int)(86400 / dt); k++)
            {
                model.StepCpu(state, next, accumulation, insolation, winds.WindPacked, null, dt);
                (state, next) = (next, state);
            }
            for (int c = 0; c < grid.CellCount; c++)
            {
                int i = c * 4;
                expected += areas[c] * (model.IsOcean(c) ? accumulation[i + 1] - accumulation[i] : -accumulation[i + 2]);
                throughput += areas[c] * accumulation[i];
            }
        }
        double error = Total(state) - start - expected;
        Assert.True(Math.Abs(error) < 0.002 * throughput,
            $"bilan d'eau : écart {error:G3} pour {throughput:G3} de précipitations cumulées ({Math.Abs(error) / throughput:P3})");
    }

    [Test]
    public static void GridStepIsDeterministic()
    {
        var (model, winds, state, insolation, dt) = Planet(4);
        UpdateWinds(winds, state);
        var a = new float[state.Length];
        var b = new float[state.Length];
        model.StepCpu(state, a, new float[state.Length], insolation, winds.WindPacked, null, dt);
        model.StepCpu(state, b, new float[state.Length], insolation, winds.WindPacked, null, dt);
        Assert.True(a.SequenceEqual(b), "deux calculs identiques doivent donner le même résultat au bit près");
    }

    [Test]
    public static void HalfYearWithSeasonsStaysPhysical()
    {
        var (model, winds, state, _, dt) = Planet(4);
        var grid = model.Grid;
        var orbit = new OrbitalParameters();
        var instant = new float[grid.CellCount];
        var daily = new float[grid.CellCount];
        var next = new float[state.Length];
        var accumulation = new float[state.Length];
        for (int day = 0; day < 180; day++)
        {
            Insolation.Compute(grid.Positions, orbit.ComputeSun(day + 0.5), instant, daily);
            UpdateWinds(winds, state);
            for (int k = 0; k < (int)(86400 / dt); k++)
            {
                model.StepCpu(state, next, accumulation, daily, winds.WindPacked, null, dt);
                (state, next) = (next, state);
            }
        }
        Assert.Finite(state, "état");
        for (int c = 0; c < grid.CellCount; c++)
        {
            int i = c * 4;
            Assert.InRange(state[i], -90, 60, $"température de la cellule {c}");
            Assert.True(state[i + 1] >= 0f, $"vapeur négative en {c}");
            Assert.InRange(state[i + 2], 0, model.LandCover[i] + 1e-3, $"eau du sol en {c}");
            Assert.InRange(state[i + 3], 0, AtmosphereModel.GlacierMaxMm + 1e-3, $"neige en {c}");
        }
        double meanPrecipitation = Enumerable.Range(0, grid.CellCount).Average(c => accumulation[c * 4]) / 180;
        // Planète presque entièrement océanique, partie d'un état initial chaud : plus humide que la Terre
        Assert.InRange(meanPrecipitation, 1.5, 7, "précipitations moyennes (mm/jour, Terre ≈ 2,7)");
    }
}
