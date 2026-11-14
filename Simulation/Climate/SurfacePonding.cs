using System;

namespace Planet.Simulation.Climate;

/// <summary>
/// Eau en surface sous la pluie, là où se trouve la caméra : le sol se mouille, puis, quand il
/// pleut plus vite que le sol ne boit, l'eau stagne en flaques avant de s'infiltrer.
/// <list type="bullet">
/// <item><b>Infiltration</b> (Horton 1933) : le sol absorbe jusqu'à f = f_c + (f₀ − f_c)(1 − s)
/// mm/h, s étant sa part saturée : ~25 mm/h pour un sol sec, ~3 mm/h une fois gorgé d'eau
/// (sol limoneux tassé), presque rien s'il est gelé.</item>
/// <item><b>Intensité de la pluie</b> : le climat donne une moyenne par jour ; il pleut en
/// réalité par averses, quelques heures par jour, d'où une intensité ~4 fois la moyenne
/// horaire pendant qu'il pleut.</item>
/// <item><b>Flaques</b> : l'excès (pluie − infiltration) s'accumule en surface (jusqu'à ~30 mm,
/// au-delà l'eau ruisselle vers les fleuves) ; quand la pluie faiblit, l'eau s'infiltre à la
/// même capacité et s'évapore (~0,2 mm/h) : les flaques restent quelques heures après l'averse.</item>
/// <item><b>Sol mouillé</b> : il fonce et luit dès qu'il pleut, et sèche en quelques heures.</item>
/// </list>
/// Unités : mm, heures. Gel : la pluie tombe en neige, et une flaque gèle (on la garde).
/// </summary>
public sealed class SurfacePonding
{
    public const double DryInfiltrationMmH = 25, SaturatedInfiltrationMmH = 3, FrozenInfiltrationMmH = 0.5;
    public const double ShowerFactor = 4, MaxDepthMm = 30, EvaporationMmH = 0.2;
    private const double DryingHours = 4;

    /// <summary>Eau en surface (mm, moyenne sur le sol : les flaques en couvrent une partie).</summary>
    public double DepthMm { get; private set; }
    /// <summary>Sol mouillé (0 : sec, 1 : trempé).</summary>
    public double Wetness { get; private set; }

    /// <summary>Capacité d'infiltration du sol (mm/h).</summary>
    public static double Infiltration(double soilSaturation, double temperatureC) =>
        temperatureC < -1 ? FrozenInfiltrationMmH
            : SaturatedInfiltrationMmH + (DryInfiltrationMmH - SaturatedInfiltrationMmH) * (1 - Math.Clamp(soilSaturation, 0, 1));

    /// <summary>Intensité de la pluie pendant les averses (mm/h).</summary>
    public static double ShowerIntensity(double rainMmPerDay) => Math.Max(0, rainMmPerDay) / 24 * ShowerFactor;

    /// <summary>Avance de <paramref name="hours"/> heures sous la pluie du moment.</summary>
    public void Step(double hours, double rainMmPerDay, double soilSaturation, double temperatureC)
    {
        if (hours <= 0)
            return;
        bool liquid = temperatureC > 0.5;
        double rain = liquid ? ShowerIntensity(rainMmPerDay) : 0;
        double infiltration = Infiltration(soilSaturation, temperatureC);
        // Pas de 15 min au plus (au-delà de 12 h simulées d'un coup : l'état d'équilibre suffit)
        hours = Math.Min(hours, 12);
        int steps = Math.Max(1, (int)Math.Ceiling(hours / 0.25));
        double dt = hours / steps;
        for (int k = 0; k < steps; k++)
        {
            // (gel : la flaque devient glace et reste là)
            double change = liquid ? rain - infiltration - EvaporationMmH : 0;
            DepthMm = Math.Clamp(DepthMm + change * dt, 0, MaxDepthMm);
            // Mouillé : tout de suite sous la pluie, puis il sèche (plus vite s'il n'y a plus de flaques)
            double target = Math.Max(Math.Min(1, rain / 0.5), Math.Min(1, DepthMm / 2));
            double rate = target > Wetness ? 4 : 1 / DryingHours;
            Wetness += (target - Wetness) * (1 - Math.Exp(-rate * dt));
        }
    }

    public void Reset()
    {
        DepthMm = 0;
        Wetness = 0;
    }
}
