using System;
using System.Collections.Generic;
using Planet.Simulation.Terrain;

namespace Planet.Simulation.Climate;

/// <summary>
/// Tsunamis (étape 17) : un grand séisme sous la mer (magnitude 7,5 et plus, à une frontière qui
/// converge) soulève brusquement le fond, et toute la colonne d'eau au-dessus. L'onde part dans
/// toutes les directions.
/// <list type="bullet">
/// <item><b>Vitesse</b> : c = √(g h) (ondes en eau peu profonde devant leur longueur d'onde) ;
/// ~200 m/s au-dessus de 4 km de fond, la vitesse d'un avion de ligne.</item>
/// <item><b>Hauteur au large</b> : ~2 m près de la source pour un séisme de magnitude 9 (bouées
/// au large de Tōhoku, 2011), dix fois moins par degré de magnitude en moins ; elle décroît comme 1/√r en s'étalant.</item>
/// <item><b>À la côte</b> : la vague ralentit sur les hauts-fonds et grandit (loi de Green :
/// h^−1/4, ~×4,5 de 4 000 m à 10 m de fond).</item>
/// <item><b>Train d'ondes</b> : d'abord un retrait de la mer, puis trois à quatre vagues
/// espacées d'une quinzaine de minutes, de plus en plus faibles.</item>
/// </list>
/// Unités : m, s ; le temps est celui du climat (jours).
/// </summary>
public sealed class TsunamiModel
{
    /// <summary>Plus haute submersion prise en compte (m).</summary>
    public const double MaxRunupM = 12;
    public const double MinMagnitude = 7.5;
    private const double DeepDepthM = 4000, Gravity = 9.81, PeriodSeconds = 15 * 60, SourceRadiusKm = 100;
    public static readonly double SpeedMs = Math.Sqrt(Gravity * DeepDepthM);

    public readonly record struct Wave(Double3 Origin, double Magnitude, double StartDay);

    private readonly List<Wave> _waves = new();
    public IReadOnlyList<Wave> Waves => _waves;

    /// <summary>Nouveau tsunami (séisme sous la mer).</summary>
    public void Add(Double3 origin, double magnitude, double day)
    {
        if (magnitude < MinMagnitude)
            return;
        _waves.Add(new Wave(origin.Normalized(), magnitude, day));
    }

    /// <summary>Hauteur de la vague à la côte (m) à une distance donnée (km) de la source.</summary>
    public static double CoastalAmplitude(double magnitude, double distanceKm)
    {
        double source = 2.0 * Math.Pow(10, magnitude - 9);
        double spread = Math.Sqrt(SourceRadiusKm / Math.Max(SourceRadiusKm, distanceKm));
        double shoaling = Math.Pow(DeepDepthM / 10.0, 0.25);
        return Math.Min(MaxRunupM, source * spread * shoaling);
    }

    /// <summary>Délai d'arrivée (s) à une distance (km).</summary>
    public static double ArrivalSeconds(double distanceKm) => distanceKm * 1000 / SpeedMs;

    /// <summary>
    /// Élévation de la mer (m) en un point de la côte à un instant : somme des tsunamis en
    /// cours. Retrait d'abord, puis vagues de plus en plus faibles.
    /// </summary>
    public double SeaRise(Double3 position, double day)
    {
        double rise = 0;
        position = position.Normalized();
        foreach (var wave in _waves)
        {
            double km = Math.Acos(Math.Clamp(position.Dot(wave.Origin), -1, 1)) * PlanetConstants.RadiusKm;
            double t = (day - wave.StartDay) * 86400 - ArrivalSeconds(km);
            if (t < 0 || t > 4.5 * PeriodSeconds)
                continue;
            double phase = t / PeriodSeconds;
            // −sin : la mer se retire d'abord ; enveloppe qui s'éteint en 3 à 4 vagues
            rise += -Math.Sin(2 * Math.PI * phase) * Math.Exp(-0.45 * phase) * CoastalAmplitude(wave.Magnitude, km);
        }
        return Math.Clamp(rise, -MaxRunupM, MaxRunupM);
    }

    /// <summary>Oublie les tsunamis passés partout (ondes sorties de la planète).</summary>
    public void Forget(double day)
    {
        double maxSeconds = Math.PI * PlanetConstants.RadiusKm * 1000 / SpeedMs + 5 * PeriodSeconds;
        _waves.RemoveAll(w => (day - w.StartDay) * 86400 > maxSeconds);
    }

    public void Clear() => _waves.Clear();
}
