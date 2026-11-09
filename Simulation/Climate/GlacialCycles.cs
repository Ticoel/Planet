using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>Éléments de l'orbite à un instant (cycles de Milankovitch).</summary>
public readonly record struct OrbitalElements(double Eccentricity, double ObliquityDeg, double PerihelionLongitudeDeg);

/// <summary>
/// Glaciations (étape 13c).
/// <list type="bullet">
/// <item><b>Cycles de Milankovitch</b> : l'orbite change lentement sous l'attraction des autres
/// planètes. Excentricité (0,005 à ~0,06, périodes ~100 000 et 405 000 ans), inclinaison de
/// l'axe (22,1° à 24,5°, 41 000 ans) et précession (le périhélie fait le tour des saisons en
/// ~21 000 ans). Ici, quelques termes périodiques aux périodes et amplitudes principales de
/// Berger (1978) — une approximation, pas la vraie solution de la Terre (Laskar et al. 2004).</item>
/// <item><b>Volume des glaces</b> (modèle d'Imbrie et Imbrie 1980) : l'ensoleillement d'été
/// à 65° N décide si la neige de l'hiver fond. Les calottes grossissent lentement (~40 000 ans)
/// et fondent vite (~10 000 ans), d'où des cycles en dents de scie.</item>
/// <item>Les glaciations n'existent que sur une planète assez froide : sur Terre, seulement
/// depuis ~2,6 millions d'années. Leur ampleur dépend ici de la température moyenne estimée.</item>
/// <item>Au maximum glaciaire : mer ~130 m plus bas (Lambeck et al. 2014), ~6 °C de moins.</item>
/// </list>
/// </summary>
public sealed class GlacialCycles
{
    public const double MaxSeaLevelDropM = 130.0;
    public const double MaxCoolingCelsius = 6.0;
    // Modèle d'Imbrie : temps de réponse moyen et dissymétrie croissance / fonte
    private const double ResponseTimeKyr = 17.0;
    private const double Asymmetry = 0.6;
    private const double StepKyr = 0.5;
    // Ampleur selon la température moyenne de la planète : rien au-dessus de 18 °C, plein à 11 °C
    private const double WarmLimitCelsius = 18.0;
    private const double ColdLimitCelsius = 11.0;

    private double _timeKyr;
    private double _ice;   // volume relatif des glaces (−1 : rien, +1 : maximum)

    /// <summary>Volume des glaces (0 : aucune calotte en plus d'aujourd'hui, 1 : maximum glaciaire).</summary>
    public double IceFraction => Math.Clamp(0.5 + 0.5 * _ice, 0.0, 1.0);
    /// <summary>Ampleur possible des glaciations (0 : planète trop chaude, 1 : pleine).</summary>
    public double Strength { get; private set; }
    /// <summary>Baisse du niveau de la mer (m, négatif).</summary>
    public double SeaLevelM => -MaxSeaLevelDropM * IceFraction * Strength;
    /// <summary>Refroidissement global (°C, négatif).</summary>
    public double CoolingCelsius => -MaxCoolingCelsius * IceFraction * Strength;
    public double TimeMyr => _timeKyr / 1000.0;

    /// <summary>Éléments de l'orbite à l'instant t (Ma), autour des valeurs de la Terre actuelle.</summary>
    public static OrbitalElements ElementsAt(double timeMyr)
    {
        double k = timeMyr * 1000.0;   // milliers d'années
        double Cycle(double periodKyr, double phase) => Math.Cos(2 * Math.PI * k / periodKyr + phase);
        double eccentricity = 0.028 + 0.012 * Cycle(405, 0.3) + 0.009 * Cycle(95, 1.1) + 0.007 * Cycle(124, 2.0);
        double obliquity = 23.3 + 1.0 * Cycle(41, 0.7) + 0.2 * Cycle(54, 1.9);
        double perihelion = (282.9 + 360.0 * k / 21.7) % 360.0;
        return new OrbitalElements(Math.Clamp(eccentricity, 0.002, 0.065), obliquity, perihelion);
    }

    /// <summary>
    /// Ensoleillement moyen du jour du solstice d'été à 65° N (W/m²), le « signal de Milankovitch ».
    /// Distance au Soleil ce jour-là : r = a (1 − e²) / (1 + e cos(λ − ϖ)), λ = 90°.
    /// </summary>
    public static double SummerInsolation65N(OrbitalElements elements)
    {
        double e = elements.Eccentricity;
        double trueAnomaly = Mathf.DegToRad((float)(90.0 - elements.PerihelionLongitudeDeg));
        double distance = (1 - e * e) / (1 + e * Math.Cos(trueAnomaly));
        double flux = 1361.0 / (distance * distance);
        double latitude = Math.PI * 65.0 / 180.0, declination = Math.PI * elements.ObliquityDeg / 180.0;
        double x = -Math.Tan(latitude) * Math.Tan(declination);
        double h0 = Math.Acos(Math.Clamp(x, -1, 1));
        return flux / Math.PI * (h0 * Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Sin(h0));
    }

    /// <summary>Avance jusqu'à l'instant t (Ma) ; meanCelsius : température moyenne estimée de la planète.</summary>
    public void AdvanceTo(double timeMyr, double meanCelsius)
    {
        Strength = Math.Clamp((WarmLimitCelsius - meanCelsius) / (WarmLimitCelsius - ColdLimitCelsius), 0.0, 1.0);
        double target = timeMyr * 1000.0;
        if (target < _timeKyr || target - _timeKyr > 2000.0)
        {
            // Saut en arrière ou très loin : les glaces n'ont gardé la mémoire que des derniers
            // ~100 000 ans, on repart 200 000 ans avant la cible
            _timeKyr = Math.Max(0.0, target - 200.0);
            _ice = Forcing(_timeKyr);
        }
        while (_timeKyr < target)
        {
            double dt = Math.Min(StepKyr, target - _timeKyr);
            double forcing = Forcing(_timeKyr + dt / 2);
            double response = forcing > _ice ? ResponseTimeKyr / (1 - Asymmetry) : ResponseTimeKyr / (1 + Asymmetry);
            _ice += (forcing - _ice) * (1 - Math.Exp(-dt / response));
            _timeKyr += dt;
        }
    }

    /// <summary>Volume des glaces vers lequel tend la planète : plus l'été est faible à 65° N,
    /// plus les glaces gagnent (−1 à +1). Été moyen ~493 W/m² (de ~430 à ~570) ; seuil au-dessus
    /// de la moyenne, car les calottes fondent bien plus vite qu'elles ne grossissent : sinon
    /// elles ne dépassaient jamais le quart de leur maximum.</summary>
    private static double Forcing(double timeKyr)
    {
        double insolation = SummerInsolation65N(ElementsAt(timeKyr / 1000.0));
        return Math.Clamp(-(insolation - 510.0) / 15.0, -1.0, 1.0);
    }

    /// <summary>Applique l'orbite du moment à la planète simulée (le calendrier reste calé sur
    /// l'équinoxe de printemps, le 20 mars).</summary>
    public static void ApplyTo(OrbitalParameters orbit, OrbitalElements elements)
    {
        orbit.Eccentricity = (float)elements.Eccentricity;
        orbit.AxialTiltDeg = (float)elements.ObliquityDeg;
        orbit.PerihelionLongitudeDeg = (float)elements.PerihelionLongitudeDeg;
        // Jour du périhélie : l'équinoxe (λ = 0) tombe le jour 79,5 ; ~1 jour par degré de longitude
        double day = 79.5 + elements.PerihelionLongitudeDeg / 360.0 * orbit.YearLengthDays;
        orbit.PerihelionDay = (float)(day % orbit.YearLengthDays);
    }
}
