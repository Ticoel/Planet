using System;
using System.Linq;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Eau souterraine et sources d'eau douce (étape 17).</summary>
public static class GroundwaterTests
{
    [Test]
    public static void WetLandsHaveShallowTablesAndSpringsDesertsDeepOnes()
    {
        var grid = Fixtures.Grid(4);
        int n = grid.CellCount;
        var elevation = Enumerable.Repeat(0.4f, n).ToArray();
        var rain = new float[n];
        var temperature = Enumerable.Repeat(15f, n).ToArray();
        var carbon = Enumerable.Repeat(500f, n).ToArray();
        for (int c = 0; c < n; c++)
        {
            bool north = grid.Positions[c].Y > 0;
            rain[c] = north ? 1500 : 80;                       // nord humide, sud désertique
            if (north && grid.Positions[c].X > 0.5f) carbon[c] = 20000;   // calcaires
        }
        var water = new Groundwater(grid, elevation, rain, temperature, carbon, null, 7);
        var wet = Enumerable.Range(0, n).Where(c => rain[c] > 1000 && !water.Karst[c]).ToArray();
        var dry = Enumerable.Range(0, n).Where(c => rain[c] < 100).ToArray();
        Assert.True(wet.Average(c => water.TableDepthM[c]) < 10, "pluie abondante : nappe à quelques mètres");
        Assert.True(dry.Average(c => water.TableDepthM[c]) > 40, "désert : nappe profonde");
        Assert.True(dry.All(c => water.Recharge[c] == 0), "désert : presque rien ne s'infiltre");
        Assert.True(water.Springs.Any(s => s.Kind == SpringKind.Slope), "des sources dans les terres humides");
        Assert.True(water.Springs.All(s => rain[s.Cell] > 1000), "pas de source en désert");
        var karst = water.Springs.Where(s => s.Kind == SpringKind.Karst).ToArray();
        Assert.True(karst.Length > 0 && karst.All(s => water.Karst[s.Cell]), "résurgences dans les calcaires");
        Assert.True(karst.Average(s => s.FlowLs) > water.Springs.Where(s => s.Kind == SpringKind.Slope).Average(s => s.FlowLs) * 10,
            "une résurgence débite bien plus qu'une petite source");
        Assert.True(water.Benches().All(b => b.Water), "les vasques des sources sont pleines d'eau");
        Assert.True(water.Describe(wet[0]).Contains("nappe"), "lisible à la sonde");
    }
}
