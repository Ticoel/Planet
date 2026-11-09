using System;
using System.Linq;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Orbite et phases de la Lune (étape 10a).</summary>
public static class MoonTests
{
    private static readonly OrbitalParameters Earth = new();

    [Test]
    public static void FullMoonsComeBackEverySynodicMonth()
    {
        // Pleines lunes successives (part éclairée maximale) : espacées de ~29,5 jours
        var fullMoons = new System.Collections.Generic.List<double>();
        double previous = 0, beforePrevious = 0;
        for (double day = 0; day < 400; day += 0.05)
        {
            double fraction = MoonOrbit.Compute(day, Earth).IlluminatedFraction;
            if (day > 0.1 && previous > fraction && previous > beforePrevious && previous > 0.9)
                fullMoons.Add(day - 0.05);
            beforePrevious = previous;
            previous = fraction;
        }
        Assert.True(fullMoons.Count >= 12, $"{fullMoons.Count} pleines lunes en 400 jours");
        double mean = (fullMoons[^1] - fullMoons[0]) / (fullMoons.Count - 1);
        Console.WriteLine($"     lunaison moyenne : {mean:0.00} jours");
        Assert.Near(mean, 29.53, 0.15, "lunaison (jours)");
    }

    [Test]
    public static void PhaseMatchesIlluminatedFraction()
    {
        for (double day = 0; day < 60; day += 0.7)
        {
            var moon = MoonOrbit.Compute(day, Earth);
            // Part éclairée attendue d'après l'âge de la lunaison (orbite presque dans l'écliptique)
            double expected = (1 - Math.Cos(2 * Math.PI * moon.Phase)) / 2;
            Assert.Near(moon.IlluminatedFraction, expected, 0.06, $"part éclairée au jour {day:0.0}");
        }
    }

    [Test]
    public static void DistanceAndLatitudeStayInRange()
    {
        var states = Enumerable.Range(0, 4000).Select(i => MoonOrbit.Compute(i * 0.25, Earth)).ToArray();
        double nearest = states.Min(s => s.DistanceKm), farthest = states.Max(s => s.DistanceKm);
        double maxLatitude = states.Max(s => Math.Abs(s.EclipticLatitude)) * 180 / Math.PI;
        Console.WriteLine($"     distance {nearest:0} à {farthest:0} km, latitude écliptique max {maxLatitude:0.00}°");
        Assert.Near(nearest, 363300, 500, "périgée (km)");
        Assert.Near(farthest, 405500, 500, "apogée (km)");
        Assert.Near(maxLatitude, 5.145, 0.05, "latitude écliptique maximale (°)");
    }

    [Test]
    public static void MoonRisesAboutFiftyMinutesLaterEachDay()
    {
        // Le point « sous la Lune » fait le tour de la planète en ~24 h 50 min : chaque jour, la
        // Lune passe au méridien ~50 min plus tard que la veille
        double LongitudeAt(double day)
        {
            var d = MoonOrbit.Compute(day, Earth).Direction;
            return Math.Atan2(-d.Z, d.X);
        }
        double drift = 0;
        int days = 20;
        for (int k = 0; k < days; k++)
        {
            double delta = LongitudeAt(k + 1) - LongitudeAt(k);
            drift += Math.IEEERemainder(delta, 2 * Math.PI);
        }
        double minutesPerDay = drift / days / (2 * Math.PI) * 1440;
        Console.WriteLine($"     retard quotidien du passage au méridien : {minutesPerDay:0} min");
        Assert.Near(minutesPerDay, 49, 6, "retard quotidien (min)");
    }
}
