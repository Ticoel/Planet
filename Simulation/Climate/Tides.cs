using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>Marée du moment : une composante par astre (direction + amplitude, m).</summary>
public readonly struct TideState
{
    /// <summary>Direction de la Lune (repère de la planète).</summary>
    public Vector3 MoonDirection { get; init; }
    /// <summary>Amplitude lunaire K (m) : la marée vaut K·(cos²θ − 1/3), θ = angle avec la Lune.</summary>
    public float MoonAmplitude { get; init; }
    public Vector3 SunDirection { get; init; }
    public float SunAmplitude { get; init; }

    /// <summary>Hauteur de la mer (m) au-dessus de son niveau moyen, au point de direction up.</summary>
    public float HeightAt(Vector3 up)
    {
        float m = up.Dot(MoonDirection), s = up.Dot(SunDirection);
        return MoonAmplitude * (m * m - 1f / 3f) + SunAmplitude * (s * s - 1f / 3f);
    }

    /// <summary>Plus haute mer possible aujourd'hui (m) : les deux astres alignés au-dessus du point.</summary>
    public float MaxHeight => (MoonAmplitude + SunAmplitude) * 2f / 3f;
}

/// <summary>
/// Marées (étape 11b), dans le modèle de la <b>marée d'équilibre</b> (Newton) :
/// <list type="bullet">
/// <item>La Lune attire plus fort le côté de la planète qui lui fait face que son centre, et
/// son centre plus fort que le côté opposé. Il en résulte deux bourrelets d'eau, l'un sous la
/// Lune, l'autre à l'opposé. La planète tourne dessous : deux marées hautes par jour lunaire
/// (24 h 50), donc une toutes les 12 h 25.</item>
/// <item>Hauteur : η = K (cos²θ − 1/3), avec K = 3/2 · (m/M) · (R/d)³ · R
/// (m : masse de l'astre, M et R : masse et rayon de la planète, d : distance de l'astre).
/// Pour la Lune, K ≈ 0,54 m ; pour le Soleil, ≈ 0,25 m (beaucoup plus massif, mais 390 fois
/// plus loin : l'effet de marée décroît comme 1/d³). Le terme −1/3 conserve le volume d'eau.</item>
/// <item>Pleine et nouvelle lune : les deux bourrelets s'ajoutent (vives-eaux) ; aux quartiers,
/// ils se contrarient (mortes-eaux). La Lune au périgée donne des marées ~20 % plus fortes.</item>
/// </list>
/// Sur les vraies côtes, la marée est amplifiée par la résonance des bassins et le
/// ralentissement de l'onde sur les plateaux continentaux (Manche : jusqu'à 14 m ; Méditerranée :
/// quelques dizaines de cm). Sans modèle de propagation des ondes, on applique un facteur moyen.
/// </summary>
public static class Tides
{
    private const double MoonToPlanetMass = 0.0123000371;
    private const double SunToPlanetMass = 332946.0487;
    private const double AstronomicalUnitKm = 149597870.7;
    /// <summary>Amplification côtière moyenne : marnage de vives-eaux ~2,3 m.</summary>
    public const float CoastalAmplification = 3f;
    /// <summary>Majorant de la hauteur de la marée (m) : boîtes englobantes de l'eau.</summary>
    public const float MaxHeightMeters = 2.5f;

    /// <summary>Amplitude K (m) de la marée d'équilibre d'un astre.</summary>
    public static double EquilibriumAmplitude(double massRatio, double distanceKm)
    {
        double radiusKm = PlanetConstants.RadiusKm;
        double ratio = radiusKm / distanceKm;
        return 1.5 * massRatio * ratio * ratio * ratio * radiusKm * 1000.0;
    }

    /// <summary>Marée à l'instant donné (jours), amplification côtière comprise.</summary>
    public static TideState Compute(double timeDays, OrbitalParameters orbit, float amplification = CoastalAmplification)
    {
        SunState sun = orbit.ComputeSun(timeDays);
        MoonState moon = MoonOrbit.Compute(timeDays, orbit);
        return new TideState
        {
            MoonDirection = moon.Direction,
            MoonAmplitude = (float)(EquilibriumAmplitude(MoonToPlanetMass, moon.DistanceKm) * amplification),
            SunDirection = sun.Direction,
            SunAmplitude = (float)(EquilibriumAmplitude(SunToPlanetMass, sun.DistanceAu * AstronomicalUnitKm) * amplification),
        };
    }
}
