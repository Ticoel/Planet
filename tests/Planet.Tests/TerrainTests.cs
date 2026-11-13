using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Relief détaillé à l'échelle réelle et quadtree (étape 9).</summary>
public static class TerrainTests
{
    private static readonly Lazy<TerrainHeightField> Field = new(() =>
    {
        var grid = Fixtures.Grid(5);
        // Continent de 2,5 km au centre, océan à −4 km autour
        return new TerrainHeightField(grid, Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3), 42);
    });

    private static Double3 RandomDirection(Random random) =>
        new Double3(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1).Normalized();

    /// <summary>Direction à (lat, lon) en degrés, avec la convention du jeu (lon = atan2(x, z)).</summary>
    private static Double3 At(double latitudeDeg, double longitudeDeg)
    {
        double lat = latitudeDeg * Math.PI / 180, lon = longitudeDeg * Math.PI / 180;
        return new Double3(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    // --- Bruit ------------------------------------------------------------------------------------

    [Test]
    public static void NoiseDerivativesMatchFiniteDifferences()
    {
        var random = new Random(1);
        const double h = 1e-5;
        for (int i = 0; i < 2000; i++)
        {
            double x = random.NextDouble() * 100 - 50, y = random.NextDouble() * 100 - 50, z = random.NextDouble() * 100 - 50;
            GradientNoise.Sample(x, y, z, 7, out double dx, out double dy, out double dz);
            double fx = (GradientNoise.Sample(x + h, y, z, 7) - GradientNoise.Sample(x - h, y, z, 7)) / (2 * h);
            double fy = (GradientNoise.Sample(x, y + h, z, 7) - GradientNoise.Sample(x, y - h, z, 7)) / (2 * h);
            double fz = (GradientNoise.Sample(x, y, z + h, 7) - GradientNoise.Sample(x, y, z - h, 7)) / (2 * h);
            Assert.Near(dx, fx, 1e-5, $"dérivée x au point {i}");
            Assert.Near(dy, fy, 1e-5, $"dérivée y au point {i}");
            Assert.Near(dz, fz, 1e-5, $"dérivée z au point {i}");
        }
    }

    [Test]
    public static void NoiseIsBoundedCenteredAndSeeded()
    {
        var random = new Random(2);
        double sum = 0, squares = 0, max = 0;
        int differentSeed = 0;
        const int samples = 100000;
        for (int i = 0; i < samples; i++)
        {
            double x = random.NextDouble() * 1000, y = random.NextDouble() * 1000, z = random.NextDouble() * 1000;
            double n = GradientNoise.Sample(x, y, z, 3);
            sum += n; squares += n * n; max = Math.Max(max, Math.Abs(n));
            if (Math.Abs(n - GradientNoise.Sample(x, y, z, 4)) > 1e-3) differentSeed++;
        }
        double mean = sum / samples, deviation = Math.Sqrt(squares / samples - mean * mean);
        Assert.True(max <= 1.2, $"valeur maximale {max:0.000}");
        Assert.Near(mean, 0, 0.01, "moyenne");
        Assert.InRange(deviation, 0.15, 0.45, "écart-type");
        Assert.True(differentSeed > samples * 0.95, "la graine doit changer le bruit");
    }

    [Test]
    public static void NoiseStaysPreciseMillionsOfWavelengthsAway()
    {
        // À 3,2 millions d'unités (la Terre au mètre près), un déplacement de 1/1000 d'unité
        // donne une variation petite et non nulle : pas de marches dues aux arrondis
        double x0 = 3.2e6 + 0.37, y0 = -2.9e6 + 0.11, z0 = 1.7e6 + 0.73;
        double previous = GradientNoise.Sample(x0, y0, z0, 1);
        int changes = 0;
        for (int i = 1; i <= 1000; i++)
        {
            double n = GradientNoise.Sample(x0 + i * 1e-3, y0, z0, 1);
            Assert.True(Math.Abs(n - previous) < 0.01, $"saut de {n - previous:G3} au pas {i}");
            if (n != previous) changes++;
            previous = n;
        }
        Assert.True(changes > 990, $"seulement {changes} variations sur 1000 pas : précision insuffisante");
    }

    // --- Sphère cubique ---------------------------------------------------------------------------

    [Test]
    public static void CubeSphereRoundTrip()
    {
        var random = new Random(3);
        for (int i = 0; i < 20000; i++)
        {
            Double3 direction = RandomDirection(random);
            var (face, s, t) = CubeSphere.FromDirection(direction);
            Assert.True(Math.Abs(s) <= 1 + 1e-12 && Math.Abs(t) <= 1 + 1e-12, "coordonnées hors de la face");
            Assert.True(CubeSphere.Direction(face, s, t).DistanceTo(direction) < 1e-12, $"aller-retour faux pour {direction}");
        }
    }

    [Test]
    public static void CubeSphereCellsHaveSimilarSizes()
    {
        // Projection à angles égaux : écart de taille limité entre le centre et le coin d'une face
        double step = 1.0 / 512;
        double center = CubeSphere.Direction(4, 0, 0).DistanceTo(CubeSphere.Direction(4, step, 0));
        double corner = CubeSphere.Direction(4, 1 - step, 1 - step).DistanceTo(CubeSphere.Direction(4, 1, 1 - step));
        Assert.InRange(corner / center, 0.6, 1.0, "rapport des tailles coin / centre");
    }

    // --- Grandes formes ---------------------------------------------------------------------------

    [Test]
    public static void BaseHeightFollowsSimulation()
    {
        // Au centre des cellules, l'interpolation lisse reste proche de l'altitude simulée :
        // l'écart est petit devant les différences d'altitude entre cellules voisines
        var field = Field.Value;
        var grid = field.Grid;
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        int hint = -1;
        double error = 0, contrast = 0;
        int count = 0;
        for (int cell = 0; cell < grid.CellCount; cell += 7)
        {
            double h = field.BaseHeight(Double3.From(grid.Positions[cell]).Normalized(), ref hint);
            error += Math.Pow(h - elevation[cell] * 1000.0, 2);
            contrast += grid.Neighbors[cell].Average(n => Math.Pow((elevation[n] - elevation[cell]) * 1000.0, 2));
            count++;
        }
        double rmsError = Math.Sqrt(error / count), rmsContrast = Math.Sqrt(contrast / count);
        Console.WriteLine($"     écart rms au centre des cellules {rmsError:0} m (écart rms entre voisines {rmsContrast:0} m)");
        Assert.True(rmsError < 0.2 * rmsContrast, $"écart rms {rmsError:0} m pour un contraste de {rmsContrast:0} m");
    }

    [Test]
    public static void BaseHeightIsSmooth()
    {
        // Dérivée seconde bornée le long de lignes aléatoires : ni marche, ni pli
        var field = Field.Value;
        var random = new Random(8);
        int hint = -1;
        double step = 2000.0 / TerrainHeightField.RadiusMeters;   // 2 km
        double worstCurvature = 0;
        for (int line = 0; line < 200; line++)
        {
            Double3 start = RandomDirection(random), direction = RandomDirection(random).Cross(start).Normalized();
            double h0 = field.BaseHeight(start, ref hint);
            double h1 = field.BaseHeight((start + direction * step).Normalized(), ref hint);
            for (int k = 2; k < 300; k++)
            {
                double h2 = field.BaseHeight((start + direction * (k * step)).Normalized(), ref hint);
                worstCurvature = Math.Max(worstCurvature, Math.Abs(h2 - 2 * h1 + h0));
                h0 = h1; h1 = h2;
            }
        }
        // Un pli de pente 0,01 (1 %) donnerait déjà 20 m sur 2 km
        Assert.True(worstCurvature < 10, $"dérivée seconde maximale {worstCurvature:0.0} m sur 2 km");
    }

    [Test]
    public static void FindTriangleAlwaysSucceeds()
    {
        var field = Field.Value;
        var random = new Random(4);
        int hint = -1;
        for (int i = 0; i < 20000; i++)
        {
            Double3 direction = RandomDirection(random);
            int triangle = field.FindTriangle(direction, ref hint, out double wa, out double wb, out double wc);
            Assert.True(triangle >= 0, "aucun triangle trouvé");
            Assert.True(Math.Min(wa, Math.Min(wb, wc)) > -1e-9, $"point hors du triangle trouvé ({wa:G3}, {wb:G3}, {wc:G3})");
            Assert.Near(wa + wb + wc, 1, 1e-9, "somme des coordonnées barycentriques");
        }
    }

    [Test]
    public static void BaseHeightHasNoStepsAcrossGridEdges()
    {
        // De part et d'autre d'une arête de la grille, à 6 mm l'un de l'autre : même altitude
        var field = Field.Value;
        var grid = field.Grid;
        int hint = -1;
        double worst = 0;
        for (int cell = 0; cell < grid.CellCount; cell += 3)
            foreach (int n in grid.Neighbors[cell])
            {
                Double3 a = Double3.From(grid.Positions[cell]).Normalized(), b = Double3.From(grid.Positions[n]).Normalized();
                Double3 middle = (a + b).Normalized();
                Double3 across = a.Cross(b).Normalized() * 5e-10;   // ~3 mm de chaque côté
                double h1 = field.BaseHeight((middle + across).Normalized(), ref hint);
                double h2 = field.BaseHeight((middle - across).Normalized(), ref hint);
                worst = Math.Max(worst, Math.Abs(h1 - h2));
            }
        Assert.True(worst < 0.05, $"saut d'altitude de {worst:G3} m à travers une arête");
    }

    // --- Détail -----------------------------------------------------------------------------------

    [Test]
    public static void DetailKeepsLargeScaleElevation()
    {
        // En moyenne sur une région, le détail ne doit ni surélever ni creuser le relief simulé
        var field = Field.Value;
        var random = new Random(5);
        int hint = -1;
        // Régions assez grandes pour contenir des dizaines de fois la plus grande longueur d'onde (64 km)
        foreach (var (lat, lon, spread, label) in new[] { (20.0, 30.0, 8.0, "montagnes"), (20.0, 58.0, 3.0, "plaine côtière"), (-30.0, -100.0, 15.0, "océan") })
        {
            double sumDetail = 0, sumAbs = 0;
            const int samples = 8000;
            for (int i = 0; i < samples; i++)
            {
                Double3 direction = At(lat + (random.NextDouble() - 0.5) * spread, lon + (random.NextDouble() - 0.5) * spread);
                double detail = field.Height(direction, 50, ref hint) - field.BaseHeight(direction, ref hint);
                sumDetail += detail;
                sumAbs += Math.Abs(detail);
            }
            double mean = sumDetail / samples, typical = sumAbs / samples;
            Console.WriteLine($"     {label} : détail moyen {mean:0.0} m, amplitude typique {typical:0.0} m");
            Assert.True(Math.Abs(mean) < 0.35 * typical + 5, $"{label} : détail moyen {mean:0.0} m pour une amplitude {typical:0.0} m");
        }
    }

    [Test]
    public static void MountainsAreRougherThanPlains()
    {
        var field = Field.Value;
        var random = new Random(6);
        int hint = -1;
        double Roughness(double lat, double lon)
        {
            double sum = 0;
            for (int i = 0; i < 2000; i++)
            {
                Double3 direction = At(lat + (random.NextDouble() - 0.5), lon + (random.NextDouble() - 0.5));
                sum += Math.Abs(field.Height(direction, 50, ref hint) - field.BaseHeight(direction, ref hint));
            }
            return sum / 2000;
        }
        double mountains = Roughness(20, 30), plains = Roughness(20, 60), ocean = Roughness(-30, -100);
        Assert.True(mountains > 5 * plains, $"montagnes {mountains:0} m, plaines {plains:0} m");
        Assert.True(ocean > plains, $"plancher océanique {ocean:0} m, plaines {plains:0} m");
        Assert.InRange(mountains, 100, 2000, "rugosité des montagnes (m)");
    }

    [Test]
    public static void DroppedWavelengthsChangeLittle()
    {
        // Retirer les longueurs d'onde courtes (morceaux éloignés) ne change le relief que de
        // quelques mètres : pas de « saut » visible quand le niveau de détail change
        var field = Field.Value;
        var random = new Random(7);
        int hint = -1;
        var directions = Enumerable.Range(0, 3000).Select(_ => At(20 + (random.NextDouble() - 0.5) * 4, 30 + (random.NextDouble() - 0.5) * 4)).ToArray();
        var reference = directions.Select(d => field.Height(d, 1, ref hint)).ToArray();
        double previous = 0;
        foreach (double wavelength in new[] { 4.0, 40.0, 400.0, 4000.0 })
        {
            double rms = Math.Sqrt(directions.Select((d, i) => Math.Pow(field.Height(d, wavelength, ref hint) - reference[i], 2)).Average());
            Console.WriteLine($"     sans les longueurs d'onde < {wavelength} m : écart rms {rms:0.00} m");
            Assert.True(rms > previous, "l'écart doit croître avec la longueur d'onde retirée");
            Assert.True(rms < wavelength * 0.15 + 0.5, $"écart rms {rms:0.00} m en retirant < {wavelength} m");
            previous = rms;
        }
    }

    [Test]
    public static void MetreScaleDetailIsSmoothAndPrecise()
    {
        // Le long d'une ligne de 50 m au pas de 25 cm, en montagne, au détail le plus fin
        var field = Field.Value;
        int hint = -1;
        Double3 start = At(20.3, 30.2);
        Double3 east = Double3.UnitY.Cross(start).Normalized();
        var heights = Enumerable.Range(0, 201)
            .Select(i => field.Height((start + east * (i * 0.25 / TerrainHeightField.RadiusMeters)).Normalized(), 1, ref hint))
            .ToArray();
        double maxStep = Enumerable.Range(1, 200).Max(i => Math.Abs(heights[i] - heights[i - 1]));
        int distinct = heights.Distinct().Count();
        Assert.True(maxStep < 0.6, $"marche de {maxStep:0.00} m entre deux points distants de 25 cm");
        Assert.True(distinct > 190, $"seulement {distinct} altitudes différentes sur 201 points : précision insuffisante");
    }

    // --- Morceaux ----------------------------------------------------------------------------------

    private static Double3 AbsolutePosition(TerrainChunk chunk, int vertex) =>
        chunk.Center + new Double3(chunk.Positions[vertex * 3], chunk.Positions[vertex * 3 + 1], chunk.Positions[vertex * 3 + 2]);

    private const int Side = TerrainChunkBuilder.Resolution + 1;

    [Test]
    public static void NeighbourChunksShareTheirEdge()
    {
        var field = Field.Value;
        var (face, s, t) = CubeSphere.FromDirection(At(20.2, 30.1));
        int level = 8, n = 1 << level;
        int x = Math.Clamp((int)((s + 1) / 2 * n), 0, n - 2), y = Math.Clamp((int)((t + 1) / 2 * n), 0, n - 1);
        var left = TerrainChunkBuilder.Build(field, new ChunkKey(face, level, x, y));
        var right = TerrainChunkBuilder.Build(field, new ChunkKey(face, level, x + 1, y));
        for (int j = 0; j <= TerrainChunkBuilder.Resolution; j++)
        {
            int a = j * Side + TerrainChunkBuilder.Resolution, b = j * Side;
            double gap = AbsolutePosition(left, a).DistanceTo(AbsolutePosition(right, b));
            Assert.True(gap < 0.05, $"fente de {gap:0.000} m sur le bord commun (rangée {j})");
            Assert.Near(left.Custom0[a * 4 + 3], right.Custom0[b * 4 + 3], 1e-3, "altitude du bord commun");
        }
    }

    [Test]
    public static void ChunksMeetAcrossCubeFaces()
    {
        var field = Field.Value;
        int level = 4, n = 1 << level;
        // Morceau au bord droit de la face 4 (+Z) et son voisin sur la face 0 (+X)
        var a = TerrainChunkBuilder.Build(field, new ChunkKey(4, level, n - 1, n / 2));
        Double3 outside = CubeSphere.Direction(4, 1.0, a.Key.MinT + a.Key.FaceSize / 2);
        var (face, s, t) = CubeSphere.FromDirection(outside * 1.0 + new Double3(1e-9, 0, 0));
        Assert.Equal(0, face, "face voisine");
        int bx = Math.Clamp((int)((s + 1) / 2 * n), 0, n - 1), by = Math.Clamp((int)((t + 1) / 2 * n), 0, n - 1);
        var b = TerrainChunkBuilder.Build(field, new ChunkKey(face, level, bx, by));
        var bVertices = Enumerable.Range(0, Side * Side).Select(v => AbsolutePosition(b, v)).ToArray();
        for (int j = 0; j <= TerrainChunkBuilder.Resolution; j++)
        {
            Double3 p = AbsolutePosition(a, j * Side + TerrainChunkBuilder.Resolution);
            double nearest = bVertices.Min(q => q.DistanceTo(p));
            Assert.True(nearest < 1.0, $"aucun sommet voisin à moins de 1 m sur l'autre face (écart {nearest:0.00} m)");
        }
    }

    [Test]
    public static void MorphTargetsLieOnTheParentGrid()
    {
        // Au bout du morphing, le morceau doit coïncider exactement avec son parent : mêmes
        // sommets (calculés avec les longueurs d'onde du parent), milieux des arêtes entre eux
        var field = Field.Value;
        var key = new ChunkKey(2, 6, 20, 33);
        var chunk = TerrainChunkBuilder.Build(field, key);
        var parent = TerrainChunkBuilder.Build(field, key.Parent);
        int half = TerrainChunkBuilder.Resolution / 2;
        int offsetI = (key.X & 1) * half, offsetJ = (key.Y & 1) * half;
        Double3 Morphed(int i, int j)
        {
            int v = j * Side + i;
            return AbsolutePosition(chunk, v) + new Double3(chunk.Custom0[v * 4], chunk.Custom0[v * 4 + 1], chunk.Custom0[v * 4 + 2]);
        }
        Double3 Parent(int i, int j) => AbsolutePosition(parent, (offsetJ + j / 2) * Side + offsetI + i / 2);
        for (int j = 0; j <= TerrainChunkBuilder.Resolution; j++)
            for (int i = 0; i <= TerrainChunkBuilder.Resolution; i++)
            {
                int i0 = i % 2 == 1 ? i - 1 : i, i1 = i % 2 == 1 ? i + 1 : i;
                int j0 = j % 2 == 1 ? j - 1 : j, j1 = j % 2 == 1 ? j + 1 : j;
                Double3 expected = (Parent(i0, j0) + Parent(i1, j1)) * 0.5;
                Assert.True(Morphed(i, j).DistanceTo(expected) < 0.05,
                    $"cible de morphing en ({i}, {j}) à {Morphed(i, j).DistanceTo(expected):0.00} m du parent");
            }
    }

    [Test]
    public static void TrianglesAreClockwiseSeenFromOutside()
    {
        // Convention de Godot : faces avant dans le sens horaire
        var field = Field.Value;
        var chunk = TerrainChunkBuilder.Build(field, new ChunkKey(0, 3, 4, 4));
        int[] indices = TerrainChunkBuilder.Indices;
        int gridTriangles = TerrainChunkBuilder.Resolution * TerrainChunkBuilder.Resolution * 2;
        for (int k = 0; k < gridTriangles; k++)
        {
            Double3 a = AbsolutePosition(chunk, indices[k * 3]), b = AbsolutePosition(chunk, indices[k * 3 + 1]), c = AbsolutePosition(chunk, indices[k * 3 + 2]);
            Double3 normal = (b - a).Cross(c - a);
            Assert.True(normal.Dot(a.Normalized()) < 0, $"triangle {k} dans le sens trigonométrique");
        }
    }

    [Test]
    public static void ChunkBoundsContainAllVertices()
    {
        var field = Field.Value;
        var chunk = TerrainChunkBuilder.Build(field, new ChunkKey(4, 5, 17, 20));
        for (int v = 0; v < TerrainChunkBuilder.VertexCount; v++)
            Assert.True(AbsolutePosition(chunk, v).DistanceTo(chunk.BoundsCenter) <= chunk.BoundsRadius * 1.0001, $"sommet {v} hors de la sphère englobante");
        var (center, radius) = TerrainQuadtree.EstimatedBounds(chunk.Key);
        for (int v = 0; v < TerrainChunkBuilder.Resolution * TerrainChunkBuilder.Resolution; v++)
            Assert.True(AbsolutePosition(chunk, v).DistanceTo(center) <= radius, $"sommet {v} hors de la sphère estimée avant calcul");
    }

    [Test]
    public static void ChunkBuildIsFastEnough()
    {
        var field = Field.Value;
        var (face, s, t) = CubeSphere.FromDirection(At(20.3, 30.2));
        int level = 16, n = 1 << level;
        int x = (int)((s + 1) / 2 * n), y = (int)((t + 1) / 2 * n);
        TerrainChunkBuilder.Build(field, new ChunkKey(face, level, x, y));   // échauffement
        var stopwatch = Stopwatch.StartNew();
        for (int k = 0; k < 20; k++)
            TerrainChunkBuilder.Build(field, new ChunkKey(face, level, x + k, y));
        double ms = stopwatch.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"     morceau de niveau {level} (montagne, détail au mètre) : {ms:0.0} ms sur un cœur");
        Assert.True(ms < 25, $"{ms:0.0} ms par morceau");
    }

    // --- Quadtree -----------------------------------------------------------------------------------

    /// <summary>Sélection avec tous les morceaux « calculés », sur un relief presque plat (0 à 50 m).</summary>
    private static List<SelectedChunk> SelectAllReady(Double3 camera, TerrainQuadtree quadtree, List<ChunkKey> requests = null) =>
        quadtree.Select(camera, key => TerrainQuadtree.EstimatedBounds(key, 0, 50), (_, _) => true,
                        (key, _) => requests?.Add(key));

    [Test]
    public static void FarCameraSeesOnlyWholeFaces()
    {
        var selection = SelectAllReady(At(10, 20) * 1e9, new TerrainQuadtree());
        Assert.InRange(selection.Count, 1, 6, "morceaux vus de très loin");
        Assert.True(selection.All(c => c.Key.Level == 0), "seules les faces entières doivent être affichées");
    }

    [Test]
    public static void GroundCameraReachesFinestLevelWithBoundedCount()
    {
        var quadtree = new TerrainQuadtree();
        Double3 camera = At(20.3, 30.2) * (TerrainHeightField.RadiusMeters + 52);   // 2 m au-dessus du relief
        var selection = SelectAllReady(camera, quadtree);
        int deepest = selection.Max(c => c.Key.Level);
        Console.WriteLine($"     au sol : {selection.Count} morceaux, niveau max {deepest}");
        Assert.Equal(quadtree.MaxLevel, deepest, "niveau le plus fin sous la caméra");
        Assert.True(selection.Count < 2500, $"{selection.Count} morceaux sélectionnés (tout autour de la caméra : le champ de vision en garde environ un quart)");

        // Aucun morceau affiché en même temps qu'un de ses ancêtres (recouvrement)
        var keys = new HashSet<ChunkKey>(selection.Select(c => c.Key));
        foreach (var chunk in selection)
            for (var k = chunk.Key; k.Level > 0;)
            {
                k = k.Parent;
                Assert.True(!keys.Contains(k), $"{chunk.Key} et son ancêtre {k} sont affichés ensemble");
            }
    }

    [Test]
    public static void NeighbourLevelsDifferByAtMostOne()
    {
        var quadtree = new TerrainQuadtree();
        Double3 camera = At(-12.7, 101.4) * (TerrainHeightField.RadiusMeters + 300);
        var selection = SelectAllReady(camera, quadtree);
        var keys = new HashSet<ChunkKey>(selection.Select(c => c.Key));
        int checkedPairs = 0;
        foreach (var chunk in selection)
        {
            var key = chunk.Key;
            int n = 1 << key.Level;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int x = key.X + dx, y = key.Y + dy;
                if (x < 0 || y < 0 || x >= n || y >= n)
                    continue;   // voisin sur une autre face : vérifié visuellement (jupes)
                for (var k = new ChunkKey(key.Face, key.Level, x, y); k.Level >= 0; k = k.Parent)
                {
                    if (keys.Contains(k))
                    {
                        Assert.True(key.Level - k.Level <= 1, $"{key} touche {k} : {key.Level - k.Level} niveaux d'écart");
                        checkedPairs++;
                        break;
                    }
                    if (k.Level == 0)
                        break;
                }
            }
        }
        Assert.True(checkedPairs > 100, "trop peu de paires vérifiées");
    }

    [Test]
    public static void MissingChildrenAreRequestedAndParentShown()
    {
        // Seules les faces sont calculées : elles restent affichées et leurs enfants sont demandés
        var quadtree = new TerrainQuadtree();
        var requests = new List<(ChunkKey key, double priority)>();
        Double3 camera = At(0, 0) * (TerrainHeightField.RadiusMeters + 1000);
        var selection = quadtree.Select(camera,
            key => key.Level == 0 ? TerrainQuadtree.EstimatedBounds(key) : null,
            (_, _) => true, (key, priority) => requests.Add((key, priority)));
        Assert.True(selection.Count >= 1 && selection.All(c => c.Key.Level == 0), "les faces doivent rester affichées");
        Assert.True(requests.Count >= 4 && requests.All(r => r.key.Level == 1), "les enfants des faces proches doivent être demandés");
    }

    [Test]
    public static void HorizonHidesTheFarSide()
    {
        var quadtree = new TerrainQuadtree();
        Double3 camera = At(0, 0) * (TerrainHeightField.RadiusMeters + 2000);
        var selection = SelectAllReady(camera, quadtree);
        // La face opposée (−Z, longitude 180°) est sous l'horizon
        Assert.True(selection.All(c => c.Key.Face != 5), "la face opposée de la planète ne doit pas être affichée");
    }

    // --- Exagération et caméra ----------------------------------------------------------------

    [Test]
    public static void FlyingForwardKeepsAltitudeWhenLookingDown()
    {
        // Régression : en vol, avancer en regardant vers le sol faisait plonger vers la planète
        var camera = new ExplorerCamera();
        camera.SetField(Field.Value);
        camera.PlaceAbove(At(-30, -100), 20000);   // au-dessus de l'océan
        camera.Look(new Godot.Vector2(0, 400));   // regard nettement vers le bas
        Assert.True(camera.Pitch < -0.5, "la caméra doit regarder vers le bas");
        double startRadius = camera.Position.Length();
        for (int i = 0; i < 120; i++)
            camera.Update(1.0 / 60, new ExplorerInput(Forward: 1, Strafe: 0, Vertical: 0, Turn: 0, Pitch: 0, Run: false, Jump: false));
        Assert.Near(camera.Position.Length(), startRadius, 1.0, "distance au centre après 2 s de vol vers l'avant (m)");
        Assert.True(camera.Speed > 1000, $"vitesse de vol à 20 km d'altitude : {camera.Speed:0} m/s");
    }

    [Test]
    public static void ArrowKeysTurnAndClimb()
    {
        var camera = new ExplorerCamera();
        camera.SetField(Field.Value);
        camera.PlaceAbove(At(-30, -100), 1000);
        double yaw = camera.Yaw, altitude = camera.AltitudeAboveGround;
        for (int i = 0; i < 60; i++)
            camera.Update(1.0 / 60, new ExplorerInput(0, 0, Vertical: 1, Turn: 1, Pitch: 0, Run: false, Jump: false));
        Assert.True(camera.Yaw > yaw + 1, "la flèche droite doit faire tourner vers l'est");
        Assert.True(camera.AltitudeAboveGround > altitude * 1.8, "Page préc. doit faire monter");
        Assert.True(camera.AltitudeAboveGround < altitude * 4, "la montée ne doit pas être trop brutale");
    }

    [Test]
    public static void ShadowCastersBehindTheCameraAreKept()
    {
        // Champ de vision : le demi-espace devant la caméra (vers le nord). Sans ombres, rien
        // derrière ; avec, les morceaux proches derrière la caméra sont gardés, plus grossiers
        Double3 camera = At(10, 20) * (TerrainHeightField.RadiusMeters + 2000);
        Double3 up = camera.Normalized();
        Double3 north = (Double3.UnitY - up * up.Dot(Double3.UnitY)).Normalized();
        bool Front(Double3 center, double radius) => center.Dot(north) > -radius;
        List<SelectedChunk> Select(double range)
        {
            var quadtree = new TerrainQuadtree { ShadowCasterRange = range };
            return quadtree.Select(camera, key => TerrainQuadtree.EstimatedBounds(key, 0, 50), Front, (_, _) => { });
        }
        bool Behind(SelectedChunk chunk)
        {
            var (center, radius) = TerrainQuadtree.EstimatedBounds(chunk.Key, 0, 50);
            return (center - camera).Dot(north) < -radius;
        }
        var without = Select(0);
        var with = Select(20000);
        int behind = with.Count(Behind);
        Console.WriteLine($"     {without.Count} morceaux sans ombres, {with.Count} avec ({behind} derrière la caméra)");
        Assert.True(without.Count(Behind) == 0, "sans ombres, rien derrière la caméra");
        Assert.True(behind > 0, "avec les ombres, des morceaux derrière la caméra");
        Assert.True(with.Count < without.Count * 1.6, "les morceaux d'ombre doivent rester peu nombreux");
        int finestFront = with.Where(c => !Behind(c)).Max(c => c.Key.Level), finestBehind = with.Where(Behind).Max(c => c.Key.Level);
        Console.WriteLine($"     niveau le plus fin : {finestFront} devant, {finestBehind} derrière");
        Assert.True(finestBehind <= finestFront, "derrière la caméra, le relief n'est pas plus fin que devant");
    }

    [Test]
    public static void WheelMovesAlongTheViewAxis()
    {
        var camera = new ExplorerCamera();
        camera.SetField(Field.Value);
        // Depuis l'espace, regard vers le bas : la molette rapproche du sol sans changer le regard
        camera.PlaceAbove(At(-30, -100), 1.3e7);
        double pitch = camera.Pitch;
        for (int i = 0; i < 60; i++)
            camera.Zoom(+1);
        Assert.True(camera.AltitudeAboveGround < 2.0e4, $"60 crans de molette : {camera.AltitudeAboveGround:0} m");
        Assert.Near(camera.Pitch, pitch, 1e-9, "l'inclinaison du regard ne change pas");

        // Près du sol, regard un peu plongeant : on avance dans l'axe du regard
        camera.PlaceAbove(At(-30, -100), 3000);
        camera.Face(Double3.UnitY, -0.3);
        double yaw = camera.Yaw;
        Double3 forward = camera.Forward();
        Double3 start = camera.Position;
        camera.Zoom(+1);
        Double3 move = camera.Position - start;
        Assert.True(move.Length() > 100, $"un cran doit avancer nettement : {move.Length():0} m");
        Assert.True(move.Normalized().Dot(forward) > 0.999, "le déplacement suit l'axe du regard");
        Assert.Near(camera.Yaw, yaw, 1e-4, "le cap ne change pas");
        Assert.Near(camera.Pitch, -0.3, 1e-9, "l'inclinaison ne change pas");
        camera.Zoom(-1);
        Assert.True((camera.Position - start).Dot(forward) < move.Dot(forward) * 0.5, "un cran en arrière recule dans l'axe");
    }

    [Test]
    public static void DraggingMovesAlongTheSurface()
    {
        var camera = new ExplorerCamera();
        camera.SetField(Field.Value);
        camera.PlaceAbove(At(-30, -100), 50000);
        Double3 start = camera.Position;
        camera.Pan(new Godot.Vector2(0, 300), 1000);
        Assert.Near(camera.Position.Length(), start.Length(), 1e-3, "distance au centre");
        double moved = camera.Position.DistanceTo(start);
        Assert.InRange(moved, 0.3 * 50000 * 0.5, 0.3 * 50000 * 2, "déplacement pour 300 pixels sur 1 000 à 50 km d'altitude (m)");
    }

    [Test]
    public static void CameraBasisStaysStableLookingStraightDown()
    {
        // Régression : regard presque vertical (clic droit) puis avancer → la caméra tournoyait
        var camera = new ExplorerCamera();
        camera.SetField(Field.Value);
        camera.PlaceAbove(At(10, 20), 3000);
        camera.Look(new Godot.Vector2(0, 2000));   // regard au maximum vers le bas
        Godot.Basis previous = camera.Basis();
        for (int i = 0; i < 300; i++)
        {
            camera.Update(1.0 / 60, new ExplorerInput(Forward: 1, Strafe: 0.3f, Vertical: 0, Turn: 0, Pitch: 0, Run: false, Jump: false));
            Godot.Basis basis = camera.Basis();
            Assert.True(basis.X.IsNormalized() && basis.Y.IsNormalized() && basis.Z.IsNormalized(), $"repère non normé à l'image {i}");
            Assert.True(Math.Abs(basis.X.Dot(basis.Y)) < 1e-4 && Math.Abs(basis.X.Dot(basis.Z)) < 1e-4, $"repère non orthogonal à l'image {i}");
            float change = basis.X.AngleTo(previous.X) + basis.Z.AngleTo(previous.Z);
            Assert.True(change < 0.01f, $"saut de l'orientation de {change:0.000} rad à l'image {i}");
            previous = basis;
        }
    }

    [Test]
    public static void FlyingOverThePoleKeepsHeading()
    {
        // Au pôle, le « nord » n'existe pas : la direction de marche est transportée, sans saut
        var camera = new ExplorerCamera();
        camera.SetField(Field.Value);
        camera.PlaceAbove(At(89.5, 0), 20000);
        Godot.Basis previous = camera.Basis();
        for (int i = 0; i < 600; i++)
        {
            camera.Update(1.0 / 60, new ExplorerInput(Forward: 1, 0, 0, 0, 0, false, false));
            Godot.Basis basis = camera.Basis();
            Assert.True(basis.Z.AngleTo(previous.Z) < 0.05f, $"saut d'orientation au passage du pôle (image {i})");
            previous = basis;
        }
        Assert.True(Fixtures.LatitudeDeg(camera.Position.Normalized().ToVector3()) < 89.5, "la caméra doit avoir franchi le pôle");
    }

    // --- Géologie fine (9d) -------------------------------------------------------------------

    private static readonly Lazy<TerrainHeightField> WetField = new(() =>
    {
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        var runoff = elevation.Select(e => e > 0 ? 600f : 0f).ToArray();
        return new TerrainHeightField(grid, elevation, 42, runoff);
    });

    [Test]
    public static void RiversCarveValleysWithAWaterBed()
    {
        var field = WetField.Value;
        var grid = field.Grid;
        var hydrology = field.Hydrology;
        Assert.True(hydrology != null, "réseau hydrographique absent");
        // Un grand fleuve : la cellule de plus fort débit qui n'est pas une embouchure
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        int cell = Enumerable.Range(0, grid.CellCount)
            .Where(c => elevation[c] > 0.3f && hydrology.Downstream[c] >= 0 && elevation[hydrology.Downstream[c]] > 0.2f)
            .OrderByDescending(c => hydrology.Discharge[c]).First();
        Double3 a = Double3.From(grid.Positions[cell]).Normalized();
        Double3 b = Double3.From(grid.Positions[hydrology.Downstream[cell]]).Normalized();
        Double3 middle = (a + b).Normalized();
        Double3 across = a.Cross(b).Normalized();
        // Profil en travers du fleuve, de −15 km à +15 km, au pas de 100 m
        int hint = -1;
        double lowest = double.MaxValue, lowestOffset = 0, maxRiver = 0, sides = 0, maxWater = 0;
        for (int k = -150; k <= 150; k++)
        {
            double offset = k * 100.0;
            var sample = field.Sample((middle + across * (offset / TerrainHeightField.RadiusMeters)).Normalized(), 10, ref hint);
            if (sample.Height < lowest) { lowest = sample.Height; lowestOffset = offset; }
            maxRiver = Math.Max(maxRiver, sample.River);
            maxWater = Math.Max(maxWater, sample.WaterLevel - sample.Height);
            if (Math.Abs(k) > 120) sides += sample.Height / 60;
            // Hors du lit, l'eau reste sous le sol (aucune nappe sur les versants)
            if (sample.River < 0.01)
                Assert.True(sample.WaterLevel <= sample.Height + 1e-9, $"eau au-dessus du sol hors du lit, à {offset:0} m de l'axe");
        }
        // Le lit est rempli de vraie eau (surface de l'eau, comme la mer et les lacs)
        Assert.True(maxWater is > 1.5 and < 3.5, $"profondeur d'eau dans le lit : {maxWater:0.0} m");
        Console.WriteLine($"     fleuve de {hydrology.Discharge[cell]:N0} m³/s : fond à {lowest:0} m ({lowestOffset / 1000:0.0} km de l'axe), versants à {sides:0} m en moyenne");
        Assert.True(Math.Abs(lowestOffset) < 6000, $"le point le plus bas est à {lowestOffset:0} m de l'axe du fleuve (méandres compris)");
        Assert.True(sides - lowest > 50, $"vallée trop peu marquée : {sides - lowest:0} m");
        Assert.True(maxRiver > 0.99, $"le lit du fleuve doit être marqué comme eau (maximum {maxRiver:0.00})");
    }

    [Test]
    public static void SeaAndLakesHaveAWaterLevel()
    {
        var field = WetField.Value;
        int hint = -1;
        var ocean = field.Sample(At(-30, -100), 10, ref hint);
        Assert.Near(ocean.WaterLevel, 0, 1e-9, "niveau de la mer");
        Assert.True(ocean.Height < -1000 && ocean.Surface == 0, "en mer, le fond est sous l'eau et la surface au niveau 0");

        // Cuvette fermée sur un plateau : un lac doit s'y former
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 0f, 0f, 40f, 5);
        int pit = grid.NearestCell(new Godot.Vector3(0, 0, 1), 0);
        var basin = new[] { pit }.Concat(grid.Neighbors[pit]).ToArray();
        foreach (int c in basin) elevation[c] = 0.5f;
        foreach (int c in basin.SelectMany(x => grid.Neighbors[x]).Distinct().Except(basin)) elevation[c] = Math.Max(elevation[c], 2.6f);
        var lakeField = new TerrainHeightField(grid, elevation, 1, elevation.Select(e => e > 0 ? 2000f : 0f).ToArray());
        int h2 = -1;
        var lake = lakeField.Sample(new Double3(0, 0, 1), 10, ref h2);
        Console.WriteLine($"     lac : surface à {lake.WaterLevel:0} m, fond à {lake.Height:0} m");
        Assert.True(lake.WaterLevel > 2000 && lake.WaterLevel > lake.Height + 100, "le fond de la cuvette doit être sous un lac");
    }

    [Test]
    public static void FoldsRunAlongTheRange()
    {
        // Flanc de chaîne : rampe de 0,2 à 6 km sur 3° de longitude (pente ~1 %, axe nord-sud).
        // Les plis suivent les courbes de niveau : le relief varie plus en descendant le flanc
        // (vers l'est, en travers des plis) qu'en le longeant (vers le nord)
        var grid = Fixtures.Grid(6);
        var elevation = new float[grid.CellCount];
        for (int c = 0; c < grid.CellCount; c++)
        {
            var p = grid.Positions[c];
            double lat = Fixtures.LatitudeDeg(p), lon = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
            elevation[c] = Math.Abs(lat) < 40 ? (float)(0.2 + 5.8 * Math.Clamp((lon - 11) / 3, 0, 1)) : -4f;
        }
        var field = new TerrainHeightField(grid, elevation, 42);
        Double3 start = At(0, 12.5), east = Double3.UnitY.Cross(start).Normalized(), north = Double3.UnitY;
        int hint = -1;
        double Roughness(Double3 direction)
        {
            double sum = 0;
            double previous = field.Height(start, 4000, ref hint);
            for (int k = 1; k <= 200; k++)
            {
                double h = field.Height((start + direction * ((k - 100) * 1000.0 / TerrainHeightField.RadiusMeters)).Normalized(), 4000, ref hint);
                if (k > 1) sum += (h - previous) * (h - previous);
                previous = h;
            }
            return Math.Sqrt(sum / 199);
        }
        double acrossFolds = Roughness(east), alongFolds = Roughness(north);
        Console.WriteLine($"     variation par km : en travers des plis {acrossFolds:0} m, le long des plis {alongFolds:0} m");
        Assert.True(acrossFolds > 1.15 * alongFolds, "les plis doivent rendre le relief plus irrégulier en travers qu'en long");
    }

    // --- Falaises (vue Maj + F4) ------------------------------------------------------------------

    [Test]
    public static void CliffFinderFindsASteepCoastQuickly()
    {
        var grid = Fixtures.Grid(5);
        float[] elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        var field = new TerrainHeightField(grid, elevation, 42);
        var watch = Stopwatch.StartNew();
        var found = CliffFinder.Find(field, grid, elevation);
        watch.Stop();
        Assert.True(found.HasValue, "aucune falaise trouvée");
        var cliff = found.Value;
        int hint = 0;
        double atShore = field.Height(cliff.Shore, 8, ref hint);
        Double3 offshore = (cliff.Shore - cliff.Inland * (300 / TerrainHeightField.RadiusMeters)).Normalized();
        double atSea = field.Height(offshore, 8, ref hint);
        Console.WriteLine($"    falaise {cliff.HeightM:0} m à {cliff.DistanceM:0} m du rivage ({cliff.SlopeDegrees:0}°), " +
                          $"rivage {atShore:0.0} m, large {atSea:0} m, {watch.ElapsedMilliseconds} ms");
        Assert.True(Math.Abs(atShore) < 15, $"le point du rivage est à {atShore:0} m");
        Assert.True(atSea < 0, "le large devrait être sous la mer");
        Assert.True(cliff.HeightM >= 20, $"falaise trop basse : {cliff.HeightM:0} m");
        Assert.True(Math.Abs(cliff.Inland.Dot(cliff.Shore)) < 1e-6, "la direction des terres doit être tangente");
        Assert.True(watch.ElapsedMilliseconds < 3000, $"recherche trop lente : {watch.ElapsedMilliseconds} ms");
    }
}
