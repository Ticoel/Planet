using System;
using System.Collections.Generic;
using System.Linq;
using Planet.Simulation;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Croûte modulaire (étape 16a) : calques de modification, stabilité des pentes, glissements.</summary>
public static class CrustTests
{
    private static readonly Double3 Site = new Double3(0.42, 0.55, 0.72).Normalized();

    /// <summary>Coordonnées (m) dans le plan tangent au site (même repère que les calques).</summary>
    private static (double x, double y) Local(Double3 direction)
    {
        var (ax, ay) = LavaFlow.TangentFrame(Site);
        double r = TerrainHeightField.RadiusMeters / direction.Dot(Site);
        return (direction.Dot(ax) * r, direction.Dot(ay) * r);
    }

    private static Double3 At(double x, double y)
    {
        var (ax, ay) = LavaFlow.TangentFrame(Site);
        double r = TerrainHeightField.RadiusMeters;
        return (Site + ax * (x / r) + ay * (y / r)).Normalized();
    }

    [Test]
    public static void PatchSamplesItsGridAndNothingOutside()
    {
        var patch = new CrustPatch(Site, 64, 5, 0, "test");
        var delta = new float[64 * 64];
        var debris = new float[64 * 64];
        for (int j = 0; j < 64; j++)
            for (int i = 0; i < 64; i++)
            {
                delta[j * 64 + i] = i + 100 * j;
                debris[j * 64 + i] = 0.5f;
            }
        patch.Publish(delta, debris);
        foreach (var (i, j) in new[] { (3, 4), (10, 50), (62, 61), (30, 30) })
        {
            double h = patch.Sample(patch.DirectionOf(i, j), out float d);
            Assert.Near(h, i + 100 * j, 1e-3, $"calque au centre de la cellule ({i}, {j})");
            Assert.Near(d, 0.5, 1e-6, "sol remanié");
        }
        var (x, y) = patch.Project(patch.DirectionAt(123.4, -56.7));
        Assert.Near(x, 123.4, 1e-6, "projection x");
        Assert.Near(y, -56.7, 1e-6, "projection y");
        Assert.True(patch.Sample(patch.DirectionAt(400, 0), out _) == 0, "hors de la grille : rien");
        Assert.True(patch.Sample(-Site, out _) == 0, "de l'autre côté de la planète : rien");

        var edits = new CrustEdits();
        int version = edits.Version;
        edits.Add(patch);
        edits.Add(new CrustPatch(Site, 16, 5, 3, "second"));
        Assert.True(edits.Version > version && edits.Count == 2, "deux calques ajoutés");
        Assert.Near(edits.Offset(patch.DirectionOf(10, 50), out _, out _), 10 + 5000, 1e-3, "les calques s'additionnent");
    }

    [Test]
    public static void HeightFieldIncludesCrustEdits()
    {
        var grid = Fixtures.Grid(4);
        var elevation = Fixtures.DomeContinent(grid, 30, 40, 50, 7);
        var field = new TerrainHeightField(grid, elevation, 7);
        Double3 d = Double3.From(grid.Positions[grid.NearestCell(new Godot.Vector3(0.5f, 0.5f, 0.7f).Normalized(), 0)]).Normalized();
        int hint = -1;
        double before = field.Height(d, 2, ref hint);
        var patch = new CrustPatch(d, 32, 4, 12, "test");
        var delta = Enumerable.Repeat(-7.5f, 32 * 32).ToArray();
        patch.Publish(delta, Enumerable.Repeat(-1f, 32 * 32).ToArray());
        field.Edits = new CrustEdits();
        field.Edits.Add(patch);
        var sample = field.Sample(d, 2, ref hint);
        Assert.Near(sample.Height, before - 7.5, 1e-6, "le relief suit la croûte modulaire");
        Assert.Near(field.BaseTerrainHeight(d, 2, ref hint), before, 1e-9, "relief naturel inchangé");
        Assert.True(sample.Debris < -0.99f && sample.DebrisDay == 12, "sol arraché et date de l'événement");
        Double3 far = (d + new Double3(0.3, -0.2, 0.1)).Normalized();
        Assert.True(field.Sample(far, 2, ref hint).Debris == 0, "loin du calque, sol naturel");
    }

    [Test]
    public static void TerrainChunksShowTheModifiedCrust()
    {
        var grid = Fixtures.Grid(4);
        var elevation = Fixtures.DomeContinent(grid, 30, 40, 50, 7);
        var field = new TerrainHeightField(grid, elevation, 7);
        int top = Enumerable.Range(0, grid.CellCount).OrderByDescending(c => elevation[c]).First();
        Double3 d = Double3.From(grid.Positions[top]).Normalized();
        // Morceau de ~150 m (niveau 16) au milieu d'un calque de 256 m qui abaisse le sol de 10 m
        var (face, s, t) = CubeSphere.FromDirection(d);
        int n = 1 << 16;
        var key = new ChunkKey(face, 16, (int)((s + 1) / 2 * n), (int)((t + 1) / 2 * n));
        var natural = TerrainChunkBuilder.Build(field, key);
        var patch = new CrustPatch(CubeSphere.CenterDirection(key), 64, 4, 3, "test");
        patch.Publish(Enumerable.Repeat(-10f, 64 * 64).ToArray(), Enumerable.Repeat(-1f, 64 * 64).ToArray());
        field.Edits = new CrustEdits();
        field.Edits.Add(patch);
        var modified = TerrainChunkBuilder.Build(field, key);
        for (int v = 0; v < TerrainChunkBuilder.Side * TerrainChunkBuilder.Side; v++)
        {
            Assert.Near(modified.Custom0[v * 4 + 3], natural.Custom0[v * 4 + 3] - 10, 0.01, "altitude du sommet abaissée de 10 m");
            Assert.Near(modified.Custom1[v * 4 + 3], natural.Custom1[v * 4 + 3] - 10, 0.01, "altitude « parent » (morphing) abaissée aussi");
            Assert.True(modified.Debris[v * 2] < -0.99f && modified.Debris[v * 2 + 1] == 3, "sol arraché et date pour le shader");
            Assert.True(natural.Debris[v * 2] == 0, "sans calque, sol naturel");
        }
    }

    [Test]
    public static void SpatialIndexMatchesBruteForce()
    {
        // Des centaines de calques partout sur la planète : l'index doit donner la même somme
        var random = new Random(8);
        var edits = new CrustEdits();
        var patches = new List<CrustPatch>();
        for (int p = 0; p < 300; p++)
        {
            var center = new Double3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5).Normalized();
            var patch = new CrustPatch(center, 32, 50 + 400 * random.NextDouble(), p, "test");
            patch.Publish(Enumerable.Repeat((float)(1 + random.NextDouble()), 32 * 32).ToArray(), new float[32 * 32]);
            edits.Add(patch);
            patches.Add(patch);
        }
        int checkedPoints = 0;
        foreach (var patch in patches.Take(100))
            for (int k = 0; k < 20; k++)
            {
                Double3 d = patch.DirectionAt((random.NextDouble() - 0.5) * 2.2 * patch.HalfExtentM, (random.NextDouble() - 0.5) * 2.2 * patch.HalfExtentM);
                double brute = patches.Sum(p => p.Sample(d, out _));
                Assert.Near(edits.Offset(d, out _, out _), brute, 1e-9, "index spatial = somme de tous les calques");
                if (brute > 0) checkedPoints++;
            }
        Assert.True(checkedPoints > 500, "points testés à l'intérieur des calques");
    }

    [Test]
    public static void AshThinsWithDistanceAndDriftsDownwind()
    {
        var patch = VolcanicDeposits.NewPatch(Site, true, 0);
        var (ax, _) = LavaFlow.TangentFrame(Site);
        var ash = VolcanicDeposits.Tephra(patch, 3, ax, 1.0, out double placed);
        double volume = VolcanicDeposits.TephraVolume(3);
        Console.WriteLine($"     VEI 3 : {volume:G2} m³ émis, {placed:G2} m³ posés sur le calque");
        Assert.True(placed > 0.8 * volume && placed <= volume * 1.001, "presque tout le volume retombe sur le calque");
        double At(double x, double y) { patch.Publish(ash, new float[ash.Length]); return patch.Sample(patch.DirectionAt(x, y), out _); }
        Assert.True(At(500, 0) > At(3000, 0) && At(3000, 0) > At(8000, 0), "les cendres s'amincissent en s'éloignant");
        Assert.True(At(4000, 0) > 1.5 * At(-4000, 0), "plus épaisses sous le vent");
        Assert.True(At(0, 0) is > 1 and < 30, $"épaisseur près de la bouche : {At(0, 0):0.0} m");
        Assert.True(VolcanicDeposits.TephraVolume(1) == 0, "éruption effusive : pas de cendres");
    }

    [Test]
    public static void LavaFlowsDownTheVolcanoAndAccumulates()
    {
        // Volcan bouclier : cône de 1 500 m à 7 % de pente
        double Cone(Double3 d) { var (x, y) = Local(d); return 1500 - 0.07 * Math.Sqrt(x * x + y * y); }
        var patch = VolcanicDeposits.NewPatch(Site, false, 0);
        var lava = VolcanicDeposits.Lava(patch, Cone, 0, 5, out double placed);
        Console.WriteLine($"     coulées : {placed:G2} m³ posés pour {VolcanicDeposits.LavaVolume(0):G2} m³ émis");
        Assert.True(placed > 0.3 * VolcanicDeposits.LavaVolume(0) && placed < 2 * VolcanicDeposits.LavaVolume(0), "volume des coulées vraisemblable");
        double far = 0, near = 0;
        int n = patch.Size;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var (x, y) = Local(patch.DirectionOf(i, j));
                double r = Math.Sqrt(x * x + y * y);
                if (r > 2000) far += lava[j * n + i]; else near += lava[j * n + i];
                if (i < 4 || j < 4 || i >= n - 4 || j >= n - 4)
                    Assert.True(lava[j * n + i] == 0, "bords du calque intacts");
            }
        Assert.True(far > 0.3 * (far + near), "les coulées descendent loin de la bouche");
        // Deux éruptions : le calque cumule (le volcan grandit)
        patch.Accumulate(lava, VolcanicDeposits.LavaCode, 0.3f);
        double once = patch.NetVolume();
        patch.Accumulate(lava, VolcanicDeposits.LavaCode, 0.3f);
        Assert.Near(patch.NetVolume(), 2 * once, 1e-6 * once, "les éruptions successives s'additionnent");
        Assert.True(patch.Data.Debris.Any(d => d == VolcanicDeposits.LavaCode), "coulées marquées comme lave figée");
    }

    [Test]
    public static void InfiniteSlopeBehavesLikeRealSoils()
    {
        double Deg(double d) => d * Math.PI / 180;
        var dry = new SlopeConditions(0, 0, 0);
        var wet = new SlopeConditions(1, 0, 0);
        double fs35Dry = SlopeStability.FactorOfSafety(Deg(35), dry), fs35Wet = SlopeStability.FactorOfSafety(Deg(35), wet);
        double fs25Wet = SlopeStability.FactorOfSafety(Deg(25), wet);
        Console.WriteLine($"     FS : 35° sec {fs35Dry:0.00}, 35° détrempé {fs35Wet:0.00}, 25° détrempé {fs25Wet:0.00}");
        Assert.True(fs35Dry > 1.2 && fs35Wet < 1, "une pente de 35° tient à sec et cède détrempée");
        Assert.True(fs25Wet > 1, "une pente de 25° tient même détrempée");
        Assert.True(SlopeStability.FactorOfSafety(Deg(30), new SlopeConditions(0.3, 0.25, 0)) < 1, "un fort séisme fait céder une pente de 30°");
        Assert.True(SlopeStability.FactorOfSafety(Deg(5), wet) > 3, "terrain presque plat : stable");
        Assert.True(SlopeStability.SeismicCoefficient(3) == 0 && SlopeStability.SeismicCoefficient(6) < SlopeStability.SeismicCoefficient(8),
            "accélération sismique croissante avec l'intensité");
        Assert.True(SlopeStability.TravelAngleRatio(1e3) > SlopeStability.TravelAngleRatio(1e8), "les grands volumes vont plus loin");
        Assert.True(SlopeStability.MeanDepth(1e4) is > 2 and < 5, "glissement d'un hectare : quelques mètres d'épaisseur");
    }

    [Test]
    public static void WeakestSlopeIsFoundOnlyWhereItCanFail()
    {
        var random = new Random(3);
        var wet = new SlopeConditions(1, 0, 0);
        Assert.True(SlopeStability.FindWeakest(_ => 500, Site, 800, 300, wet, random, 1) == null, "terrain plat : rien ne part");
        double Steep(Double3 d) { var (x, _) = Local(d); return 800 - 0.8 * x; }   // ~39°
        var found = SlopeStability.FindWeakest(Steep, Site, 300, 200, wet, random, 0);
        Assert.True(found is { Kind: SlideKind.Landslide }, "versant détrempé à ~39° : glissement");
        Assert.True(SlopeStability.FindWeakest(Steep, Site, 300, 200, new SlopeConditions(0, 0, 0), random, 0) == null, "le même versant sec tient");
        double Cliff(Double3 d) { var (x, _) = Local(d); return 800 - 2.0 * x; }    // ~63°
        var rock = SlopeStability.FindWeakest(Cliff, Site, 300, 200, new SlopeConditions(0, 0, 1), random, 0.05);
        Assert.True(rock is { Kind: SlideKind.Rockfall }, "paroi gelée puis dégelée : éboulement");
        Assert.True(SlopeStability.FindWeakest(d => Cliff(d) - 2000, Site, 300, 200, wet, random, 1) == null, "sous la mer : rien");
    }

    /// <summary>Versant à ~35° qui s'adoucit en fond de vallée plat vers +x.</summary>
    private static double Hillside(Double3 d)
    {
        var (x, y) = Local(d);
        double s = 0.7 * 60 * Math.Log(1 + Math.Exp(-x / 60));   // ~0,7 (−x) en amont, 0 en aval
        return 300 + s + 0.02 * Math.Abs(y);
    }

    private static Landslide RunToEnd(Landslide slide, double maxSeconds = 400)
    {
        for (double t = 0; t < maxSeconds && !slide.Finished; t += 0.5)
            slide.Step(0.5);
        return slide;
    }

    [Test]
    public static void LandslideConservesVolumeAndRunsDownhill()
    {
        Double3 source = At(-80, 0);
        var slide = new Landslide(Hillside, source, SlideKind.Landslide, 1, 5, 0);
        double released = slide.ReleasedVolume;
        slide.Step(3);
        Assert.True(slide.MovingVolume > 0 && slide.PacketCount > 0, "la masse est en mouvement");
        Assert.Near(slide.NetVolume(), 0, 1e-6 * released + 1e-3, "volume conservé pendant le mouvement");
        RunToEnd(slide);
        Assert.True(slide.Finished, $"le glissement doit s'arrêter (encore {slide.PacketCount} paquets après {slide.ElapsedSeconds:0} s)");
        Assert.Near(slide.NetVolume(), 0, 1e-6 * released + 1e-3, "volume conservé à la fin");
        Assert.Near(slide.DepositedVolume, released, 1e-6 * released, "tout ce qui est parti s'est déposé");

        // Centre de gravité du dépôt en aval de la niche
        double depositX = 0, depositV = 0, scarX = 0, scarV = 0;
        var (delta, debris) = slide.BuildLayer();
        int n = slide.Size;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double d = delta[j * n + i];
                var (x, _) = Local(slide.Patch.DirectionOf(i, j));
                if (d > 0) { depositX += x * d; depositV += d; }
                else if (d < 0) { scarX += x * -d; scarV += -d; }
                if (i < Landslide.Margin || j < Landslide.Margin || i >= n - Landslide.Margin || j >= n - Landslide.Margin)
                    Assert.True(delta[j * n + i] == 0 && debris[j * n + i] == 0, "les bords du calque restent intacts");
            }
        depositX /= depositV;
        scarX /= scarV;
        Console.WriteLine($"     {released:N0} m³ en {slide.ElapsedSeconds:0} s, vitesse max {slide.MaxSpeed:0.0} m/s, niche à x = {scarX:0} m, dépôt à x = {depositX:0} m");
        Assert.True(depositX > scarX + 30, "le dépôt doit être en aval de la niche");
        Assert.True(released is > 2000 and < 200000, $"volume d'un glissement type : {released:N0} m³");
        Assert.True(slide.MaxSpeed is > 2 and < 60, $"vitesse vraisemblable : {slide.MaxSpeed:0.0} m/s");
    }

    [Test]
    public static void RockfallStopsNearTheFootOfTheCliff()
    {
        // Paroi de ~120 m à ~60° au-dessus d'une plaine
        double Cliff(Double3 d)
        {
            var (x, _) = Local(d);
            return 100 + 120 / (1 + Math.Exp(x / 35.0));
        }
        var slide = RunToEnd(new Landslide(Cliff, At(-10, 0), SlideKind.Rockfall, 1, 9, 0));
        Assert.True(slide.Finished, "l'éboulement doit s'arrêter");
        var (delta, _) = slide.BuildLayer();
        int n = slide.Size;
        double farthest = double.MinValue;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                if (delta[j * n + i] > 0.05)
                    farthest = Math.Max(farthest, Local(slide.Patch.DirectionOf(i, j)).x);
        Console.WriteLine($"     éboulement de {slide.ReleasedVolume:N0} m³, blocs jusqu'à x = {farthest:0} m");
        Assert.True(farthest > 30 && farthest < 400, $"les blocs s'arrêtent au pied de la paroi (x = {farthest:0} m)");
    }

    [Test]
    public static void DepositsRestBelowTheAngleOfRepose()
    {
        var slide = RunToEnd(new Landslide(Hillside, At(-80, 0), SlideKind.Landslide, 1.5, 11, 0));
        int n = slide.Size;
        double c = slide.CellM;
        int steep = 0, deposits = 0;
        for (int j = Landslide.Margin + 1; j < n - Landslide.Margin - 1; j++)
            for (int i = Landslide.Margin + 1; i < n - Landslide.Margin - 1; i++)
            {
                if (slide.GroundAt(i, j) - slide.InitialAt(i, j) < 0.3)
                    continue;
                deposits++;
                double gx = (slide.GroundAt(i + 1, j) - slide.GroundAt(i - 1, j)) / (2 * c);
                double gy = (slide.GroundAt(i, j + 1) - slide.GroundAt(i, j - 1)) / (2 * c);
                if (Math.Sqrt(gx * gx + gy * gy) > 1.0)   // plus de 45°
                    steep++;
            }
        Console.WriteLine($"     {deposits} cellules de dépôt, dont {steep} à plus de 45°");
        Assert.True(deposits > 20 && steep < 0.05 * deposits, "le dépôt meuble ne forme pas de pics");
    }

    [Test]
    public static void ParticlesFollowThePatch()
    {
        var slide = new Landslide(Hillside, At(-80, 0), SlideKind.Landslide, 1, 2, 0);
        slide.Step(2);
        var particles = new List<DebrisParticle>();
        slide.GetParticles(particles);
        Assert.True(particles.Count == slide.PacketCount && particles.Count > 10, "un bloc par paquet");
        foreach (var p in particles)
        {
            Assert.True(Math.Abs(p.Direction.Length() - 1) < 1e-9, "direction unitaire");
            Assert.True(Math.Abs(p.Height - Hillside(p.Direction)) < 40, "les blocs sont sur le relief");
        }
    }
}
