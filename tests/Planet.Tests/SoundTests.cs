using System;
using System.Linq;
using Planet.Simulation.Audio;

namespace Planet.Tests;

/// <summary>Sons synthétisés de l'environnement.</summary>
public static class SoundTests
{
    /// <summary>Niveau efficace (RMS) et taux de passages par zéro (son aigu = taux élevé) sur ~4 s.</summary>
    private static (double rms, double zeroCrossings) Measure(SoundscapeInput input, int seed = 1)
    {
        var sound = new Soundscape(22050, seed);
        var left = new float[1024];
        var right = new float[1024];
        double sum = 0;
        long crossings = 0, samples = 0;
        float previous = 0;
        for (int block = 0; block < 90; block++)
        {
            sound.Render(input, left, right);
            if (block < 20)
                continue;   // paramètres lissés : on attend qu'ils soient établis
            foreach (float v in left)
            {
                Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1f, "échantillon fini entre −1 et 1");
                sum += v * v;
                if ((v >= 0) != (previous >= 0)) crossings++;
                previous = v;
                samples++;
            }
        }
        return (Math.Sqrt(sum / samples), crossings / (double)samples);
    }

    [Test]
    public static void SilenceWhenNothingHappens()
    {
        var (rms, _) = Measure(new SoundscapeInput());
        Assert.True(rms < 1e-4, $"silence ({rms})");
    }

    [Test]
    public static void StrongerWindIsLouder()
    {
        double breeze = Measure(new SoundscapeInput { WindSpeed = 4 }).rms;
        double gale = Measure(new SoundscapeInput { WindSpeed = 20 }).rms;
        Console.WriteLine($"     vent : brise {breeze:0.000}, coup de vent {gale:0.000}");
        Assert.True(gale > 4 * breeze, "le coup de vent couvre la brise");
    }

    [Test]
    public static void RainIsBrighterThanWindAndSurfIsDeep()
    {
        var wind = Measure(new SoundscapeInput { WindSpeed = 12 });
        var rain = Measure(new SoundscapeInput { Rain = 0.8f });
        var surf = Measure(new SoundscapeInput { Surf = 1 });
        Console.WriteLine($"     passages par zéro par échantillon : pluie {rain.zeroCrossings:0.000}, vent {wind.zeroCrossings:0.000}, vagues {surf.zeroCrossings:0.000}");
        Assert.True(rain.zeroCrossings > 2 * wind.zeroCrossings, "la pluie est plus aiguë que le vent");
        Assert.True(surf.zeroCrossings < wind.zeroCrossings, "les vagues sont plus graves que le vent");
        Assert.True(rain.rms > 0.01 && surf.rms > 0.01, "audibles");
    }

    [Test]
    public static void GroundSoundsFadeWithAltitudeAndSnowMuffles()
    {
        double beach = Measure(new SoundscapeInput { Surf = 1, Rain = 0.5f }).rms;
        double plane = Measure(new SoundscapeInput { Surf = 1, Rain = 0.5f, AltitudeM = 3000 }).rms;
        Assert.True(plane < 0.01 * beach + 1e-4, "plus de vagues ni de pluie à 3 km d'altitude");
        double space = Measure(new SoundscapeInput { WindSpeed = 30, Volcano = 1, Quake = 1, AltitudeM = 400000 }).rms;
        double low = Measure(new SoundscapeInput { WindSpeed = 30, Volcano = 1 }).rms;
        Assert.True(space < 1e-4 && low > 0.01, "dans l'espace, pas d'air : silence");
        var clear = Measure(new SoundscapeInput { WindSpeed = 15 });
        var snowy = Measure(new SoundscapeInput { WindSpeed = 15, Snow = 1 });
        Console.WriteLine($"     vent : {clear.zeroCrossings:0.000} passages par zéro, sous la neige {snowy.zeroCrossings:0.000}");
        Assert.True(snowy.zeroCrossings < clear.zeroCrossings, "la neige étouffe les aigus");
    }

    [Test]
    public static void ThunderComesWithStorms()
    {
        var sound = new Soundscape(22050, 3);
        var left = new float[2048];
        var right = new float[2048];
        for (int block = 0; block < 22050 * 60 / 2048; block++)
            sound.Render(new SoundscapeInput { Rain = 1, ThunderRate = 0.2f }, left, right);
        Console.WriteLine($"     {sound.ThunderCount} coups de tonnerre en une minute (attendu ~12)");
        Assert.InRange(sound.ThunderCount, 5, 22, "tonnerre");
    }
}
