using System;
using System.Linq;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Caméra : vols de transition, commandes au hasard, marche dans une grotte.</summary>
public static class CameraTests
{
    private static TerrainHeightField Field()
    {
        var grid = Fixtures.Grid(4);
        var elevation = Fixtures.DomeContinent(grid, 20f, 30f, 35f, 3);
        return new TerrainHeightField(grid, elevation, 42, elevation.Select(e => e > 0 ? 600f : 0f).ToArray());
    }

    private static Double3 RandomDirection(Random random)
    {
        double z = random.NextDouble() * 2 - 1, a = random.NextDouble() * 2 * Math.PI, r = Math.Sqrt(1 - z * z);
        return new Double3(r * Math.Cos(a), r * Math.Sin(a), z);
    }

    private static ExplorerInput RandomInput(Random random) => new(
        random.Next(-1, 2), random.Next(-1, 2), random.Next(-1, 2) * (random.NextDouble() < 0.3 ? 1 : 0),
        (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), random.NextDouble() < 0.2, random.NextDouble() < 0.05);

    private static void AssertValid(ExplorerCamera camera, TerrainHeightField field, string what)
    {
        Assert.True(double.IsFinite(camera.Position.X + camera.Position.Y + camera.Position.Z), $"{what} : position finie");
        Assert.True(double.IsFinite(camera.Pitch) && Math.Abs(camera.Pitch) <= 1.531, $"{what} : inclinaison {camera.Pitch}");
        if (camera.InCave)
            return;
        int hint = -1;
        double ground = Math.Max(0, field.Sample(camera.Position.Normalized(), 0.5, ref hint).Surface);
        double above = camera.Position.Length() - TerrainHeightField.RadiusMeters - ground;
        Assert.True(above > 0.5, $"{what} : au-dessus du sol ({above:0.00} m)");
    }

    [Test]
    public static void TransitionsAndRandomFlightNeverGoUnderground()
    {
        var field = Field();
        var camera = new ExplorerCamera();
        camera.SetField(field);
        camera.PlaceAbove(new Double3(0, 0, 1), 4e6);
        var random = new Random(3);
        for (int trip = 0; trip < 25; trip++)
        {
            Double3 target = RandomDirection(random);
            double altitude = Math.Pow(10, random.NextDouble() * 6.5);
            bool walk = random.NextDouble() < 0.3;
            camera.Transition(() =>
            {
                camera.PlaceAbove(target, walk ? ExplorerCamera.EyeHeight : altitude);
                if (walk)
                    camera.ToggleWalking();
            });
            int frames = 0;
            while (camera.InTransition && frames < 400)
            {
                camera.Update(1 / 60.0, default);
                AssertValid(camera, field, $"vol {trip}, image {frames}");
                frames++;
            }
            Assert.True(!camera.InTransition, $"vol {trip} : arrivée en moins de 400 images");
            Assert.True(camera.Position.Normalized().DistanceTo(target) * TerrainHeightField.RadiusMeters < 5, $"vol {trip} : arrivé au but");
            Assert.True(camera.Walking == walk, $"vol {trip} : marche ou vol comme demandé");
            for (int f = 0; f < 120; f++)
            {
                camera.Update(1 / 60.0, RandomInput(random));
                AssertValid(camera, field, $"après le vol {trip}, image {f}");
            }
        }
    }

    [Test]
    public static void WalkingInACaveNeverEntersTheRock()
    {
        var field = Field();
        int hint = -1;
        Func<Double3, double> height = d => field.Sample(d, 0.5, ref hint).Height;
        int cell = Enumerable.Range(0, field.Grid.CellCount).First(c => field.Grid.Positions[c].Normalized().Dot(field.Grid.Positions[0]) > -2 && height(Double3.From(field.Grid.Positions[c]).Normalized()) > 600);
        Double3 site = Double3.From(field.Grid.Positions[cell]).Normalized();
        foreach (var cave in new[] { Cave.LavaTube(site, LavaFlow.TangentFrame(site).x, 6, height), Cave.Karst(site, 5, height) })
        {
            var camera = new ExplorerCamera();
            camera.SetField(field);
            camera.CaveProbe = p => cave.SpaceAt(p);
            camera.CaveRescue = p =>
            {
                Double3 q = cave.ToLocal(p);
                var best = cave.Segments.Select(g => { var ab = g.b - g.a; double t = Math.Clamp((q - g.a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-9), 0, 1); return g.a + ab * t; })
                                        .OrderBy(x => x.DistanceTo(q)).First();
                return cave.ToWorld(best);
            };
            // Au-dessus de l'ouverture, à pied : on tombe dans la grotte, puis on erre au hasard
            camera.PlaceAbove(cave.Up, ExplorerCamera.EyeHeight);
            camera.ToggleWalking();
            var random = new Random(11);
            int inside = 0;
            for (int f = 0; f < 3000; f++)
            {
                camera.Update(1 / 60.0, f < 120 ? default : RandomInput(random) with { Vertical = 0, Jump = false });
                if (camera.InCave)
                {
                    inside++;
                    var space = cave.SpaceAt(camera.Position);
                    Assert.True(space is { Solid: false }, $"{cave.Kind}, image {f} : jamais dans la roche");
                }
                Assert.True(double.IsFinite(camera.Position.Length()), $"{cave.Kind}, image {f} : position finie");
            }
            Assert.True(inside > 100, $"{cave.Kind} : la caméra entre dans la grotte ({inside} images sous terre)");
        }
    }
}
