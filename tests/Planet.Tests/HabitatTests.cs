using System;
using System.Linq;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Tectonics;

namespace Planet.Tests;

/// <summary>Milieux hydrothermaux (étape 14a).</summary>
public static class HabitatTests
{
    private static readonly Lazy<TectonicPlates> Evolved = new(() =>
    {
        var tectonics = TectonicPlates.Generate(Fixtures.Grid(5), 12, 3);
        while (tectonics.TimeMyr < 60)
            tectonics.Step(tectonics.MaxTimeStep());
        return tectonics;
    });

    [Test]
    public static void HabitatsFollowWaterAndHeat()
    {
        var tectonics = Evolved.Value;
        var wet = new VolcanicHabitats(tectonics, Enumerable.Repeat(1200f, tectonics.Grid.CellCount).ToArray());
        var dry = new VolcanicHabitats(tectonics, Enumerable.Repeat(50f, tectonics.Grid.CellCount).ToArray());
        Console.WriteLine("     planète arrosée : " + string.Join(", ", Enum.GetValues<HabitatKind>().Select(k => $"{wet.Count(k)} {VolcanicHabitats.NameOf(k, true)}")));
        Assert.True(wet.Count(HabitatKind.HotSpring) > 20 && wet.Count(HabitatKind.Geyser) > 0, "une planète arrosée a des sources chaudes et des geysers");
        Assert.True(wet.Count(HabitatKind.Geyser) < wet.Count(HabitatKind.HotSpring) / 2, "les geysers sont rares");
        Assert.True(dry.Count(HabitatKind.Geyser) == 0 && dry.Count(HabitatKind.MudPot) == 0, "sans pluie, ni geysers ni mares");
        Assert.True(dry.Count(HabitatKind.Fumarole) > wet.Count(HabitatKind.Fumarole), "sans eau, il ne reste que des fumerolles");
        Assert.True(wet.Count(HabitatKind.BlackSmoker) > 20 && wet.Count(HabitatKind.AlkalineVent) > 0, "dorsales : fumeurs noirs et sources alcalines");
        Assert.True(wet.SubmarineCount == dry.SubmarineCount, "la pluie ne change rien sous la mer");

        foreach (var h in wet.All)
        {
            Assert.True(h.Cell == tectonics.Grid.NearestCell(h.Position, h.Cell), "chaque milieu est rangé dans sa cellule");
            if (h.Submarine)
                Assert.True(h.AltitudeM < 0, "milieu sous-marin au-dessus de la mer");
            else
            {
                Assert.True(h.AltitudeM >= 0, "milieu terrestre sous la mer");
                if (h.Kind != HabitatKind.Fumarole)
                    Assert.True(h.TemperatureC <= VolcanicHabitats.BoilingPoint(h.AltitudeM) + 0.01f, $"eau au-dessus de l'ébullition : {h.TemperatureC} °C à {h.AltitudeM} m");
            }
            Assert.True(h.Ph is > 0.5f and < 12f, $"pH invraisemblable : {h.Ph}");
        }
        int withHabitats = Enumerable.Range(0, tectonics.Grid.CellCount).Count(c => wet.InCell(c).Length > 0);
        Assert.True(withHabitats > 50 && wet.Describe(wet.All[0].Cell) != null, "fiche des milieux d'une cellule");
    }

    [Test]
    public static void HabitatsAreStableAndScaleWithAbundance()
    {
        var tectonics = Evolved.Value;
        var rain = Enumerable.Repeat(900f, tectonics.Grid.CellCount).ToArray();
        var a = new VolcanicHabitats(tectonics, rain);
        var b = new VolcanicHabitats(tectonics, rain);
        Assert.True(a.All.Count == b.All.Count && a.All.Zip(b.All).All(p => p.First.Position == p.Second.Position && p.First.Kind == p.Second.Kind),
            "mêmes milieux d'un calcul à l'autre");
        var none = new VolcanicHabitats(tectonics, rain, 0);
        var half = new VolcanicHabitats(tectonics, rain, 0.5);
        var twice = new VolcanicHabitats(tectonics, rain, 2);
        Console.WriteLine($"     milieux : abondance 0 : {none.All.Count}, 0,5 : {half.All.Count}, 1 : {a.All.Count}, 2 : {twice.All.Count}");
        Assert.True(none.All.Count == 0, "abondance nulle : aucun milieu");
        Assert.True(half.All.Count < a.All.Count && a.All.Count < twice.All.Count, "le nombre de milieux suit l'abondance");
    }

    [Test]
    public static void GeysersEruptPeriodically()
    {
        var geyser = new Habitat { Kind = HabitatKind.Geyser, PeriodMinutes = 60, JetHeightM = 40, Variation = 0 };
        Assert.True(geyser.GeyserActivity(1) > 0.5f, "en éruption au début de la période");
        Assert.True(geyser.GeyserActivity(10) == 0f && geyser.GeyserActivity(59) == 0f, "au repos ensuite");
        Assert.True(geyser.GeyserActivity(61) > 0.5f, "nouvelle éruption une période plus tard");
        double active = Enumerable.Range(0, 6000).Count(i => geyser.GeyserActivity(i * 0.1) > 0) / 6000.0;
        Assert.Near(active, VolcanicHabitats.GeyserEruptionMinutes / 60, 0.01, "part du temps en éruption");
    }

    [Test]
    public static void PoolsSitOnLevelBenches()
    {
        // L'eau ne tient pas sur une pente : chaque bassin est posé sur un replat horizontal
        var tectonics = Evolved.Value;
        var habitats = new VolcanicHabitats(tectonics, Enumerable.Repeat(1200f, tectonics.Grid.CellCount).ToArray());
        var landforms = tectonics.SnapshotLandforms();
        var natural = new Planet.Simulation.Terrain.TerrainHeightField(tectonics.Grid, tectonics.CellElevation, 3, null, landforms);
        var field = new Planet.Simulation.Terrain.TerrainHeightField(tectonics.Grid, tectonics.CellElevation, 3, null, landforms, habitats.Benches());
        int checkedPools = 0;
        double steepest = 0;
        foreach (var h in habitats.All.Where(h => h.Kind is HabitatKind.HotSpring or HabitatKind.Geyser).Take(60))
        {
            var center = Planet.Simulation.Terrain.Double3.From(h.Position).Normalized();
            int hint = -1;
            double floor = field.BenchFloor(center, ref hint);
            if (double.IsNaN(floor))
                continue;   // (site au ras de la mer sur le relief détaillé : pas de bassin)
            checkedPools++;
            var east = Planet.Simulation.Terrain.Double3.UnitY.Cross(center).Normalized();
            var north = center.Cross(east);
            double before = 0;
            for (int k = 0; k < 8; k++)
            {
                var side = east * Math.Cos(k * Math.PI / 4) + north * Math.Sin(k * Math.PI / 4);
                Planet.Simulation.Terrain.Double3 At(double radii) =>
                    (center + side * (radii * h.BenchRadiusM / Planet.Simulation.Terrain.TerrainHeightField.RadiusMeters)).Normalized();
                Assert.Near(field.Height(At(0.6), 1, ref hint), floor, 0.01, "le fond du bassin est horizontal");
                Assert.Near(field.Height(At(1.6), 1, ref hint), floor + Planet.Simulation.Terrain.TerrainHeightField.BenchBasinDepthM, 0.01, "la terrasse est horizontale");
                Assert.Near(field.Height(At(4.0), 1, ref hint), natural.Height(At(4.0), 1, ref hint), 1e-6, "au-delà du replat, le relief est intact");
                before = Math.Max(before, Math.Abs(natural.Height(At(0.6), 1, ref hint) - natural.Height(center, 1, ref hint)));
            }
            steepest = Math.Max(steepest, before);
        }
        Console.WriteLine($"     {checkedPools} bassins vérifiés ; avant, le terrain y variait jusqu'à {steepest:0.0} m d'un bord à l'autre");
        Assert.True(checkedPools > 20, "trop peu de bassins vérifiés");
    }
}
