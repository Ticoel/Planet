using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>État de la Lune à un instant donné, vu depuis le centre de la planète.</summary>
public readonly struct MoonState
{
    /// <summary>Direction de la Lune (repère de la planète, unitaire).</summary>
    public Vector3 Direction { get; init; }
    /// <summary>Distance centre à centre (km).</summary>
    public double DistanceKm { get; init; }
    /// <summary>Âge de la lunaison (0 : nouvelle lune, 0,5 : pleine lune, vers 1 : nouvelle lune suivante).</summary>
    public double Phase { get; init; }
    /// <summary>Part du disque éclairée vue de la planète (0 à 1).</summary>
    public float IlluminatedFraction { get; init; }
    /// <summary>Latitude écliptique (radians) : la Lune s'écarte de ±5,1° du plan de l'orbite de la planète.</summary>
    public double EclipticLatitude { get; init; }
}

/// <summary>
/// Orbite de la Lune (étape 10a), calculée pour n'importe quel instant comme le Soleil. Modèle
/// « képlérien simplifié », suffisant pour l'affichage : mouvement moyen, équation du centre (au
/// premier ordre en excentricité), orbite inclinée de 5,1° dont les nœuds reculent en 18,6 ans.
/// La Lune est placée dans le même repère tournant que le Soleil : sa longitude au-dessus de
/// laquelle elle passe se déduit de celle du Soleil et de l'écart d'ascension droite.
/// </summary>
public static class MoonOrbit
{
    public const double RadiusKm = 1737.4;
    public const double SemiMajorAxisKm = 384400.0;
    public const double Eccentricity = 0.0549;
    public const double SiderealMonthDays = 27.321661;
    public const double AnomalisticMonthDays = 27.554550;
    // Recul des nœuds de l'orbite (période de 18,6 ans, sens rétrograde)
    public const double NodalPeriodDays = 6798.38;
    public static readonly double InclinationRad = 5.145 * Math.PI / 180.0;
    // Longitude moyenne au jour 0, comptée depuis la longitude du Soleil : premier quartier
    // (la Lune est visible le soir au lancement de la simulation)
    private const double InitialElongation = Math.PI / 2;

    public static MoonState Compute(double timeDays, OrbitalParameters orbit)
    {
        SunState sun = orbit.ComputeSun(timeDays);
        SunState sun0 = orbit.ComputeSun(0);

        // Longitude moyenne, anomalie moyenne, nœud ascendant
        double meanLongitude = sun0.EclipticLongitude + InitialElongation + 2 * Math.PI * timeDays / SiderealMonthDays;
        double meanAnomaly = 2 * Math.PI * timeDays / AnomalisticMonthDays;
        double node = -2 * Math.PI * timeDays / NodalPeriodDays;

        // Équation du centre (premier ordre) : la Lune va plus vite près du périgée
        double longitude = meanLongitude + 2 * Eccentricity * Math.Sin(meanAnomaly);
        double latitude = Math.Asin(Math.Sin(InclinationRad) * Math.Sin(longitude - node));
        double distance = SemiMajorAxisKm * (1 - Eccentricity * Math.Cos(meanAnomaly));

        // Coordonnées équatoriales (ascension droite, déclinaison)
        double tilt = Mathf.DegToRad(orbit.AxialTiltDeg);
        var (moonRa, moonDec) = EclipticToEquatorial(longitude, latitude, tilt);
        var (sunRa, _) = EclipticToEquatorial(sun.EclipticLongitude, 0, tilt);

        // Repère tournant de la planète : un astre d'ascension droite plus grande que le Soleil
        // passe au méridien plus tard, son point « au zénith » est donc plus à l'est
        double subLongitude = sun.SubsolarLongitude + (moonRa - sunRa);
        double cosDec = Math.Cos(moonDec);
        var direction = new Vector3(
            (float)(cosDec * Math.Cos(subLongitude)),
            (float)Math.Sin(moonDec),
            (float)(-cosDec * Math.Sin(subLongitude))).Normalized();

        // Phase : angle Soleil–planète–Lune (élongation)
        float cosElongation = Mathf.Clamp(direction.Dot(sun.Direction), -1f, 1f);
        double phase = PositiveModulo((longitude - sun.EclipticLongitude) / (2 * Math.PI), 1.0);
        return new MoonState
        {
            Direction = direction,
            DistanceKm = distance,
            Phase = phase,
            IlluminatedFraction = (1f - cosElongation) / 2f,
            EclipticLatitude = latitude,
        };
    }

    /// <summary>Nom de la phase, pour l'interface.</summary>
    public static string PhaseName(double phase) => PositiveModulo(phase, 1.0) switch
    {
        < 0.03 or >= 0.97 => "nouvelle lune",
        < 0.22 => "premier croissant",
        < 0.28 => "premier quartier",
        < 0.47 => "gibbeuse croissante",
        < 0.53 => "pleine lune",
        < 0.72 => "gibbeuse décroissante",
        < 0.78 => "dernier quartier",
        _ => "dernier croissant",
    };

    private static (double ra, double dec) EclipticToEquatorial(double longitude, double latitude, double tilt)
    {
        double x = Math.Cos(latitude) * Math.Cos(longitude);
        double y = Math.Cos(latitude) * Math.Sin(longitude);
        double z = Math.Sin(latitude);
        double yEq = y * Math.Cos(tilt) - z * Math.Sin(tilt);
        double zEq = y * Math.Sin(tilt) + z * Math.Cos(tilt);
        return (Math.Atan2(yEq, x), Math.Asin(Math.Clamp(zEq, -1, 1)));
    }

    private static double PositiveModulo(double value, double modulus) =>
        ((value % modulus) + modulus) % modulus;
}
