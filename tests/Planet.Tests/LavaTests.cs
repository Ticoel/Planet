using System;
using System.Diagnostics;
using Planet.Simulation;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Coulées de lave simulées comme un fluide (automate cellulaire).</summary>
public static class LavaTests
{
    private static readonly Double3 VentDirection = new Double3(0.3, 0.5, 0.81).Normalized();

    /// <summary>Coordonnées (m) dans le plan tangent à la bouche.</summary>
    private static (double x, double y) Local(Double3 direction)
    {
        var (ax, ay) = LavaFlow.TangentFrame(VentDirection);
        return (direction.Dot(ax) * TerrainHeightField.RadiusMeters, direction.Dot(ay) * TerrainHeightField.RadiusMeters);
    }

    [Test]
    public static void VolumeIsConserved()
    {
        // Cône : 2 000 m au sommet, pente 10 %
        var flow = new LavaFlow(VentDirection, d =>
        {
            var (x, y) = Local(d);
            return 2000 - 0.1 * Math.Sqrt(x * x + y * y);
        });
        flow.Step(1500, 50);
        flow.Step(500, 0);
        double total = flow.VolumeOnGrid() + flow.LostVolume;
        Console.WriteLine($"    émis {flow.ErupedVolume / 1e6:0.00} Mm³, sur la grille {flow.VolumeOnGrid() / 1e6:0.00}, sortis {flow.LostVolume / 1e6:0.00}");
        Assert.True(Math.Abs(total - flow.ErupedVolume) < 1e-6 * flow.ErupedVolume + 1, "le volume de lave n'est pas conservé");
    }

    [Test]
    public static void LavaFollowsTheValleyDownhill()
    {
        // Pente de 8 % vers +x, vallée en V le long de l'axe x (bords à 3 %)
        var flow = new LavaFlow(VentDirection, d =>
        {
            var (x, y) = Local(d);
            return 1000 - 0.08 * x + 0.03 * Math.Abs(y);
        });
        flow.Step(2000, 30);
        var (vi, vj) = flow.VentCell;
        double downhill = 0, uphill = 0, inValley = 0, outside = 0;
        int frontX = 0;
        for (int j = 0; j < LavaFlow.Size; j++)
            for (int i = 0; i < LavaFlow.Size; i++)
            {
                double lava = flow.Thickness(i, j) + flow.Basalt(i, j);
                if (lava <= 0.01)
                    continue;
                int dx = i - vi, dy = j - vj;
                if (dx > 3) downhill += lava; else if (dx < -3) uphill += lava;
                if (Math.Abs(dy) <= 10) inValley += lava; else outside += lava;
                frontX = Math.Max(frontX, dx);
            }
        Console.WriteLine($"    front à {frontX * LavaFlow.CellM / 1000:0.0} km, aval {downhill:0} / amont {uphill:0}, dans la vallée {inValley / (inValley + outside) * 100:0} %");
        Assert.True(uphill < 0.01 * downhill, "la lave ne doit pas remonter la pente");
        Assert.True(inValley > 0.8 * (inValley + outside), "la lave doit rester dans la vallée");
        Assert.True(frontX * LavaFlow.CellM > 1200, "la coulée devrait avoir parcouru plus de 1,2 km");
    }

    [Test]
    public static void LavaFillsABasinThenFreezes()
    {
        // Cuvette : la lave s'étale en lac, puis se fige quand l'éruption cesse
        var flow = new LavaFlow(VentDirection, d =>
        {
            var (x, y) = Local(d);
            double r = Math.Sqrt(x * x + y * y);
            return 200 + 0.05 * Math.Max(0, r - 1500);
        });
        flow.Step(1000, 40);
        int liquid = 0;
        for (int j = 0; j < LavaFlow.Size; j++)
            for (int i = 0; i < LavaFlow.Size; i++)
                if (flow.Thickness(i, j) > 0) liquid++;
        flow.Step(30000, 0);   // ~4 jours
        double stillLiquid = 0, basalt = 0;
        for (int j = 0; j < LavaFlow.Size; j++)
            for (int i = 0; i < LavaFlow.Size; i++)
            {
                stillLiquid += flow.Thickness(i, j);
                basalt += flow.Basalt(i, j);
            }
        Console.WriteLine($"    {liquid} cellules en fusion, puis {basalt * LavaFlow.CellM * LavaFlow.CellM / 1e6:0.00} Mm³ de basalte, {stillLiquid:0.0} m encore liquides");
        Assert.True(liquid > 50, "la lave devrait s'étaler");
        Assert.True(flow.LostVolume == 0, "la lave ne doit pas sortir de la cuvette");
        Assert.True(stillLiquid < 0.05 * basalt, "la lave devrait s'être figée");
    }

    [Test]
    public static void StepsAreFastEnough()
    {
        var flow = new LavaFlow(VentDirection, d =>
        {
            var (x, y) = Local(d);
            return 1500 - 0.06 * Math.Sqrt(x * x + y * y) + 20 * Math.Sin(x / 300) * Math.Cos(y / 250);
        });
        flow.Step(3000, 80);   // grande coulée déjà étalée
        var watch = Stopwatch.StartNew();
        flow.Step(200, 80);
        watch.Stop();
        double perStep = watch.Elapsed.TotalMilliseconds / 200;
        var pixels = new byte[LavaFlow.Size * LavaFlow.Size * 2];
        flow.FillTexture(pixels);
        Console.WriteLine($"    {perStep:0.00} ms par pas");
        Assert.True(perStep < 2.0, $"pas trop lent : {perStep:0.00} ms");
    }
}
