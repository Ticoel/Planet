using System;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;

namespace Planet.Tests;

/// <summary>Eau de pluie au sol (flaques) et glissements naturels selon la variété des sols.</summary>
public static class RainTests
{
    [Test]
    public static void HeavyRainOnWetSoilMakesPuddlesThatSoakAway()
    {
        var light = new SurfacePonding();
        light.Step(6, 10, 0.2, 15);   // pluie faible sur un sol sec
        Assert.True(light.Wetness > 0.8 && light.DepthMm == 0, "pluie faible : sol mouillé, pas de flaques (le sol boit tout)");

        var storm = new SurfacePonding();
        storm.Step(3, 60, 0.95, 15);  // grosse averse sur un sol gorgé d'eau
        Console.WriteLine($"     averse de 60 mm/jour sur sol saturé : {storm.DepthMm:0.0} mm d'eau en surface après 3 h");
        Assert.True(storm.DepthMm > 3, "il pleut plus vite que le sol n'absorbe : l'eau stagne");
        Assert.True(storm.DepthMm <= SurfacePonding.MaxDepthMm, "au-delà, l'eau ruisselle");

        double peak = storm.DepthMm;
        storm.Step(1, 0, 0.95, 15);
        Assert.True(storm.DepthMm < peak && storm.DepthMm > 0, "après l'averse, les flaques baissent sans disparaître d'un coup");
        storm.Step(8, 0, 0.95, 15);
        Assert.True(storm.DepthMm == 0 && storm.Wetness < 0.5, "quelques heures plus tard, l'eau s'est infiltrée et le sol sèche");

        var frozen = new SurfacePonding();
        frozen.Step(6, 60, 0.5, -5);
        Assert.True(frozen.DepthMm == 0, "il gèle : la pluie tombe en neige, pas de flaques");
        Assert.True(SurfacePonding.Infiltration(1, 15) < SurfacePonding.Infiltration(0, 15), "un sol gorgé d'eau boit moins");
    }

    [Test]
    public static void VariedSoilsFailUnderModerateRain()
    {
        // Versant à 33° sous une pluie modérée : le sol type tient, mais certains endroits (sols
        // minces, peu cohérents) cèdent : les glissements arrivent au hasard des circonstances
        var site = new Double3(0.3, 0.6, 0.74).Normalized();
        var (ax, _) = LavaFlow.TangentFrame(site);
        double Slope(Double3 d) => 800 - Math.Tan(33 * Math.PI / 180) * d.Dot(ax) * TerrainHeightField.RadiusMeters;
        var rain = new SlopeConditions(0.6, 0, 0);
        int uniform = 0, varied = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            if (SlopeStability.FindWeakest(Slope, site, 300, 60, rain, new Random(seed), 0) != null) uniform++;
            if (SlopeStability.FindWeakest(Slope, site, 300, 60, rain, new Random(seed), 0, heterogeneity: 1) != null) varied++;
        }
        Console.WriteLine($"     versant à 33°, pluie modérée : {uniform}/40 ruptures avec un sol uniforme, {varied}/40 avec des sols variés");
        Assert.True(uniform == 0 && varied > 0, "la variété des sols fait céder quelques endroits");
        Assert.True(SlopeStability.FindWeakest(Slope, site, 300, 60, new SlopeConditions(0, 0, 0), new Random(1), 0, heterogeneity: 1) == null,
            "même variés, les sols secs tiennent à 33°");
    }
}
