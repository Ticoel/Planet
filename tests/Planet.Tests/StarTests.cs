using System;
using Planet.Simulation.Climate;

namespace Planet.Tests;

/// <summary>Vie de l'étoile du système.</summary>
public static class StarTests
{
    [Test]
    public static void SunLikeStarLivesTenBillionYears()
    {
        var sun = new Star(1.0);
        Console.WriteLine($"     Soleil : vie {sun.LifetimeGyr:0.0} Ga, mort vers {sun.DeathGyr:0.0} Ga, année {sun.YearLengthDays:0} jours");
        Assert.Near(sun.LifetimeGyr, 10, 1e-9, "durée de la séquence principale (Ga)");
        Assert.Near(sun.YearLengthDays, 365.25, 0.01, "année terrestre");
        Assert.Near(sun.RelativeLuminosity(4.57), 1.0, 1e-6, "luminosité actuelle");
        Assert.Near(sun.RelativeLuminosity(0), 0.71, 0.01, "jeune Soleil ~30 % plus faible (Gough 1981)");
        Assert.Equal(StarPhase.MainSequence, sun.PhaseAt(9.9), "séquence principale à 9,9 Ga");
        Assert.Equal(StarPhase.RedGiant, sun.PhaseAt(10.5), "géante rouge à 10,5 Ga");
        Assert.Equal(StarPhase.Dead, sun.PhaseAt(11.5), "morte à 11,5 Ga");
        Assert.True(sun.RelativeLuminosity(11.1) > 500, "géante rouge des centaines de fois plus lumineuse");
        Assert.True(sun.AngularRadius(11.1) > 50 * sun.AngularRadius(4.57), "et énorme dans le ciel");
    }

    [Test]
    public static void MassiveStarsLiveShorter()
    {
        var big = new Star(1.4);
        var small = new Star(0.8);
        Console.WriteLine($"     0,8 masse solaire : {small.LifetimeGyr:0.0} Ga, année {small.YearLengthDays:0} j ; 1,4 : {big.LifetimeGyr:0.0} Ga, année {big.YearLengthDays:0} j");
        Assert.Near(big.LifetimeGyr, 10 * Math.Pow(1.4, -2.5), 1e-9, "étoile de 1,4 masse solaire");
        Assert.True(small.LifetimeGyr > 17, "petite étoile : plus de 17 milliards d'années");
        Assert.True(big.YearLengthDays > 365 && small.YearLengthDays < 365, "année plus longue autour d'une étoile massive (planète plus loin)");
    }

    [Test]
    public static void RockThermostatKeepsTheYoungPlanetTemperateButNotTheOldOne()
    {
        var sun = new Star(1.0);
        Assert.Near(sun.EffectiveIrradiance(0.5), 1.0, 1e-6, "jeune étoile faible compensée par le gaz carbonique");
        double old = sun.EffectiveIrradiance(8.0);
        Console.WriteLine($"     lumière reçue par le climat : 8 Ga ×{old:0.000}, 10 Ga ×{sun.EffectiveIrradiance(9.99):0.000}");
        Assert.True(old > 1.05, "étoile vieillissante : réchauffement sans retour");
    }
}
