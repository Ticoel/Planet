using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Relief en volume : grottes karstiques et tunnels de lave (étape 18).</summary>
public static class CaveTests
{
    [Test]
    public static void CavesAreRealHollowsUnderTheGroundWithAnOpening()
    {
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        var field = new TerrainHeightField(grid, elevation, 42, elevation.Select(e => e > 0 ? 600f : 0f).ToArray());
        int hint = -1;
        Func<Double3, double> height = d => field.Sample(d, 0.5, ref hint).Height;
        int cell = Enumerable.Range(0, grid.CellCount).Where(c => elevation[c] > 0.6f).OrderBy(c => c).First();
        Double3 site = Double3.From(grid.Positions[cell]).Normalized();

        foreach (var cave in new[] { Cave.Karst(site, 5, height), Cave.LavaTube(site, LavaFlow.TangentFrame(site).x, 6, height) })
        {
            string kind = cave.Kind.ToString();
            Assert.True(cave.LengthM > 100, $"{kind} : plus de 100 m de galeries ({cave.LengthM:0} m)");
            Assert.True(cave.MaxDepthM > 6, $"{kind} : sous la surface ({cave.MaxDepthM:0} m)");
            // Chaque galerie loin de l'entrée reste sous terre : de la roche au-dessus
            foreach (var (a, ra, _, _) in cave.Segments.Skip(4))
            {
                if (Math.Sqrt(a.X * a.X + a.Y * a.Y) < cave.HoleRadius + 4)
                    continue;
                Assert.True(cave.GroundZ(a.X, a.Y) - a.Z > ra, $"{kind} : galerie couverte de roche");
                Assert.True(cave.Density(a, cave.GroundZ(a.X, a.Y)) < 0, $"{kind} : vide au centre de la galerie");
            }
            // On peut entrer : au-dessus de l'ouverture, on tombe dans la grotte
            Double3 above = cave.ToWorld(new Double3(0, 0, cave.GroundZ(0, 0) + 1.7));
            var space = cave.SpaceAt(above);
            Assert.True(space.HasValue && !space.Value.Solid, $"{kind} : l'ouverture laisse passer");
            Assert.True(space.Value.FloorRadius < cave.Origin.Length() - 1.5, $"{kind} : le sol de la grotte est sous la surface");
            // Dans la roche, loin des galeries : plein
            Double3 rock = cave.ToWorld(new Double3(cave.HoleRadius + 30, cave.HoleRadius + 30, -60));
            Assert.True(cave.Density(cave.ToLocal(rock), 0) > 0, $"{kind} : roche pleine hors des galeries");
        }
    }

    [Test]
    public static void VolumeReliefMatchesTheSurfaceAndHollowsTheCaves()
    {
        var grid = Fixtures.Grid(5);
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        var field = new TerrainHeightField(grid, elevation, 42, elevation.Select(e => e > 0 ? 600f : 0f).ToArray());
        int hint = -1;
        Func<Double3, double> height = d => field.Sample(d, 0.5, ref hint).Height;
        int cell = Enumerable.Range(0, grid.CellCount).Where(c => elevation[c] > 0.6f).OrderBy(c => c).First();
        Double3 site = Double3.From(grid.Positions[cell]).Normalized();
        var cave = Cave.LavaTube(site, LavaFlow.TangentFrame(site).x, 6, height);
        var frame = new VolumeFrame(site, height(site));

        // Sans grotte : la surface du volume est celle du relief détaillé (raccord invisible)
        var watch = Stopwatch.StartNew();
        var plain = VolumeChunkBuilder.Build(frame, 3, -2, field, null);
        long ms = watch.ElapsedMilliseconds;
        Assert.True(plain.Indices.Length > 300, "un bloc de sol maillé");
        double worst = 0;
        for (int v = 0; v < plain.Vertices.Length; v++)
        {
            Double3 world = plain.Center + Double3.From(plain.Vertices[v]);
            worst = Math.Max(worst, Math.Abs(world.Length() - TerrainHeightField.RadiusMeters - height(world.Normalized())));
        }
        Console.WriteLine($"     bloc de sol : {plain.Indices.Length / 3} triangles en {ms} ms, écart au relief détaillé {worst:0.00} m au plus");
        Assert.True(worst < 0.5, "sommets du volume sur le relief détaillé");
        // Faces tournées vers l'air (sens horaire vu de devant pour Godot : produit vectoriel opposé à la normale)
        for (int t = 0; t < plain.Indices.Length; t += 3)
        {
            Vector3 a = plain.Vertices[plain.Indices[t]], b = plain.Vertices[plain.Indices[t + 1]], c = plain.Vertices[plain.Indices[t + 2]];
            Vector3 n = plain.Normals[plain.Indices[t]] + plain.Normals[plain.Indices[t + 1]] + plain.Normals[plain.Indices[t + 2]];
            Assert.True((b - a).Cross(c - a).Dot(n) <= 1e-6f, "triangle tourné vers l'air");
        }
        // Blocs voisins : sommets de bord partagés (pas de fente)
        var right = VolumeChunkBuilder.Build(frame, 4, -2, field, null);
        var mine = plain.Vertices.Select(v => plain.Center + Double3.From(v)).ToArray();
        int shared = right.Vertices.Select(v => right.Center + Double3.From(v)).Count(w => mine.Any(m => m.DistanceTo(w) < 1e-3));
        Assert.True(shared >= 16, $"bord commun aux deux blocs ({shared} sommets partagés)");

        // Avec la grotte : parois intérieures, plus sombres au fond ; le sol autour reste du sol
        var near = VolumeChunkBuilder.Build(frame, 0, 0, field, new[] { cave });
        var inside = Enumerable.Range(0, near.Colors.Length).Where(v => near.Colors[v].G > 0.25f).ToArray();
        Assert.True(inside.Length > 50, $"parois de grotte dans le bloc de l'entrée ({inside.Length} sommets)");
        Assert.True(inside.Any(v => near.Colors[v].A < 0.7f), "le fond est plus sombre que l'entrée");
        Assert.True(near.Colors.Any(c => c.G == 0f && c.A == 1f), "le sol de surface autour de l'entrée");
        Assert.True(near.Colors.All(c => c.B == 1f), "sommets marqués comme relief en volume");
    }
}
