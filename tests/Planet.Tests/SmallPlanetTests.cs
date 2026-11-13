using System;
using System.Linq;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;

namespace Planet.Tests;

/// <summary>Petite planète « éprouvette » (rayon de 2 000 km, 5 plaques, grille de 2 562 cellules).</summary>
public static class SmallPlanetTests
{
    private const float RadiusKm = 2000f;

    /// <summary>Exécute une action avec le rayon de la petite planète, puis remet celui de la Terre.</summary>
    private static T WithSmallRadius<T>(Func<T> action)
    {
        PlanetConstants.SetRadius(RadiusKm);
        try { return action(); }
        finally { PlanetConstants.SetRadius(PlanetConstants.EarthRadiusKm); }
    }

    [Test(Slow = true)]
    public static void SmallPlanetStaysSaneAndHasFewerFeatures()
    {
        var grid = Fixtures.Grid(4);
        var orbit = new OrbitalParameters();
        var (alive, land, volcanoes, hotspots, impacts, highest, meanTemperature, rain) = WithSmallRadius(() =>
        {
            var tectonics = TectonicPlates.Generate(grid, 5, 2);
            double nextEstimate = 0;
            FastClimateEstimate estimate = null;
            while (tectonics.TimeMyr < 400)
            {
                if (tectonics.TimeMyr >= nextEstimate)
                {
                    estimate = FastClimateEstimator.Estimate(grid, tectonics.CellElevation, orbit);
                    tectonics.AnnualPrecipitation = estimate.AnnualPrecipitation;
                    nextEstimate += 25;
                }
                tectonics.Step(tectonics.MaxTimeStep());
            }
            int active = tectonics.Landforms.Count(l => l.Active && l.Kind != LandformKind.ImpactCrater);
            // Le climat complet (vents, océans, banquise, cycle de l'eau) : une année, sans NaN
            var climate = new ClimateSimulation(grid, orbit, useGpu: false, startDay: 79.5);
            climate.SetSurface(tectonics.CellElevation);
            climate.WaitForHydrology();
            while (climate.Statistics.CompletedYears < 1)
                climate.Step(1.0);
            Assert.Finite(climate.SurfaceTemperature, "températures de la petite planète");
            Assert.Finite(climate.RelativeHumidity, "humidité de la petite planète");
            double yearlyRain = climate.Statistics.AnnualPrecipitation.Average();
            Console.WriteLine($"     climat complet après un an : {climate.GlobalMeanTemperature:0.0} °C, {yearlyRain:0} mm/an");
            Assert.True(climate.GlobalMeanTemperature is > 0 and < 30 && yearlyRain is > 300 and < 3500, "climat complet vivable, pluies terrestres");
            climate.Dispose();
            return (tectonics.AlivePlateCount, tectonics.CellElevation.Count(e => e > 0) / (double)grid.CellCount, active,
                    tectonics.Hotspots.Count, tectonics.ImpactCount, tectonics.CellElevation.Max(),
                    estimate.MeanTemperature.Average(), estimate.AnnualPrecipitation.Average());
        });
        Console.WriteLine($"     après 400 Ma : {alive} plaques, {land * 100:0} % de terres, sommet {highest:0.0} km, {volcanoes} volcans actifs, " +
                          $"{hotspots} point chaud, {impacts} impacts ; climat {meanTemperature:0.0} °C, {rain:0} mm/an");
        Assert.True(alive is >= 2 and <= 12, $"nombre de plaques : {alive}");
        Assert.True(land is > 0.1 and < 0.6, $"part de terres : {land:0.00}");
        Assert.True(highest is > 0.5f and < 12f, $"plus haut sommet : {highest:0.0} km");
        Assert.True(hotspots == 1, "un seul point chaud sur une planète dix fois plus petite que la Terre");
        Assert.True(volcanoes is > 0 and < 60, $"une poignée de volcans : {volcanoes}");
        Assert.True(meanTemperature is > -5 and < 30 && rain is > 200 and < 3000, $"climat estimé vivable : {meanTemperature:0.0} °C, {rain:0} mm/an");
    }

    [Test]
    public static void RatesScaleWithTheSurface()
    {
        Assert.Near(PlanetConstants.AreaRatio, 1, 1e-6, "la Terre par défaut");
        double ratio = WithSmallRadius(() => PlanetConstants.AreaRatio);
        Assert.Near(ratio, 0.0985, 0.001, "surface d'une planète de 2 000 km par rapport à la Terre");
        Assert.True(Math.Abs(Simulation.Terrain.TerrainHeightField.RadiusMeters - 6371000) < 1e-6, "rayon remis à celui de la Terre");
        // Moins de séismes : même planète, deux rayons
        var tectonics = TectonicPlates.Generate(Fixtures.Grid(4), 5, 4);
        int Quakes()
        {
            var activity = new PlanetActivity(3);
            activity.SetPlanet(tectonics);
            int count = 0;
            for (int day = 0; day < 365; day++)
            {
                activity.Step(day, 1);
                count += activity.RecentEarthquakes.Count;
            }
            return count;
        }
        int earth = Quakes(), small = WithSmallRadius(Quakes);
        Console.WriteLine($"     séismes de magnitude 5 et plus en un an : {earth} (taille de la Terre), {small} (petite planète)");
        Assert.True(small < earth / 4, "une petite planète a moins de séismes");
    }
}
