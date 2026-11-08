using System;
using System.Linq;
using Godot;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Orbite, saisons et ensoleillement (étape 3).</summary>
public static class OrbitTests
{
    private static readonly OrbitalParameters Earth = new();

    private static double[] YearSamples(int count) =>
        Enumerable.Range(0, count).Select(i => (i + 0.5) * Earth.YearLengthDays / count).ToArray();

    [Test]
    public static void DeclinationSpansAxialTilt()
    {
        var samples = YearSamples(2000).Select(day => (day, sun: Earth.ComputeSun(day))).ToArray();
        var max = samples.MaxBy(s => s.sun.Declination);
        var min = samples.MinBy(s => s.sun.Declination);
        Assert.Near(Mathf.RadToDeg(max.sun.Declination), 23.44, 0.05, "déclinaison maximale (°)");
        Assert.Near(Mathf.RadToDeg(min.sun.Declination), -23.44, 0.05, "déclinaison minimale (°)");
        // Solstice d'été vers le 21 juin (jour 171), d'hiver vers le 21 décembre (jour 354)
        Assert.Near(max.day, 171.5, 3, "jour du solstice de juin");
        Assert.Near(min.day, 354.5, 3, "jour du solstice de décembre");
    }

    [Test]
    public static void DistanceFollowsEccentricity()
    {
        var distances = YearSamples(4000).Select(day => (day, d: Earth.ComputeSun(day).DistanceAu)).ToArray();
        var perihelion = distances.MinBy(x => x.d);
        var aphelion = distances.MaxBy(x => x.d);
        Assert.Near(perihelion.d, 1 - Earth.Eccentricity, 1e-4, "distance au périhélie (UA)");
        Assert.Near(aphelion.d, 1 + Earth.Eccentricity, 1e-4, "distance à l'aphélie (UA)");
        Assert.Near(perihelion.day, Earth.PerihelionDay, 1.5, "jour du périhélie");
    }

    [Test]
    public static void GlobalDailyMeanIsQuarterOfFlux()
    {
        // Moyenne sur la sphère de l'ensoleillement moyen sur 24 h = flux / 4 (géométrie)
        var grid = Fixtures.Grid(5);
        float[] areas = grid.ComputeCellAreas();
        var instant = new float[grid.CellCount];
        var daily = new float[grid.CellCount];
        foreach (double day in new[] { 10.0, 100.0, 200.0, 300.0 })
        {
            var sun = Earth.ComputeSun(day);
            Insolation.Compute(grid.Positions, sun, instant, daily);
            double meanDaily = Fixtures.AreaSum(areas, c => daily[c]) / (4 * Math.PI);
            double meanInstant = Fixtures.AreaSum(areas, c => instant[c]) / (4 * Math.PI);
            Assert.Near(meanDaily, sun.Flux / 4, sun.Flux * 0.002, $"moyenne journalière, jour {day}");
            Assert.Near(meanInstant, sun.Flux / 4, sun.Flux * 0.002, $"moyenne instantanée, jour {day}");
        }
    }

    [Test]
    public static void PolarDayAndNightAtSolstice()
    {
        var sun = Earth.ComputeSun(171.5);
        float north = Insolation.DailyMean(Vector3.Up, sun);
        float south = Insolation.DailyMean(Vector3.Down, sun);
        // Pôle d'été : Soleil à hauteur constante = déclinaison, toute la journée
        Assert.Near(north, sun.Flux * Mathf.Sin(sun.Declination), 1.0, "pôle nord en juin (W/m²)");
        Assert.Near(south, 0, 1e-3, "pôle sud en juin : nuit polaire");
    }

    [Test]
    public static void InstantInsolationDayAndNight()
    {
        var sun = Earth.ComputeSun(80.0);
        Assert.Near(Insolation.Instant(sun.Direction, sun), sun.Flux, 0.5, "point subsolaire (W/m²)");
        Assert.Near(Insolation.Instant(-sun.Direction, sun), 0, 1e-6, "antipode : nuit");
    }

    [Test]
    public static void AnnualMeanIsHemisphericallySymmetric()
    {
        // Avec une orbite elliptique, la 2e loi de Kepler compense exactement la distance :
        // les deux hémisphères reçoivent autant d'énergie sur l'année
        var grid = Fixtures.Grid(4);
        float[] annual = AtmosphereModel.AnnualMeanInsolation(grid, Earth);
        float[] areas = grid.ComputeCellAreas();
        double north = 0, south = 0, equator = 0, pole = 0;
        int equatorCount = 0, poleCount = 0;
        for (int c = 0; c < grid.CellCount; c++)
        {
            float lat = Fixtures.LatitudeDeg(grid.Positions[c]);
            if (lat > 0) north += areas[c] * annual[c];
            if (lat < 0) south += areas[c] * annual[c];
            if (MathF.Abs(lat) < 5) { equator += annual[c]; equatorCount++; }
            if (MathF.Abs(lat) > 85) { pole += annual[c]; poleCount++; }
        }
        Assert.Near(north / south, 1.0, 0.005, "rapport nord / sud");
        Assert.Near(equator / equatorCount, 417, 8, "équateur, moyenne annuelle (W/m², Terre ≈ 417)");
        Assert.Near(pole / poleCount, 173, 8, "pôles, moyenne annuelle (W/m², Terre ≈ 173)");
    }
}
