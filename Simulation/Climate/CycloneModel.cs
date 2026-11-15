using System;
using System.Collections.Generic;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Simulation.Climate;

/// <summary>Un cyclone tropical en cours.</summary>
public sealed class Cyclone
{
    public int Id { get; init; }
    /// <summary>Position de l'œil (direction unitaire).</summary>
    public Double3 Position { get; set; }
    /// <summary>Vent maximal soutenu (m/s).</summary>
    public double MaxWind { get; set; }
    public double AgeDays { get; set; }
    /// <summary>Rayon des vents maximaux, au bord de l'œil (km).</summary>
    public double EyeWallKm { get; init; } = 35;
    /// <summary>Le plus fort vent atteint (pour le journal).</summary>
    public double PeakWind { get; set; }
}

/// <summary>
/// Cyclones tropicaux (étape 17) : ouragans, typhons.
/// <list type="bullet">
/// <item><b>Naissance</b> : sur une mer à plus de 26,5 °C, entre 5° et 22° de latitude (assez loin
/// de l'équateur pour que la force de Coriolis fasse tourner l'air ; Gray 1968) ; ~85 par an sur
/// la Terre, au prorata de la surface de la planète.</item>
/// <item><b>Trajectoire</b> : poussés vers l'ouest par les alizés, ils dérivent vers le pôle
/// (effet β), puis, passé ~25°, les vents d'ouest les ramènent vers l'est (la « parabole »
/// typique des ouragans) ; ~5 m/s.</item>
/// <item><b>Intensité</b> : ils se renforcent en quelques jours sur l'eau chaude jusqu'à une
/// intensité potentielle qui croît avec la température de la mer (Emanuel 1986 ; ~70 m/s pour
/// une mer à 30 °C), et s'épuisent en un jour ou deux sur terre ou sur une mer froide.</item>
/// <item><b>Vents</b> : vortex de Rankine modifié, nul dans l'œil, maximal au mur de l'œil
/// (~35 km), décroissant en (r_m / r)^0,6 au-delà ; il tourne dans le sens contraire des
/// aiguilles d'une montre au nord, dans l'autre sens au sud, avec ~20° de convergence vers le
/// centre. Pluies : jusqu'à 300 mm/jour près du mur de l'œil, bandes spirales autour.</item>
/// </list>
/// </summary>
public sealed class CycloneModel
{
    public const double GenesisSeaC = 26.5, EarthCyclonesPerYear = 85, DissipationWind = 15;
    private const double TrackSpeedMs = 5.5;
    private readonly List<Cyclone> _cyclones = new();
    private int _nextId = 1;

    public IReadOnlyList<Cyclone> Cyclones => _cyclones;
    /// <summary>Cyclones nés depuis le dernier pas (pour le journal).</summary>
    public List<Cyclone> Born { get; } = new();

    /// <summary>Intensité potentielle (vent maximal, m/s) au-dessus d'une mer à cette température.</summary>
    public static double PotentialWind(double seaC) => Math.Clamp(18 + 13 * (seaC - GenesisSeaC), 0, 80);

    /// <summary>Catégorie de Saffir-Simpson (0 : tempête tropicale ; 1 à 5 : ouragan).</summary>
    public static int Category(double windMs) => windMs switch
    {
        >= 70 => 5,
        >= 58 => 4,
        >= 50 => 3,
        >= 43 => 2,
        >= 33 => 1,
        _ => 0,
    };

    /// <summary>Avance de <paramref name="days"/> jours.</summary>
    /// <param name="seaC">Température de surface de chaque cellule (°C).</param>
    /// <param name="ocean">Vrai pour les cellules de mer.</param>
    public void Step(double days, PlanetGrid grid, float[] seaC, Func<int, bool> ocean, Random random)
    {
        Born.Clear();
        if (days <= 0)
            return;
        // Naissances : sur une cellule de mer chaude tirée au hasard, assez loin de l'équateur
        double expected = EarthCyclonesPerYear * PlanetConstants.AreaRatio * days / 365.25;
        int births = expected > 0.5 ? (int)Math.Round(expected) : random.NextDouble() < expected ? 1 : 0;
        for (int b = 0; b < Math.Min(births, 5); b++)
            for (int attempt = 0; attempt < 40; attempt++)
            {
                int c = random.Next(grid.CellCount);
                float lat = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(grid.Positions[c].Normalized().Y, -1f, 1f)));
                if (!ocean(c) || seaC[c] < GenesisSeaC || Math.Abs(lat) < 5 || Math.Abs(lat) > 22)
                    continue;
                var cyclone = new Cyclone { Id = _nextId++, Position = Double3.From(grid.Positions[c]).Normalized(), MaxWind = 17 };
                _cyclones.Add(cyclone);
                Born.Add(cyclone);
                break;
            }

        int hint = 0;
        double r = PlanetConstants.RadiusKm * 1000;
        for (int k = _cyclones.Count - 1; k >= 0; k--)
        {
            var cy = _cyclones[k];
            // (pas d'au plus 6 h : la trajectoire reste lisse à grande vitesse)
            int steps = Math.Max(1, (int)Math.Ceiling(days / 0.25));
            double dt = days / steps;
            bool alive = true;
            for (int s = 0; s < steps && alive; s++)
            {
                Vector3 p = cy.Position.ToVector3();
                hint = grid.NearestCell(p, hint);
                double lat = Math.Asin(Math.Clamp(cy.Position.Y, -1, 1));
                double hemisphere = Math.Sign(lat == 0 ? 1 : lat);
                Double3 up = cy.Position;
                Double3 east = Double3.UnitY.Cross(up);
                east = east.Length() < 1e-6 ? new Double3(1, 0, 0) : east.Normalized();
                Double3 north = up.Cross(east);
                // Vers l'ouest sous les tropiques, puis vers l'est passé ~25° ; dérive vers le pôle
                double latDeg = Math.Abs(lat) * 180 / Math.PI;
                double u = latDeg < 25 ? -TrackSpeedMs : TrackSpeedMs * Math.Min(2, (latDeg - 22) / 8);
                double v = hemisphere * TrackSpeedMs * 0.5 + (random.NextDouble() - 0.5) * 2;
                double seconds = dt * 86400;
                cy.Position = (up + (east * u + north * v) * (seconds / r)).Normalized();
                // Intensité : vers l'intensité potentielle sur mer chaude, épuisement sinon
                double target = ocean(hint) ? PotentialWind(seaC[hint]) : 0;
                double tau = target > cy.MaxWind ? 2.0 : ocean(hint) ? 2.5 : 0.8;
                cy.MaxWind += (target - cy.MaxWind) * (1 - Math.Exp(-dt / tau));
                cy.PeakWind = Math.Max(cy.PeakWind, cy.MaxWind);
                cy.AgeDays += dt;
                alive = !(cy.MaxWind < DissipationWind && cy.AgeDays > 1) && latDeg < 55 && cy.AgeDays < 30;
            }
            if (!alive)
                _cyclones.RemoveAt(k);
        }
    }

    /// <summary>
    /// Vent (m/s, repère de la planète) et pluie (mm/jour) des cyclones en un point.
    /// </summary>
    public (Vector3 wind, double rainMmPerDay) At(Double3 position)
    {
        position = position.Normalized();
        Double3 wind = Double3.Zero;
        double rain = 0;
        foreach (var cy in _cyclones)
        {
            double km = Math.Acos(Math.Clamp(position.Dot(cy.Position), -1, 1)) * PlanetConstants.RadiusKm;
            if (km > 900)
                continue;
            double rm = cy.EyeWallKm;
            double speed = km < rm ? cy.MaxWind * km / rm : cy.MaxWind * Math.Pow(rm / km, 0.6);
            Double3 outward = position - cy.Position;
            outward -= position * outward.Dot(position);
            if (outward.Length() < 1e-12)
                continue;
            outward = outward.Normalized();
            Double3 around = position.Cross(outward);   // sens inverse des aiguilles d'une montre vu d'en haut, au nord
            double hemisphere = cy.Position.Y >= 0 ? 1 : -1;
            const double inflow = 20 * Math.PI / 180;
            wind += (around * (hemisphere * Math.Cos(inflow)) - outward * Math.Sin(inflow)) * speed;
            double strength = cy.MaxWind / 50;
            rain += strength * (300 * Math.Exp(-Math.Pow((km - rm) / 40, 2)) + 120 * Math.Exp(-km / 250));
        }
        return (wind.ToVector3(), rain);
    }

    public void Clear() => _cyclones.Clear();
}
