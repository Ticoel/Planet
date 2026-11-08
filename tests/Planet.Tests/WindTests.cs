using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Pression et vents (étape 5) : la circulation générale doit apparaître.</summary>
public static class WindTests
{
    /// <summary>Vents pour un profil de température terrestre symétrique (équinoxe).</summary>
    private static WindModel SymmetricWinds()
    {
        var grid = Fixtures.Grid(4);
        var winds = new WindModel(grid);
        var temperature = grid.Positions.Select(p => 28f - 45f * p.Y * p.Y).ToArray();
        winds.Update(temperature);
        return winds;
    }

    /// <summary>Moyennes zonales (vent d'est en ouest, vent vers le pôle, pression) dans une bande.</summary>
    private static (double eastward, double poleward, double pressure) Band(WindModel winds, float minLat, float maxLat)
    {
        var grid = winds.FineGrid;
        double east = 0, pole = 0, pressure = 0;
        int count = 0;
        for (int c = 0; c < grid.CellCount; c++)
        {
            Vector3 p = grid.Positions[c];
            float lat = Fixtures.LatitudeDeg(p);
            if (lat < minLat || lat > maxLat)
                continue;
            east += winds.Wind[c].Dot(Fixtures.East(p));
            pole += winds.Wind[c].Dot(Fixtures.North(p)) * Math.Sign(lat);
            pressure += winds.Pressure[c];
            count++;
        }
        return (east / count, pole / count, pressure / count);
    }

    [Test]
    public static void TradeWindsBlowFromEastTowardEquator()
    {
        var winds = SymmetricWinds();
        foreach (var (lo, hi) in new[] { (8f, 22f), (-22f, -8f) })
        {
            var band = Band(winds, lo, hi);
            Assert.True(band.eastward < -1, $"alizés {lo}..{hi}° : vent zonal {band.eastward:0.0} m/s (doit venir de l'est)");
            Assert.True(band.poleward < 0, $"alizés {lo}..{hi}° : composante méridienne {band.poleward:0.00} m/s (doit aller vers l'équateur)");
        }
    }

    [Test]
    public static void WesterliesAtMidLatitudes()
    {
        var winds = SymmetricWinds();
        foreach (var (lo, hi) in new[] { (38f, 55f), (-55f, -38f) })
        {
            var band = Band(winds, lo, hi);
            Assert.True(band.eastward > 1, $"vents d'ouest {lo}..{hi}° : vent zonal {band.eastward:0.0} m/s");
        }
    }

    [Test]
    public static void PressureBelts()
    {
        var winds = SymmetricWinds();
        double equator = Band(winds, -5, 5).pressure;
        double subtropics = (Band(winds, 25, 35).pressure + Band(winds, -35, -25).pressure) / 2;
        double subpolar = (Band(winds, 55, 68).pressure + Band(winds, -68, -55).pressure) / 2;
        Assert.True(subtropics > equator + 5, $"anticyclones subtropicaux {subtropics:0} hPa vs équateur {equator:0} hPa");
        Assert.True(subtropics > subpolar + 5, $"anticyclones subtropicaux {subtropics:0} hPa vs dépressions subpolaires {subpolar:0} hPa");
    }

    [Test]
    public static void ConvergenceAtEquatorDivergenceInSubtropics()
    {
        var winds = SymmetricWinds();
        var grid = winds.FineGrid;
        double equator = 0, subtropics = 0;
        int ne = 0, ns = 0;
        for (int c = 0; c < grid.CellCount; c++)
        {
            float lat = MathF.Abs(Fixtures.LatitudeDeg(grid.Positions[c]));
            if (lat < 6) { equator += winds.Convergence[c]; ne++; }
            else if (lat > 22 && lat < 35) { subtropics += winds.Convergence[c]; ns++; }
        }
        Assert.True(equator / ne > 0, "l'air doit converger (et monter) à l'équateur");
        Assert.True(subtropics / ns < 0, "l'air doit diverger (et descendre) sous les anticyclones subtropicaux");
    }

    [Test]
    public static void WindsAreTangentFiniteAndBounded()
    {
        var winds = SymmetricWinds();
        var grid = winds.FineGrid;
        for (int c = 0; c < grid.CellCount; c++)
        {
            Vector3 w = winds.Wind[c];
            Assert.True(float.IsFinite(w.X) && float.IsFinite(w.Y) && float.IsFinite(w.Z), $"vent non fini en {c}");
            Assert.True(MathF.Abs(w.Dot(grid.Positions[c])) < 1e-3f * MathF.Max(1f, w.Length()), $"vent non tangent en {c}");
        }
        Assert.InRange(winds.MaxSpeed, 3, 30, "vent maximal (m/s)");
    }

    [Test]
    public static void ItczFollowsWarmHemisphere()
    {
        // Hémisphère nord plus chaud : la zone de convergence intertropicale remonte vers le nord
        var grid = Fixtures.Grid(4);
        var winds = new WindModel(grid);
        winds.Update(grid.Positions.Select(p => 28f - 45f * (p.Y - 0.25f) * (p.Y - 0.25f)).ToArray());
        Assert.True(winds.ItczLatitudeDeg > 2, $"ZCIT à {winds.ItczLatitudeDeg:0.0}° (doit être dans l'hémisphère nord)");
        Assert.True(winds.ItczLatitudeDeg <= 12.01, $"ZCIT à {winds.ItczLatitudeDeg:0.0}° (plafond 12°)");
    }
}
