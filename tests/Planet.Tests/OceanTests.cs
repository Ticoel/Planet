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

    /// <summary>Même océan, vent uniforme (m/s) donné par une fonction du point.</summary>
    private static OceanCurrents BasinWithWind(Func<Vector3, Vector3> windAt)
    {
        var grid = Fixtures.Grid(5);
        var elevation = new float[grid.CellCount];
        var wind = new Vector3[grid.CellCount];
        for (int c = 0; c < grid.CellCount; c++)
        {
            Vector3 p = grid.Positions[c];
            double lat = Fixtures.LatitudeDeg(p), lon = Longitude(p);
            elevation[c] = Math.Abs(lon) < 60 && Math.Abs(lat) < 65 ? -4f : 0.5f;
            wind[c] = windAt(p);
        }
        var currents = new OceanCurrents(grid);
        currents.SetSurface(elevation);
        currents.Compute(wind);
        currents.Publish();
        return currents;
    }

    private static float UpwellingAt(OceanCurrents currents, double latDeg, double lonDeg) =>
        currents.Upwelling[currents.FineGrid.NearestCell(Pos(latDeg, lonDeg), 0)];

    [Test]
    public static void EquatorwardWindRaisesColdWaterOnEasternShores()
    {
        // Vent du nord le long de la côte est de l'océan (côte ouest du continent voisin) :
        // l'eau part vers le large (à droite du vent) et de l'eau profonde remonte ; contre la
        // côte d'en face, l'eau s'accumule et plonge
        var currents = BasinWithWind(p => -Fixtures.North(p) * 7f * (p.Y > 0 ? 1f : 0f));
        // (cellule la plus côtière : la plus forte remontée, ou plongée, entre 54° et 60°)
        float east = Enumerable.Range(0, 13).Max(k => UpwellingAt(currents, 30, 54 + k * 0.5));
        float west = Enumerable.Range(0, 13).Min(k => UpwellingAt(currents, 30, -54 - k * 0.5));
        float middle = UpwellingAt(currents, 30, 0);
        Console.WriteLine($"     remontée à 30° N : côte est {east * 86400:0.00} m/jour, milieu {middle * 86400:0.00}, côte ouest {west * 86400:0.00}");
        Assert.True(east * 86400 > 0.2f, "remontée d'eau froide le long de la côte est de l'océan");
        Assert.True(west < 0f, "plongée le long de la côte ouest");
        Assert.True(MathF.Abs(middle) < 0.2f * east, "rien au large");
    }

    [Test]
    public static void TradeWindsRaiseColdWaterAtTheEquator()
    {
        // Alizés d'est : l'eau part vers le nord au nord de l'équateur, vers le sud au sud
        var currents = BasinWithWind(p => -Fixtures.East(p) * 7f);
        float equator = UpwellingAt(currents, 0, 0), off = UpwellingAt(currents, 20, 0);
        Console.WriteLine($"     remontée : équateur {equator * 86400:0.00} m/jour, 20° N {off * 86400:0.00} m/jour");
        Assert.True(equator * 86400 > 0.3f, "remontée équatoriale");
        Assert.True(equator > 5f * MathF.Abs(off), "concentrée à l'équateur");
    }

    private static Vector3 Pos(double latDeg, double lonDeg)
    {
        double la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        return new Vector3((float)(Math.Cos(la) * Math.Sin(lo)), (float)Math.Sin(la), (float)(Math.Cos(la) * Math.Cos(lo)));
    }

    [Test]
    public static void CurrentsStayFiniteOnAYoungPlanet()
    {
        // Planète de départ du jeu après 16 Ma : la surrelaxation forte y divergeait (courants
        // de 10^15 m/s, puis banquise et climat bloqués). Le solveur doit s'en apercevoir et
        // repartir avec une relaxation plus prudente
        var grid = Fixtures.Grid(5);
        var tectonics = Planet.Simulation.Tectonics.TectonicPlates.Generate(grid, 14, 1);
        while (tectonics.TimeMyr < 16)
            tectonics.Step(tectonics.MaxTimeStep());
        var climate = new ClimateSimulation(grid, new OrbitalParameters(), useGpu: false, startDay: 79.5);
        climate.SetSurface(tectonics.CellElevation);
        climate.WaitForHydrology();
        var ocean = new OceanCurrents(grid);
        ocean.SetSurface(tectonics.CellElevation);
        ocean.Compute(climate.Winds.Wind);
        ocean.Publish();
        float fastest = ocean.Current.Max(v => v.Length());
        Console.WriteLine($"     courant le plus rapide : {fastest:0.00} m/s");
        Assert.True(fastest > 0.02f && fastest < 3f, $"courants invraisemblables : {fastest} m/s");
        Assert.True(ocean.Upwelling.All(float.IsFinite), "remontées d'eau non finies");

        // Et la banquise ne se bloque pas même si on lui donne des vitesses absurdes
        var ice = new SeaIce(grid);
        ice.Reset(tectonics.CellElevation, climate.SurfaceTemperature);
        var absurd = Enumerable.Repeat(new Godot.Vector3(1e15f, 0, 0), grid.CellCount).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ice.Step(1.0, climate.SurfaceTemperature, absurd, climate.Winds.Wind);
        Assert.True(watch.ElapsedMilliseconds < 2000, "la banquise ne doit pas se bloquer");
    }
}
