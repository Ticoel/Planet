using System;
using System.Linq;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Planète active (étape 13) : volcans, cratères, séismes, éruptions, glaciations.</summary>
public static class ActivePlanetTests
{
    /// <summary>Planète de niveau 5 après 60 Ma d'évolution (points chauds, arcs, impacts).</summary>
    private static readonly Lazy<TectonicPlates> Evolved = new(() =>
    {
        var tectonics = TectonicPlates.Generate(Fixtures.Grid(5), 12, 3);
        while (tectonics.TimeMyr < 60)
            tectonics.Step(tectonics.MaxTimeStep());
        return tectonics;
    });

    [Test]
    public static void HotspotsLeaveChainsOfAgingVolcanoes()
    {
        var tectonics = Evolved.Value;
        var shields = tectonics.Landforms.Where(l => l.Kind == LandformKind.ShieldVolcano).ToArray();
        int active = shields.Count(l => l.Active);
        Console.WriteLine($"     {shields.Length} volcans de point chaud, dont {active} actifs ; âge du plus vieux {shields.Max(l => tectonics.TimeMyr - l.BirthMyr):0} Ma");
        Assert.True(active >= tectonics.Hotspots.Count * 0.8, "un volcan actif au-dessus de (presque) chaque point chaud");
        Assert.True(shields.Length > 2 * active, "des volcans éteints emportés par les plaques (chaînes)");
        // Les volcans s'éloignent de leur panache en vieillissant : âge et distance au point
        // chaud le plus proche sont liés (corrélation de rang) ; les vieux sont aussi plus érodés
        var age = shields.Select(l => tectonics.TimeMyr - l.BirthMyr).ToArray();
        var distance = shields.Select(l => tectonics.Hotspots.Min(h => (double)l.Position.AngleTo(h))).ToArray();
        double rank = SpearmanCorrelation(age, distance);
        Console.WriteLine($"     corrélation âge / distance au panache : {rank:0.00}");
        Assert.True(rank > 0.5, "les volcans les plus vieux sont les plus loin de leur point chaud");
        var old = shields.Where(l => tectonics.TimeMyr - l.BirthMyr > 20).ToArray();
        Assert.True(old.Length > 0 && old.Average(l => l.Freshness) < 0.5f, "et érodés");
    }

    private static double SpearmanCorrelation(double[] x, double[] y)
    {
        double[] Rank(double[] v)
        {
            var order = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ToArray();
            var r = new double[v.Length];
            for (int i = 0; i < order.Length; i++) r[order[i]] = i;
            return r;
        }
        double[] rx = Rank(x), ry = Rank(y);
        double mx = rx.Average(), my = ry.Average();
        double cov = 0, vx = 0, vy = 0;
        for (int i = 0; i < x.Length; i++) { cov += (rx[i] - mx) * (ry[i] - my); vx += (rx[i] - mx) * (rx[i] - mx); vy += (ry[i] - my) * (ry[i] - my); }
        return cov / Math.Sqrt(vx * vy);
    }

    [Test]
    public static void ArcVolcanoesSitAboveSubductionZones()
    {
        var tectonics = Evolved.Value;
        var arcs = tectonics.Landforms.Where(l => l.Kind == LandformKind.Stratovolcano && l.Active).ToArray();
        int nearBoundary = 0;
        foreach (var volcano in arcs)
        {
            int cell = tectonics.Grid.NearestCell(volcano.Position, 0);
            bool near = tectonics.CellBoundary[cell] == BoundaryType.Convergent
                        || tectonics.Grid.Neighbors[cell].Any(n => tectonics.CellBoundary[n] == BoundaryType.Convergent);
            if (near) nearBoundary++;
        }
        Console.WriteLine($"     {arcs.Length} volcans d'arc actifs, {nearBoundary} à côté d'une frontière convergente");
        Assert.True(arcs.Length >= 10, "des volcans d'arc");
        Assert.True(nearBoundary >= arcs.Length * 0.9, "au-dessus des subductions");
    }

    [Test]
    public static void ImpactRateFollowsTheLunarChronology()
    {
        // Aujourd'hui (4,54 Ga) : taux de référence ; il y a 3,9 Ga (t = 0,64 Ga), des centaines de fois plus
        double now = TectonicPlates.ImpactRateFactor(4540), early = TectonicPlates.ImpactRateFactor(640);
        Console.WriteLine($"     taux d'impacts relatif : aujourd'hui {now:0.00}, il y a 3,9 Ga ×{early:0}, il y a 3 Ga ×{TectonicPlates.ImpactRateFactor(1540):0.0}");
        Assert.Near(now, 1, 1e-9, "taux actuel");
        Assert.True(early > 100, "bombardement intense des débuts");
        Assert.InRange(TectonicPlates.ImpactRateFactor(1540), 1.0, 3.0, "presque calme dès 3 Ga");
        // Loi de taille : N(>D) ∝ D^−1,8 : quatre fois plus grand → ~12 fois plus rare
        var random = new Random(5);
        var sizes = Enumerable.Range(0, 200000).Select(_ => TectonicPlates.CraterDiameterKm(random.NextDouble())).ToArray();
        double ratio = sizes.Count(d => d > 10) / (double)sizes.Count(d => d > 40);
        Assert.Near(ratio, Math.Pow(4, 1.8), 1.5, "N(>10 km) / N(>40 km)");
        Assert.Near(TectonicPlates.CraterDepthM(1), 196, 1, "profondeur d'un cratère de 1 km (Pike)");
    }

    [Test]
    public static void VolcanoesAndCratersShapeTheDetailedRelief()
    {
        var grid = Fixtures.Grid(5);
        var elevation = Enumerable.Repeat(0.3f, grid.CellCount).ToArray();
        var top = new Vector3(0.2f, 0.5f, 0.84f).Normalized();
        var crater = new Vector3(-0.6f, 0.1f, 0.79f).Normalized();
        var landforms = new[]
        {
            new Landform { Kind = LandformKind.Stratovolcano, Position = top, RadiusM = 15000, PeakHeightM = 2500, Growth = 1 },
            new Landform { Kind = LandformKind.ImpactCrater, Position = crater, RadiusM = 10000, PeakHeightM = TectonicPlates.CraterDepthM(20), Growth = 1 },
        };
        var plain = new TerrainHeightField(grid, elevation, 1);
        var field = new TerrainHeightField(grid, elevation, 1, null, landforms);
        double Rise(Vector3 at, double offsetM)
        {
            var d = Double3.From(at).Normalized();
            var east = Double3.UnitY.Cross(d).Normalized();
            var p = (d + east * (offsetM / TerrainHeightField.RadiusMeters)).Normalized();
            int h1 = -1, h2 = -1;
            return field.Sample(p, 50, ref h1).Height - plain.Sample(p, 50, ref h2).Height;
        }
        double summit = Rise(top, 2000), flank = Rise(top, 8000), outside = Rise(top, 20000);
        double floor = Rise(crater, 0), rim = Rise(crater, 10000), far = Rise(crater, 40000);
        Console.WriteLine($"     volcan : {summit:0} m près du sommet, {flank:0} m sur le flanc, {outside:0} m au-delà ; cratère : fond {floor:0} m, rempart {rim:0} m");
        Assert.True(summit > 1500 && flank > 150 && flank < summit && Math.Abs(outside) < 1, "cône du volcan");
        Assert.True(floor < -500 && rim > 100 && Math.Abs(far) < 1, "cuvette et rempart du cratère");
    }

    [Test]
    public static void EarthquakesFollowGutenbergRichter()
    {
        var activity = new PlanetActivity(2);
        activity.SetPlanet(Evolved.Value);
        var magnitudes = new System.Collections.Generic.List<float>();
        double day = 0;
        for (int k = 0; k < 4000; k++)
        {
            day += 1;
            activity.Step(day, 1);
            magnitudes.AddRange(activity.RecentEarthquakes.Select(e => e.Magnitude));
        }
        double years = day / 365.25;
        double perYear = magnitudes.Count / years, above6 = magnitudes.Count(m => m >= 6) / years, above7 = magnitudes.Count(m => m >= 7) / years;
        Console.WriteLine($"     séismes par an : M≥5 {perYear:0}, M≥6 {above6:0}, M≥7 {above7:0.0}");
        Assert.Near(perYear, PlanetActivity.EarthquakesAbove5PerYear, 100, "séismes de magnitude 5 et plus par an");
        Assert.Near(perYear / above6, 10, 1.5, "dix fois moins de M≥6 (b = 1)");
        Assert.InRange(PlanetActivity.FeltIntensity(6.5f, 20), 6, 9, "M 6,5 à 20 km : fort à destructeur");
        Assert.InRange(PlanetActivity.FeltIntensity(6.5f, 800), 0, 3.5, "à 800 km : à peine ressenti");
    }

    [Test]
    public static void BigEruptionsDimTheSunForAboutAYear()
    {
        Assert.Near(PlanetActivity.AerosolInjection(6), 3, 1e-6, "Pinatubo : ~3 W/m²");
        Assert.True(PlanetActivity.AerosolInjection(3) == 0f, "une petite éruption reste dans la troposphère");
        var activity = new PlanetActivity(4);
        activity.SetPlanet(Evolved.Value);
        int eruptions = 0;
        for (int day = 1; day <= 3650; day++)
        {
            activity.Step(day, 1);
            eruptions = Math.Max(eruptions, activity.Eruptions.Count);
        }
        Console.WriteLine($"     {activity.ActiveVolcanoCount} volcans actifs, jusqu'à {eruptions} éruptions simultanées en 10 ans");
        Assert.True(eruptions >= 1, "des éruptions");
    }

    [Test]
    public static void MilankovitchCyclesDriveIceAges()
    {
        // Éléments de l'orbite dans les plages terrestres
        var samples = Enumerable.Range(0, 4000).Select(k => GlacialCycles.ElementsAt(k * 0.001)).ToArray();
        Assert.InRange(samples.Min(e => e.Eccentricity), 0.0, 0.01, "excentricité minimale");
        Assert.InRange(samples.Max(e => e.Eccentricity), 0.045, 0.065, "excentricité maximale");
        Assert.InRange(samples.Min(e => e.ObliquityDeg), 21.9, 22.4, "inclinaison minimale");
        Assert.InRange(samples.Max(e => e.ObliquityDeg), 24.2, 24.6, "inclinaison maximale");
        var insolation = samples.Select(GlacialCycles.SummerInsolation65N).ToArray();
        Console.WriteLine($"     été à 65° N : {insolation.Min():0} à {insolation.Max():0} W/m² (moyenne {insolation.Average():0})");
        Assert.InRange(insolation.Average(), 440, 500, "ensoleillement moyen d'été à 65° N");

        // Planète froide : alternance de glaciations et d'interglaciaires ; planète chaude : rien
        var cycles = new GlacialCycles();
        double min = 1, max = 0, lowest = 0;
        for (double t = 1000; t < 1001; t += 0.002)
        {
            cycles.AdvanceTo(t, 12.0);
            min = Math.Min(min, cycles.IceFraction);
            max = Math.Max(max, cycles.IceFraction);
            lowest = Math.Min(lowest, cycles.SeaLevelM);
        }
        Console.WriteLine($"     planète à 12 °C : glaces de {min:0.00} à {max:0.00}, mer jusqu'à {lowest:0} m");
        Assert.True(max - min > 0.5, "glaciations et interglaciaires");
        Assert.InRange(lowest, -135, -70, "mer basse au maximum glaciaire");
        cycles.AdvanceTo(2000, 20.0);
        Assert.Near(cycles.SeaLevelM, 0, 1e-9, "pas de glaciation sur une planète chaude");
    }
}
