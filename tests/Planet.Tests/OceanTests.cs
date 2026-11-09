using System;
using System.Linq;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Courants océaniques (étape 11a) sur un océan d'école.</summary>
public static class OceanTests
{
    /// <summary>
    /// Océan entre 60° O et 60° E (de 65° S à 65° N), terres ailleurs ; vents d'est sous les
    /// tropiques, d'ouest aux latitudes moyennes (comme sur Terre).
    /// </summary>
    private static readonly Lazy<OceanCurrents> Basin = new(() =>
    {
        var grid = Fixtures.Grid(6);
        var elevation = new float[grid.CellCount];
        var wind = new Vector3[grid.CellCount];
        for (int c = 0; c < grid.CellCount; c++)
        {
            Vector3 p = grid.Positions[c];
            double lat = Fixtures.LatitudeDeg(p), lon = Longitude(p);
            elevation[c] = Math.Abs(lon) < 60 && Math.Abs(lat) < 65 ? -4f : 0.5f;
            // −7 m/s à l'équateur, +10 m/s vers 45°, vents d'est polaires
            float u = (float)(-7 * Math.Cos(lat * Math.PI / 180 * 3) + 3 * Math.Cos(lat * Math.PI / 180 * 6) - 1);
            wind[c] = Fixtures.East(p) * u;
        }
        var currents = new OceanCurrents(grid) { IncludeEkman = false };
        currents.SetSurface(elevation);
        currents.Compute(wind);
        currents.Publish();
        return currents;
    });

    private static double Longitude(Vector3 p) => Math.Atan2(p.X, p.Z) * 180 / Math.PI;

    private static Vector3 CurrentAt(double latDeg, double lonDeg)
    {
        var currents = Basin.Value;
        double la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        var direction = new Vector3((float)(Math.Cos(la) * Math.Sin(lo)), (float)Math.Sin(la), (float)(Math.Cos(la) * Math.Cos(lo)));
        int cell = currents.FineGrid.NearestCell(direction, 0);
        return currents.Current[cell];
    }

    [Test]
    public static void SubtropicalGyreTurnsClockwiseInTheNorth()
    {
        // Gyre subtropical nord : vers le nord le long de la côte ouest, vers le sud à l'est
        float west = CurrentAt(28, -55).Dot(Fixtures.North(Pos(28, -55)));
        float middle = CurrentAt(28, 0).Dot(Fixtures.North(Pos(28, 0)));
        Console.WriteLine($"     courant vers le nord à 28° N : côte ouest {west:0.000} m/s, milieu {middle:0.000} m/s, max {Basin.Value.MaxSpeed:0.00} m/s");
        Assert.True(west > 0.02f, "courant de bord ouest vers le nord");
        Assert.True(middle < 0f, "retour vers le sud au milieu de l'océan");
    }

    [Test]
    public static void WesternBoundaryCurrentIsTheStrongest()
    {
        // Intensification à l'ouest : bien plus rapide contre la côte ouest qu'à l'est
        float west = MathF.Abs(CurrentAt(28, -55).Dot(Fixtures.North(Pos(28, -55))));
        float east = MathF.Abs(CurrentAt(28, 55).Dot(Fixtures.North(Pos(28, 55))));
        Console.WriteLine($"     vitesse nord-sud à 28° N : ouest {west:0.000} m/s, est {east:0.000} m/s");
        Assert.True(west > 2.5f * east, "le courant de bord ouest doit dominer");
    }

    [Test]
    public static void SouthernGyreMirrorsTheNorthernOne()
    {
        // Hémisphère sud : rotation inverse, le courant de bord ouest descend vers le pôle
        float west = CurrentAt(-28, -55).Dot(Fixtures.North(Pos(-28, -55)));
        Assert.True(west < -0.02f, "courant de bord ouest vers le sud dans l'hémisphère sud");
    }

    [Test]
    public static void NoCurrentOnLand()
    {
        var currents = Basin.Value;
        var grid = currents.FineGrid;
        for (int c = 0; c < grid.CellCount; c += 7)
        {
            double lon = Longitude(grid.Positions[c]), lat = Fixtures.LatitudeDeg(grid.Positions[c]);
            if (Math.Abs(lon) > 62 || Math.Abs(lat) > 67)
                Assert.True(currents.Current[c].LengthSquared() == 0, "courant sur une terre");
        }
    }

    private static Vector3 Pos(double latDeg, double lonDeg)
    {
        double la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        return new Vector3((float)(Math.Cos(la) * Math.Sin(lo)), (float)Math.Sin(la), (float)(Math.Cos(la) * Math.Cos(lo)));
    }
}
