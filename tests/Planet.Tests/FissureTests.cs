using System;
using System.Linq;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Éruptions fissurales et fentes de dessiccation.</summary>
public static class FissureTests
{
    private static readonly Lazy<TectonicPlates> Evolved = new(() =>
    {
        var tectonics = TectonicPlates.Generate(Fixtures.Grid(5), 12, 3);
        while (tectonics.TimeMyr < 60)
            tectonics.Step(tectonics.MaxTimeStep());
        return tectonics;
    });

    [Test]
    public static void FissuresOpenWithEarthquakeSwarms()
    {
        var activity = new PlanetActivity(5);
        activity.SetPlanet(Evolved.Value);
        int fissures = 0, swarms = 0;
        var seen = new System.Collections.Generic.HashSet<Eruption>();
        for (int day = 0; day < 365 * 5; day++)
        {
            activity.Step(day, 1);
            swarms += activity.RecentEarthquakes.Count(q => q.Magnitude < 5.6f && q.Boundary == BoundaryType.Divergent);
            foreach (var e in activity.Eruptions.Where(e => e.Fissure && seen.Add(e)))
            {
                fissures++;
                var (a, b) = e.Ends();
                double length = a.AngleTo(b) * PlanetConstants.RadiusKm * 1000;
                Assert.Near(length, e.LengthM, 0.02 * e.LengthM + 1, "longueur de la fissure");
                Assert.True(Math.Abs(e.Strike.Length() - 1) < 1e-3 && Math.Abs(e.Strike.Dot(e.Position.Normalized())) < 1e-3,
                    "direction de la fissure tangente à la planète");
                Assert.True(e.LengthM is >= 500 and <= 12000, $"longueur vraisemblable : {e.LengthM:0} m");
            }
        }
        Console.WriteLine($"     en 5 ans : {fissures} éruptions fissurales, {swarms} séismes d'essaims");
        Assert.True(fissures > 0, "des fissures s'ouvrent (zones de rift des volcans boucliers, rifts émergés)");
        Assert.True(swarms >= 4 * fissures, "chaque fissure s'ouvre avec un essaim de séismes");
    }

    private static readonly Double3 Site = new Double3(0.2, 0.7, 0.68).Normalized();

    [Test]
    public static void LavaPoursAlongTheWholeFissure()
    {
        var (ax, ay) = LavaFlow.TangentFrame(Site);
        double r = TerrainHeightField.RadiusMeters;
        Double3 At(double x, double y) => (Site + ax * (x / r) + ay * (y / r)).Normalized();
        // Plaine légèrement inclinée vers +y ; fissure de 6 km le long de x
        var flow = new LavaFlow(Site, d => 500 - 0.01 * d.Dot(ay) * r);
        flow.SetFissure(At(-3000, 0), At(3000, 0));
        Assert.True(flow.SourceCellCount > 100, $"la lave sort tout le long : {flow.SourceCellCount} cellules");
        flow.Step(600, 250);
        int west = 0, east = 0;
        for (int j = 0; j < LavaFlow.Size; j++)
            for (int i = 0; i < LavaFlow.Size; i++)
                if (flow.Thickness(i, j) + flow.Basalt(i, j) > 0.1)
                {
                    if (i < LavaFlow.Size / 2 - 40) west++;
                    if (i > LavaFlow.Size / 2 + 40) east++;
                }
        Assert.True(west > 50 && east > 50, "de la lave aux deux bouts de la fissure, pas seulement au milieu");

        // Coulées durables : elles partent de toute la fissure
        var patch = VolcanicDeposits.NewPatch(Site, false, 0);
        var layer = VolcanicDeposits.Lava(patch, d => 500 - 0.02 * d.Dot(ay) * r, 0, 3, out double placed, (At(-4000, 0), At(4000, 0)));
        Console.WriteLine($"     éruption fissurale : {placed / 1e6:0} millions de m³ de lave posés");
        Assert.True(placed > 5e7, "une éruption fissurale émet bien plus qu'une petite éruption de bouclier");
        double westFlows = 0, eastFlows = 0;
        int n = patch.Size;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double x = patch.Project(patch.DirectionOf(i, j)).x;
                if (x < -2000) westFlows += layer[j * n + i];
                if (x > 2000) eastFlows += layer[j * n + i];
            }
        Assert.True(westFlows > 0 && eastFlows > 0, "des coulées partent des deux moitiés de la fissure");
    }

    [Test]
    public static void DryingMudCracksAndRainClosesIt()
    {
        var ground = new SurfacePonding();
        ground.Step(3, 80, 1, 22);          // averse sur sol saturé : flaques, boue
        Assert.True(ground.Mud > 0.9 && ground.Cracks < 0.05, "sous la pluie : de la boue, pas de fentes");
        for (int h = 0; h < 72; h++)
            ground.Step(1, 0, 0.6, 28);      // trois jours de sécheresse au chaud
        Console.WriteLine($"     après 3 jours au chaud : fentes {ground.Cracks:0.00}, boue {ground.Mud:0.00}");
        Assert.True(ground.Cracks > 0.5, "la boue séchée se fend en polygones");
        ground.Step(3, 80, 1, 22);
        Assert.True(ground.Cracks < 0.1, "la pluie suivante referme les fentes");

        var cold = new SurfacePonding();
        cold.Step(3, 80, 1, 8);
        for (int h = 0; h < 72; h++)
            cold.Step(1, 0, 0.6, 4);
        Assert.True(cold.Cracks < 0.05, "au froid, la boue sèche à peine : pas de fentes");
    }
}
