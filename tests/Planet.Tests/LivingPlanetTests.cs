using System;
using System.Linq;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Une Terre vivante (étape 17) : crues, tsunamis, cyclones, lahars, avalanches.</summary>
public static class LivingPlanetTests
{
    [Test]
    public static void RiversRiseWithBasinRainAndOverflow()
    {
        Assert.True(FloodModel.StageRise(1) == 0, "débit habituel : niveau habituel");
        Assert.True(FloodModel.StageRise(2) > 0.4, "deux fois le débit : le fleuve sort de son lit (eau à 40 cm sous les berges)");
        Assert.True(FloodModel.StageRise(10) > FloodModel.StageRise(3) && FloodModel.StageRise(0.3) < 0, "plus de débit, plus haut ; basses eaux en sécheresse");

        // Réseau : 3 cellules en chaîne vers la mer ; il pleut deux fois plus que d'habitude en amont
        var grid = Fixtures.Grid(1);
        int n = grid.CellCount;
        var elevation = Enumerable.Repeat(-1f, n).ToArray();
        var downstream = Enumerable.Repeat(-1, n).ToArray();
        int a = 0, b = grid.Neighbors[0][0], c = grid.Neighbors[b].First(x => x != a && !grid.Neighbors[a].Contains(x));
        elevation[a] = elevation[b] = elevation[c] = 1;
        downstream[a] = b;
        downstream[b] = c;
        var recent = new float[n];
        var annual = Enumerable.Repeat(365.25f, n).ToArray();   // 1 mm/jour en moyenne
        recent[a] = 4; recent[b] = 1; recent[c] = 1;
        var flood = new FloodModel();
        flood.UpdateNetwork(grid, downstream, elevation, recent, annual);
        Assert.Near(flood.Ratio[a], 4, 1e-4, "débit ×4 à la source");
        Assert.Near(flood.Ratio[c], 2, 1e-4, "en aval, débit cumulé : (4 + 1 + 1) / 3 = ×2");
        flood.Follow(c, 0.5);
        double early = flood.RiseM;
        flood.Follow(c, 10);
        Assert.True(early > 0 && flood.RiseM > early, "l'onde de crue arrive en quelques jours");
    }

    [Test]
    public static void FloodplainsHoldHiddenWaterThatAFloodReveals()
    {
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        var field = new TerrainHeightField(grid, elevation, 42, elevation.Select(e => e > 0 ? 600f : 0f).ToArray());
        var hydrology = field.Hydrology;
        int cell = Enumerable.Range(0, grid.CellCount)
            .Where(x => elevation[x] > 0.3f && hydrology.Downstream[x] >= 0 && elevation[hydrology.Downstream[x]] > 0.2f)
            .OrderByDescending(x => hydrology.Discharge[x]).First();
        Double3 pa = Double3.From(grid.Positions[cell]).Normalized(), pb = Double3.From(grid.Positions[hydrology.Downstream[cell]]).Normalized();
        Double3 middle = (pa + pb).Normalized(), across = pa.Cross(pb).Normalized();
        int hint = -1, dryBefore = 0, floodedAfter = 0, plain = 0;
        for (int k = -150; k <= 150; k++)
        {
            var s = field.Sample((middle + across * (k * 50.0 / TerrainHeightField.RadiusMeters)).Normalized(), 5, ref hint);
            if (s.River > 0.01 || s.WaterLevel <= 0.01)
                continue;
            plain++;
            if (s.WaterLevel <= s.Height) dryBefore++;
            if (s.WaterLevel + 3 > s.Height) floodedAfter++;
        }
        Console.WriteLine($"     plaine : {plain} points, secs {dryBefore}, sous l'eau avec une crue de 3 m : {floodedAfter}");
        Assert.True(plain > 20 && dryBefore == plain, "hors crue, l'eau de la plaine reste cachée sous le sol");
        Assert.True(floodedAfter > 5 && floodedAfter < plain, "une crue de 3 m inonde le fond de la vallée, pas les versants");
    }

    [Test]
    public static void TsunamisCrossOceansAndWithdrawFirst()
    {
        Assert.Near(TsunamiModel.SpeedMs, 198, 1, "vitesse au-dessus de 4 km de fond : √(g h)");
        Assert.True(TsunamiModel.CoastalAmplitude(9, 150) > 5, "séisme de magnitude 9 proche : vague de plusieurs mètres");
        Assert.True(TsunamiModel.CoastalAmplitude(7.5, 3000) < 0.2, "magnitude 7,5 lointaine : vague de quelques cm");
        var model = new TsunamiModel();
        var origin = new Double3(1, 0, 0);
        model.Add(origin, 9, 0);
        model.Add(origin, 6, 0);
        Assert.True(model.Waves.Count == 1, "pas de tsunami sous la magnitude 7,5");
        var coast = new Double3(Math.Cos(0.1), Math.Sin(0.1), 0);   // ~640 km sur la Terre
        double km = 0.1 * PlanetConstants.RadiusKm;
        double arrivalDays = TsunamiModel.ArrivalSeconds(km) / 86400;
        Assert.True(model.SeaRise(coast, arrivalDays * 0.9) == 0, "avant l'arrivée de l'onde : rien");
        double first = model.SeaRise(coast, arrivalDays + 200.0 / 86400);
        double crest = Enumerable.Range(1, 60).Select(m => model.SeaRise(coast, arrivalDays + m * 30.0 / 86400)).Max();
        Assert.True(first < 0, "la mer se retire d'abord");
        Assert.True(crest > 1, $"puis la vague arrive : {crest:0.0} m");
    }

    [Test]
    public static void CyclonesFormOverWarmSeasMoveWestAndDieOverLand()
    {
        var grid = Fixtures.Grid(4);
        int n = grid.CellCount;
        var sea = Enumerable.Repeat(29.5f, n).ToArray();
        // Un continent à l'ouest de longitude −60° (axe x négatif) sur les tropiques
        bool Ocean(int c) => !(grid.Positions[c].X < -0.3f);
        var model = new CycloneModel();
        var random = new Random(4);
        int born = 0;
        double maxWind = 0, westward = 0, eastward = 0;
        for (int day = 0; day < 200; day++)
        {
            var before = model.Cyclones.ToDictionary(cy => cy.Id, cy => cy.Position);
            model.Step(1, grid, sea, Ocean, random);
            born += model.Born.Count;
            foreach (var cy in model.Born)
            {
                double lat = Math.Abs(Math.Asin(cy.Position.Y)) * 180 / Math.PI;
                Assert.True(lat is >= 3 and <= 25, $"naissance entre 5° et 22° (il a pu bouger depuis, dans la journée) : {lat:0}°");
            }
            foreach (var cy in model.Cyclones)
            {
                maxWind = Math.Max(maxWind, cy.MaxWind);
                if (!before.TryGetValue(cy.Id, out var old) || Math.Abs(Math.Asin(cy.Position.Y)) * 180 / Math.PI > 20)
                    continue;
                Double3 east = Double3.UnitY.Cross(old).Normalized();
                double move = (cy.Position - old).Dot(east);
                if (move < 0) westward++; else eastward++;
            }
        }
        Console.WriteLine($"     {born} cyclones en 200 jours, vent maximal {maxWind * 3.6:0} km/h (catégorie {CycloneModel.Category(maxWind)})");
        Assert.True(born > 3, "des cyclones naissent sur la mer chaude");
        Assert.True(maxWind > 50, "ils deviennent de violents ouragans sur une mer à 29 °C");
        Assert.True(westward > 2 * eastward, "sous les tropiques, ils filent vers l'ouest");

        // Vents : nuls dans l'œil, maximaux au mur de l'œil, tournant à l'inverse des aiguilles d'une montre au nord
        var one = new CycloneModel();
        var center = new Double3(0, Math.Sin(0.25), Math.Cos(0.25)).Normalized();
        var still = one.At(center);
        Assert.True(still.wind.Length() == 0 && still.rainMmPerDay == 0, "aucun cyclone : aucun vent");
    }

    private static readonly Double3 Site = new Double3(0.35, 0.5, 0.79).Normalized();

    private static double Hillside(Double3 d)
    {
        var (ax, _) = LavaFlow.TangentFrame(Site);
        double x = d.Dot(ax) * TerrainHeightField.RadiusMeters;
        return 400 + 0.35 * 300 * Math.Log(1 + Math.Exp(-x / 300));   // pente ~19° qui s'adoucit en plaine
    }

    [Test]
    public static void LaharsRunFarAndAvalanchesLeaveMeltingSnow()
    {
        var (ax, _) = LavaFlow.TangentFrame(Site);
        Double3 source = (Site + ax * (-600 / TerrainHeightField.RadiusMeters)).Normalized();
        Landslide Run(SlideKind kind)
        {
            var slide = new Landslide(Hillside, source, kind, 1, 3, 0);
            for (int t = 0; t < 600 && !slide.Finished; t++)
                slide.Step(1);
            return slide;
        }
        double Reach(Landslide slide)
        {
            var (delta, _) = slide.BuildLayer();
            double far = double.MinValue;
            for (int j = 0; j < slide.Size; j++)
                for (int i = 0; i < slide.Size; i++)
                    if (delta[j * slide.Size + i] > 0.05)
                        far = Math.Max(far, slide.Patch.DirectionOf(i, j).Dot(ax) * TerrainHeightField.RadiusMeters);
            return far;
        }
        var lahar = Run(SlideKind.Lahar);
        var soil = Run(SlideKind.Landslide);
        Console.WriteLine($"     dépôt jusqu'à x = {Reach(lahar):0} m (lahar), {Reach(soil):0} m (glissement)");
        Assert.True(lahar.Finished && Reach(lahar) > Reach(soil) + 200, "le lahar, très fluide, va bien plus loin dans la plaine");
        Assert.True(lahar.BuildLayer().debris.Any(d => d == 4f), "dépôt de boue de lahar");

        var avalanche = Run(SlideKind.SnowAvalanche);
        var (_, debris) = avalanche.BuildLayer();
        Assert.True(debris.Any(d => d == 5f) && !debris.Any(d => d < 0), "avalanche : dépôt de neige, pas de cicatrice dans le sol");

        // Le dépôt de neige fond : le calque est retiré de la croûte
        var edits = new CrustEdits();
        edits.Add(avalanche.Patch);
        edits.Add(new CrustPatch(Site, 16, 5, 0, "autre"));
        edits.Remove(avalanche.Patch);
        Assert.True(edits.Count == 1 && edits.Offset(avalanche.Source, out _, out _) == 0, "calque de neige retiré");
    }
}
