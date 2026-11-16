using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>
/// Robustesse : plusieurs petites planètes (graines différentes), vieillies comme dans le jeu,
/// puis tous les calculs du relief interrogés en des milliers de points tirés au hasard : aucune
/// valeur invalide (NaN, infini là où il ne faut pas), aucune exception.
/// </summary>
public static class RobustnessTests
{
    private sealed record World(PlanetGrid Grid, TectonicPlates Tectonics, FastClimateEstimate Climate, TerrainHeightField Field, Groundwater Water);

    private static World Build(int seed)
    {
        var grid = Fixtures.Grid(4);
        var orbit = new OrbitalParameters();
        var tectonics = TectonicPlates.Generate(grid, 5, seed);
        FastClimateEstimate estimate = null;
        double next = 0;
        while (tectonics.TimeMyr < 400)
        {
            if (tectonics.TimeMyr >= next)
            {
                estimate = FastClimateEstimator.Estimate(grid, tectonics.CellElevation, orbit);
                tectonics.AnnualPrecipitation = estimate.AnnualPrecipitation;
                next += 25;
            }
            tectonics.Step(tectonics.MaxTimeStep());
        }
        var runoff = tectonics.AnnualPrecipitation.Select(p => 0.5f * p).ToArray();
        var habitats = new VolcanicHabitats(tectonics, tectonics.AnnualPrecipitation).Benches();
        var water = new Groundwater(grid, tectonics.CellElevation, tectonics.AnnualPrecipitation, estimate.MeanTemperature, null, null, seed);
        var benches = habitats.Concat(water.Benches()).ToArray();
        var field = new TerrainHeightField(grid, (float[])tectonics.CellElevation.Clone(), seed, runoff, tectonics.SnapshotLandforms(), benches);
        return new World(grid, tectonics, estimate, field, water);
    }

    private static Double3 RandomDirection(Random random)
    {
        double z = random.NextDouble() * 2 - 1, a = random.NextDouble() * 2 * Math.PI, r = Math.Sqrt(1 - z * z);
        return new Double3(r * Math.Cos(a), r * Math.Sin(a), z);
    }

    [Test(Slow = true)]
    public static void EveryCalculationStaysValidOnSeveralPlanets()
    {
        PlanetConstants.SetRadius(2000);
        try
        {
            foreach (int seed in new[] { 1, 7, 13 })
            {
                var world = Build(seed);
                var random = new Random(seed * 31 + 5);
                string where = $"graine {seed}";
                Assert.Finite(world.Climate.AnnualPrecipitation, $"{where} : pluie estimée");
                Assert.Finite(world.Climate.MeanTemperature, $"{where} : températures estimées");
                Assert.Finite(world.Water.TableDepthM, $"{where} : nappe");
                Assert.Finite(world.Water.Recharge, $"{where} : recharge");
                Assert.True(world.Water.Springs.All(s => float.IsFinite(s.FlowLs) && s.FlowLs >= 0), $"{where} : débits des sources");

                // Relief détaillé en 20 000 points, à toutes les finesses
                int hint = -1;
                for (int i = 0; i < 20000; i++)
                {
                    Double3 d = RandomDirection(random);
                    double wavelength = Math.Pow(10, random.NextDouble() * 4.5 - 0.5);
                    var s = world.Field.Sample(d, wavelength, ref hint);
                    // (dans les bornes que le quadtree suppose pour un morceau pas encore calculé)
                    Assert.True(double.IsFinite(s.Height) && s.Height > -18000 && s.Height < 13000, $"{where} : hauteur {s.Height} en {d}");
                    Assert.True(!double.IsNaN(s.WaterLevel) && !double.IsPositiveInfinity(s.WaterLevel), $"{where} : niveau de l'eau {s.WaterLevel}");
                    Assert.True(double.IsFinite(s.River) && s.River is >= 0 and <= 1.0001, $"{where} : part de fleuve {s.River}");
                    Assert.True(float.IsFinite(s.Wetland) && float.IsFinite(s.Debris), $"{where} : vasière, croûte");
                }

                // Ni aiguille ni puits : sur 150 cellules de terre, une grille de 50 × 50 points tous
                // les 12 m (la maille des morceaux vus de quelques km) ; aucun point ne s'écarte de
                // la moyenne de ses 4 voisins de plus de 8 mailles (pente moyenne de 400 %).
                // (Des fonds de vallée de 2 m de large rabotaient des bosses de 900 m : fentes et
                // aiguilles de centaines de mètres le long des fleuves)
                const int n = 50;
                const double spacing = 12;
                var grid = new double[n, n];
                var land = Enumerable.Range(0, world.Grid.CellCount).Where(c => world.Tectonics.CellElevation[c] > 0.2f).ToArray();
                foreach (int c in land.Where((_, k) => k % Math.Max(1, land.Length / 150) == 0))
                {
                    Double3 center = Double3.From(world.Grid.Positions[c]).Normalized();
                    var (ax, ay) = LavaFlow.TangentFrame(center);
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                        {
                            Double3 d = (center + (ax * (i - n / 2) + ay * (j - n / 2)) * (spacing / TerrainHeightField.RadiusMeters)).Normalized();
                            grid[i, j] = world.Field.Sample(d, 2.5 * spacing, ref hint).Height;
                        }
                    for (int j = 1; j < n - 1; j++)
                        for (int i = 1; i < n - 1; i++)
                        {
                            double step = grid[i, j] - (grid[i - 1, j] + grid[i + 1, j] + grid[i, j - 1] + grid[i, j + 1]) / 4;
                            Assert.True(Math.Abs(step) < 8 * spacing, $"{where} : aiguille ou puits de {step:0} m près de la cellule {c}");
                        }
                }

                // Morceaux de terrain à tous les niveaux
                for (int i = 0; i < 60; i++)
                {
                    var (face, fs, ft) = CubeSphere.FromDirection(RandomDirection(random));
                    int level = random.Next(0, 19);
                    double size = 1.0 / (1 << level);
                    var key = new ChunkKey(face, level, Math.Min((int)(fs / size), (1 << level) - 1), Math.Min((int)(ft / size), (1 << level) - 1));
                    var chunk = TerrainChunkBuilder.Build(world.Field, key);
                    Assert.Finite(chunk.Positions, $"{where} : sommets du morceau {key}");
                    Assert.Finite(chunk.Normals, $"{where} : normales du morceau {key}");
                    Assert.Finite(chunk.Custom0, $"{where} : morphing du morceau {key}");
                    Assert.True(double.IsFinite(chunk.BoundsRadius) && chunk.BoundsRadius > 0, $"{where} : sphère englobante {key}");
                }

                // Grottes de la planète et relief en volume autour d'elles
                Func<Double3, double> height = d => world.Field.Sample(d, 0.5, ref ThreadHint.Value).Height;
                var caves = new List<Cave>();
                foreach (var spring in world.Water.Springs.Where(s => s.Kind == SpringKind.Karst).Take(3))
                    caves.Add(Cave.Karst(Double3.From(spring.Position).Normalized(), seed, height));
                foreach (var volcano in world.Field.Landforms.Where(l => l.Kind != LandformKind.ImpactCrater).Take(3))
                {
                    Double3 summit = Double3.From(volcano.Position).Normalized();
                    var (ax, _) = LavaFlow.TangentFrame(summit);
                    Double3 site = (summit + ax * (0.45 * volcano.RadiusM / TerrainHeightField.RadiusMeters)).Normalized();
                    if (height(site) > 5)
                        caves.Add(Cave.LavaTube(site, ax, seed, height));
                }
                foreach (var cave in caves)
                {
                    Assert.True(cave.Segments.All(g => double.IsFinite(g.a.X + g.a.Y + g.a.Z + g.ra + g.b.X + g.b.Y + g.b.Z + g.rb)), $"{where} : galeries");
                    var frame = new VolumeFrame(cave.Up, cave.GroundZ(0, 0) + TerrainHeightField.RadiusMeters - TerrainHeightField.RadiusMeters);
                    var mesh = VolumeChunkBuilder.Build(frame, 0, 0, world.Field, new[] { cave });
                    Assert.True(mesh.Vertices.All(v => float.IsFinite(v.X + v.Y + v.Z)), $"{where} : sommets du volume");
                    Assert.True(mesh.Normals.All(v => float.IsFinite(v.X + v.Y + v.Z) && Math.Abs(v.Length() - 1) < 1e-3), $"{where} : normales du volume");
                    Assert.True(mesh.Indices.All(k => k >= 0 && k < mesh.Vertices.Length), $"{where} : indices du volume");
                    // La caméra trouve un espace libre au centre des galeries profondes
                    foreach (var (a, ra, _, _) in cave.Segments.Skip(4).Take(3))
                    {
                        var space = cave.SpaceAt(cave.ToWorld(a));
                        Assert.True(space is { Solid: false }, $"{where} : espace libre au centre d'une galerie ({cave.Kind})");
                        Assert.True(space.Value.CeilingRadius > space.Value.FloorRadius, $"{where} : plafond au-dessus du sol");
                    }
                }
            }
        }
        finally
        {
            PlanetConstants.SetRadius(PlanetConstants.EarthRadiusKm);
        }
    }
}
