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

            var watch = Stopwatch.StartNew();
            var mesh = cave.BuildMesh();
            Console.WriteLine($"     {kind} : {cave.LengthM:0} m, {cave.MaxDepthM:0} m sous la surface, {mesh.Indices.Length / 3:N0} triangles en {watch.ElapsedMilliseconds} ms");
            Assert.True(mesh.Indices.Length > 3000, $"{kind} : des parois maillées");
            Assert.True(mesh.Attributes.Any(a => a.A == 0) && mesh.Attributes.Any(a => a.A == 1 && a.G > 5), $"{kind} : parois profondes visibles, sol de surface caché");
            Assert.True(watch.ElapsedMilliseconds < 3000, $"{kind} : maillage assez rapide pour la tâche de fond");
        }
    }
}
