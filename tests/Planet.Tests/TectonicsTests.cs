using System;
using System.Linq;
using Planet.Simulation;
using Planet.Simulation.Tectonics;

namespace Planet.Tests;

/// <summary>Tectonique des plaques et relief (étapes 2 et 7).</summary>
public static class TectonicsTests
{
    private static TectonicPlates Run(int subdivisions, int seed, double myr, float[] precipitation = null)
    {
        var tectonics = TectonicPlates.Generate(Fixtures.Grid(subdivisions), 14, seed);
        tectonics.AnnualPrecipitation = precipitation;
        while (tectonics.TimeMyr < myr)
            tectonics.Step(tectonics.MaxTimeStep());
        return tectonics;
    }

    [Test]
    public static void SameSeedGivesSamePlanet()
    {
        // Calcul réparti sur plusieurs cœurs, mais résultat identique au bit près
        var a = Run(4, 3, 60);
        var b = Run(4, 3, 60);
        Assert.True(a.CellElevation.SequenceEqual(b.CellElevation), "relief différent pour la même graine");
        Assert.True(a.CellPlate.SequenceEqual(b.CellPlate), "plaques différentes pour la même graine");
        Assert.Equal(a.RiftCount, b.RiftCount, "nombre de fracturations");
    }

    [Test]
    public static void DifferentSeedsGiveDifferentPlanets()
    {
        var a = Run(4, 3, 0);
        var b = Run(4, 4, 0);
        Assert.True(!a.CellElevation.SequenceEqual(b.CellElevation), "deux graines donnent le même relief");
    }

    [Test]
    public static void InitialPlanetIsSane()
    {
        var tectonics = Run(4, 1, 0);
        Assert.Equal(14, tectonics.AlivePlateCount, "plaques au départ");
        Assert.Finite(tectonics.CellElevation, "altitudes");
        Assert.InRange(tectonics.ContinentalFraction, 0.15, 0.5, "part de croûte continentale");
        var plateCells = tectonics.CellPlate.GroupBy(p => p).Select(g => g.Count()).ToArray();
        Assert.Equal(14, plateCells.Length, "chaque plaque possède des cellules");
    }

    [Test]
    public static void RainErodesMountainsFaster()
    {
        // Même planète, climat sec (150 mm/an) ou très humide (3 000 mm/an)
        int count = Fixtures.Grid(4).CellCount;
        var dry = Run(4, 2, 60, Enumerable.Repeat(150f, count).ToArray());
        var wet = Run(4, 2, 60, Enumerable.Repeat(3000f, count).ToArray());
        double MeanLand(TectonicPlates t) => t.CellElevation.Where(e => e > 0f).Average();
        double Highest(TectonicPlates t) => t.CellElevation.Max();
        Assert.True(MeanLand(wet) < MeanLand(dry), $"altitude moyenne des terres : humide {MeanLand(wet):0.000} km, sec {MeanLand(dry):0.000} km");
        Assert.True(Highest(wet) < Highest(dry), $"point culminant : humide {Highest(wet):0.00} km, sec {Highest(dry):0.00} km");
        Assert.True(wet.SedimentDeposited > dry.SedimentDeposited, "plus d'érosion ⇒ plus de sédiments déposés");
    }

    [Test(Slow = true)]
    public static void FourHundredMillionYearsStaySane()
    {
        var tectonics = Run(5, 1, 400);
        Assert.Finite(tectonics.CellElevation, "altitudes");
        Assert.InRange(tectonics.ContinentalFraction, 0.15, 0.65, "part de croûte continentale");
        Assert.InRange(tectonics.CellElevation.Max(), 2, 10, "point culminant (km)");
        Assert.InRange(tectonics.CellElevation.Min(), -12, -4, "fosse la plus profonde (km)");
        Assert.True(tectonics.AlivePlateCount >= 4, $"seulement {tectonics.AlivePlateCount} plaques vivantes");
        Assert.True(tectonics.RiftCount > 0 && tectonics.SutureCount > 0, "aucune fracturation ou aucune collision en 400 Ma");
        double land = tectonics.CellElevation.Count(e => e > 0f) / (double)tectonics.CellElevation.Length;
        Assert.InRange(land, 0.1, 0.6, "part des terres émergées");
    }
}
