using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>Éruption en cours.</summary>
public sealed class Eruption
{
    public Vector3 Position { get; init; }
    /// <summary>Indice d'explosivité volcanique (0 : coulées tranquilles, 6 : type Pinatubo).</summary>
    public int Vei { get; init; }
    public LandformKind Kind { get; init; }
    public double StartDay { get; init; }
    public double DurationDays { get; init; }
    /// <summary>Hauteur du panache (m) : ~1 km pour VEI 0-1, ~25 km pour VEI 5-6.</summary>
    public float PlumeHeightM { get; init; }
    /// <summary>
    /// Éruption fissurale : la lave sort tout le long d'une fissure (un « rideau de feu ») au lieu
    /// d'une bouche ; Position en est le milieu, Strike la direction (unitaire, tangente) et
    /// LengthM la longueur.
    /// </summary>
    public bool Fissure { get; init; }
    public Vector3 Strike { get; init; }
    public float LengthM { get; init; }

    /// <summary>Extrémités de la fissure (directions unitaires) ; la bouche deux fois pour une éruption ordinaire.</summary>
    public (Vector3 a, Vector3 b) Ends()
    {
        if (!Fissure)
            return (Position, Position);
        Vector3 half = Strike * (LengthM / 2f / (PlanetConstants.RadiusKm * 1000f));
        return ((Position - half).Normalized(), (Position + half).Normalized());
    }

    /// <summary>Intensité (0 à 1) à un instant : montée rapide, puis déclin.</summary>
    public float IntensityAt(double day)
    {
        double t = (day - StartDay) / DurationDays;
        if (t < 0 || t > 1)
            return 0f;
        return (float)(Math.Min(1.0, t * 20.0) * Math.Pow(1 - t, 0.7));
    }
}

/// <summary>Séisme.</summary>
public readonly record struct Earthquake(Vector3 Position, float Magnitude, double Day, BoundaryType Boundary);

/// <summary>
/// Volcans et séismes à l'échelle humaine (étape 13a), dans l'horloge climatique, sur le relief figé :
/// <list type="bullet">
/// <item><b>Éruptions</b> : chaque volcan actif entre en éruption au hasard (loi de Poisson).
/// Volcans de point chaud : coulées fréquentes et tranquilles (VEI 0-1, type Kīlauea) ; volcans
/// d'arc : plus rares et plus explosives, la fréquence étant divisée par ~10 à chaque degré de
/// VEI (Newhall et Self 1982 ; Siebert et al. 2010). Les plus fortes envoient des aérosols dans
/// la stratosphère qui voilent le Soleil pendant 1 à 2 ans : ~3 W/m² et ~0,5 °C de moins
/// après le Pinatubo (VEI 6, 1991 ; Robock 2000).</item>
/// <item><b>Séismes</b> : aux frontières de plaques, d'autant plus fréquents que le mouvement
/// relatif est rapide (convergentes &gt; transformantes &gt; divergentes). Magnitudes selon la loi
/// de Gutenberg-Richter, N(≥M) = 10^(a − bM) avec b = 1 : ~1 500 séismes de magnitude 5 ou plus
/// par an sur Terre, ~15 de magnitude 7 ou plus.</item>
/// </list>
/// </summary>
public sealed class PlanetActivity
{
    public const float EarthquakesAbove5PerYear = 1500f;
    public const float GutenbergRichterB = 1f;
    public const float MinMagnitude = 5f;
    public const float MaxMagnitude = 9.5f;
    // Éruptions : intervalle moyen entre deux éruptions d'un volcan actif (années)
    // (sur Terre, une vingtaine de volcans sont en éruption à tout moment, et un volcan de point
    // chaud comme le Kīlauea est resté en éruption presque sans arrêt de 1983 à 2018 ; avec des
    // éruptions plus rares, on n'en voyait presque jamais)
    private const double ShieldIntervalYears = 1.0;
    /// <summary>Part des éruptions de volcans boucliers qui s'ouvrent en fissure sur une zone de
    /// rift du flanc (Kīlauea : la plupart de ses éruptions récentes, Puʻu ʻŌʻō, 2018).</summary>
    private const double FlankFissureShare = 0.35;
    /// <summary>Intervalle moyen (ans) entre deux éruptions fissurales d'une cellule de rift émergé
    /// (l'Islande : une grande éruption fissurale tous les quelques siècles par système volcanique).</summary>
    private const double RiftFissureIntervalYears = 400.0;
    private const double StratoIntervalYears = 8.0;
    // Forçage des aérosols volcaniques (W/m²) pour un VEI 6, divisé par ~5 par degré en dessous
    public const float Vei6ForcingWm2 = 3f;
    public const float AerosolLifetimeDays = 365f;

    private readonly Random _random;
    private readonly List<Eruption> _eruptions = new();
    private Landform[] _volcanoes = Array.Empty<Landform>();
    private int[] _faultCells = Array.Empty<int>();
    private float[] _faultWeights = Array.Empty<float>();
    private float _totalFaultWeight;
    private PlanetGrid _grid;
    private TectonicPlates _tectonics;
    // Rifts émergés : cellules de terre sur une frontière divergente, et direction de la fissure
    // (perpendiculaire à l'écartement des plaques)
    private int[] _riftCells = Array.Empty<int>();
    private Vector3[] _riftStrikes = Array.Empty<Vector3>();

    public PlanetActivity(int seed) => _random = new Random(seed * 31 + 7);

    /// <summary>Éruptions en cours.</summary>
    public IReadOnlyList<Eruption> Eruptions => _eruptions;
    /// <summary>Séismes du dernier pas.</summary>
    public List<Earthquake> RecentEarthquakes { get; } = new();
    /// <summary>Voile d'aérosols volcaniques : forçage radiatif (W/m², positif = moins de Soleil).</summary>
    public float AerosolForcing { get; private set; }

    /// <summary>Fréquence des éruptions par rapport à la Terre (1 = comme sur Terre, 0 = aucune).</summary>
    public double ActivityFactor { get; set; } = 1.0;
    public int ActiveVolcanoCount => _volcanoes.Length;

    /// <summary>Relief figé de l'horloge climatique : volcans actifs, frontières de plaques.</summary>
    public void SetPlanet(TectonicPlates tectonics)
    {
        _tectonics = tectonics;
        _grid = tectonics.Grid;
        var volcanoes = new List<Landform>();
        foreach (var landform in tectonics.Landforms)
            if (landform.Active && landform.Kind != LandformKind.ImpactCrater)
                volcanoes.Add(landform.Snapshot());
        _volcanoes = volcanoes.ToArray();

        var cells = new List<int>();
        var weights = new List<float>();
        _totalFaultWeight = 0f;
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            float factor = tectonics.CellBoundary[cell] switch
            {
                BoundaryType.Convergent => 1f,
                BoundaryType.Transform => 0.6f,
                BoundaryType.Divergent => 0.15f,
                _ => 0f,
            };
            if (factor <= 0f)
                continue;
            float weight = factor * MathF.Max(0.5f, tectonics.CellBoundaryRate[cell]);
            cells.Add(cell);
            weights.Add(weight);
            _totalFaultWeight += weight;
        }
        _faultCells = cells.ToArray();
        _faultWeights = weights.ToArray();

        var rifts = new List<int>();
        var strikes = new List<Vector3>();
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            if (tectonics.CellBoundary[cell] != BoundaryType.Divergent || tectonics.CellElevation[cell] <= 0f)
                continue;
            // Écartement : vitesse relative avec une voisine de l'autre plaque
            Vector3 up = _grid.Positions[cell].Normalized();
            Vector3 opening = Vector3.Zero;
            foreach (int n in _grid.Neighbors[cell])
                if (tectonics.CellPlate[n] != tectonics.CellPlate[cell])
                    opening += tectonics.VelocityAt(cell) - tectonics.VelocityAt(n);
            opening -= up * opening.Dot(up);
            Vector3 strike = opening.LengthSquared() > 1e-12f ? up.Cross(opening).Normalized() : up.Cross(Vector3.Up).Normalized();
            if (strike.LengthSquared() < 0.5f)
                strike = up.Cross(Vector3.Right).Normalized();
            rifts.Add(cell);
            strikes.Add(strike);
        }
        _riftCells = rifts.ToArray();
        _riftStrikes = strikes.ToArray();
        _eruptions.Clear();
        AerosolForcing = 0f;
    }

    /// <summary>
    /// Fissure sur une zone de rift du flanc d'un volcan bouclier : à quelques km du sommet, le
    /// long d'un des deux ou trois rifts du volcan (directions propres à chaque volcan), longue
    /// de quelques centaines de mètres à quelques km.
    /// </summary>
    private Eruption FlankFissure(Landform volcano, double start)
    {
        Vector3 up = volcano.Position.Normalized();
        Vector3 east = up.Cross(Vector3.Up);
        east = east.LengthSquared() < 1e-6f ? Vector3.Right : east.Normalized();
        Vector3 north = up.Cross(east);
        // Rifts du volcan : 2 ou 3 directions fixées par sa position
        float seed = MathF.Abs(MathF.Sin(up.X * 127.1f + up.Y * 311.7f + up.Z * 74.7f)) * 6.2832f;
        float azimuth = seed + _random.Next(3) * 2.094f + (float)(_random.NextDouble() - 0.5) * 0.3f;
        Vector3 rift = east * MathF.Cos(azimuth) + north * MathF.Sin(azimuth);
        float radiusM = PlanetConstants.RadiusKm * 1000f;
        float distanceM = MathF.Max(1500f, volcano.RadiusM * (0.15f + 0.45f * _random.NextSingle()));
        Vector3 position = (up + rift * (distanceM / radiusM)).Normalized();
        rift = (rift - position * rift.Dot(position)).Normalized();   // (tangente au point de la fissure)
        return new Eruption
        {
            Position = position, Vei = 0, Kind = LandformKind.ShieldVolcano, StartDay = start,
            DurationDays = 10 + 150 * _random.NextDouble(), PlumeHeightM = PlumeHeight(0),
            Fissure = true, Strike = rift, LengthM = 500f + 4500f * _random.NextSingle(),
        };
    }

    /// <summary>
    /// Fissure d'un rift émergé, là où les plaques s'écartent à l'air libre (l'Islande, l'Afar) :
    /// perpendiculaire à l'écartement, longue de 2 à 12 km, qui crache des coulées de basalte
    /// pendant des semaines ou des mois (Holuhraun 2014-2015 : 6 mois, 1,5 km³ ; Laki 1783 : 8 mois).
    /// </summary>
    private Eruption RiftFissure(int index, double start)
    {
        int cell = _riftCells[index];
        Vector3 up = _grid.Positions[cell].Normalized();
        Vector3 strike = _riftStrikes[index];
        Vector3 across = up.Cross(strike);
        float radiusM = PlanetConstants.RadiusKm * 1000f;
        float cellM = _grid.MeanEdgeAngle * radiusM;
        Vector3 position = (up + (strike * (_random.NextSingle() - 0.5f) + across * (_random.NextSingle() - 0.5f) * 0.5f) * (cellM / radiusM)).Normalized();
        strike = (strike - position * strike.Dot(position)).Normalized();   // (tangente au point de la fissure)
        return new Eruption
        {
            Position = position, Vei = _random.NextDouble() < 0.85 ? 0 : 1, Kind = LandformKind.ShieldVolcano, StartDay = start,
            DurationDays = 20 + 220 * _random.NextDouble(), PlumeHeightM = PlumeHeight(0) * 1.5f,
            Fissure = true, Strike = strike, LengthM = 2000f + 10000f * _random.NextSingle(),
        };
    }

    /// <summary>
    /// Essaim de séismes : le magma qui monte ouvre la fissure en cassant la roche (Holuhraun :
    /// des milliers de petits séismes en deux semaines) ; quelques-uns de magnitude 4 à 5,5.
    /// </summary>
    private void AddDikeQuakes(Eruption fissure, double day)
    {
        var (a, b) = fissure.Ends();
        for (int k = 0; k < 4; k++)
        {
            float t = _random.NextSingle();
            Vector3 at = (a * (1 - t) + b * t).Normalized();
            RecentEarthquakes.Add(new Earthquake(at, 4f + 1.5f * _random.NextSingle(), day, BoundaryType.Divergent));
        }
    }

    /// <summary>Avance de dtDays (jusqu'à l'instant day) : nouvelles éruptions et séismes.</summary>
    public void Step(double day, double dtDays)
    {
        RecentEarthquakes.Clear();
        if (_grid == null || dtDays <= 0)
            return;
        double years = dtDays / 365.25;

        // Fin des éruptions passées
        _eruptions.RemoveAll(e => day > e.StartDay + e.DurationDays);

        // Nouvelles éruptions (au plus quelques-unes par pas, même à grande vitesse)
        foreach (var volcano in _volcanoes)
        {
            double interval = (volcano.Kind == LandformKind.ShieldVolcano ? ShieldIntervalYears : StratoIntervalYears)
                              / Math.Max(ActivityFactor, 1e-3);
            if (_random.NextDouble() > 1 - Math.Exp(-years / interval))
                continue;
            bool busy = false;
            foreach (var e in _eruptions)
                if (e.Position == volcano.Position)
                    busy = true;
            if (busy)
                continue;
            int vei = volcano.Kind == LandformKind.ShieldVolcano ? (_random.NextDouble() < 0.7 ? 0 : 1) : StratoVei();
            double start = day - _random.NextDouble() * dtDays;
            if (volcano.Kind == LandformKind.ShieldVolcano && _random.NextDouble() < FlankFissureShare)
            {
                _eruptions.Add(FlankFissure(volcano, start));
                AddDikeQuakes(_eruptions[^1], day);
                continue;
            }
            var eruption = new Eruption
            {
                Position = volcano.Position, Vei = vei, Kind = volcano.Kind, StartDay = start,
                // Durées : de quelques mois à plus d'un an pour les coulées tranquilles, quelques
                // semaines pour une éruption modérée, quelques jours pour une explosion majeure
                DurationDays = vei <= 1 ? 60 + 340 * _random.NextDouble()
                    : vei == 2 ? 15 + 185 * _random.NextDouble()
                    : vei == 3 ? 5 + 55 * _random.NextDouble()
                    : 2 + 18 * _random.NextDouble(),
                PlumeHeightM = PlumeHeight(vei),
            };
            _eruptions.Add(eruption);
            AerosolForcing += AerosolInjection(vei);
        }
        // Éruptions fissurales des rifts émergés
        for (int k = 0; k < _riftCells.Length; k++)
        {
            double interval = RiftFissureIntervalYears / Math.Max(ActivityFactor, 1e-3);
            if (_random.NextDouble() > 1 - Math.Exp(-years / interval) || _eruptions.Count > 40)
                continue;
            var fissure = RiftFissure(k, day - _random.NextDouble() * dtDays);
            _eruptions.Add(fissure);
            AddDikeQuakes(fissure, day);
        }
        AerosolForcing *= (float)Math.Exp(-dtDays / AerosolLifetimeDays);

        // Séismes : nombre attendu de magnitude ≥ 5 pendant le pas (au plus 50 tirés)
        // (le taux terrestre, au prorata de la surface : une petite planète a moins de failles)
        double expected = EarthquakesAbove5PerYear * PlanetConstants.AreaRatio * years;
        int count = Math.Min(50, Poisson(expected));
        for (int k = 0; k < count && _totalFaultWeight > 0; k++)
        {
            int cell = PickFaultCell();
            float magnitude = MinMagnitude - MathF.Log10(MathF.Max(1e-9f, 1f - _random.NextSingle())) / GutenbergRichterB;
            magnitude = MathF.Min(magnitude, MaxMagnitude);
            // Épicentre au hasard dans la cellule
            Vector3 p = _grid.Positions[cell];
            Vector3 jitter = new Vector3(_random.NextSingle() - 0.5f, _random.NextSingle() - 0.5f, _random.NextSingle() - 0.5f) * _grid.MeanEdgeAngle;
            RecentEarthquakes.Add(new Earthquake((p + jitter - p * jitter.Dot(p)).Normalized(), magnitude,
                day - _random.NextDouble() * dtDays, _tectonics.CellBoundary[cell]));
        }
    }

    /// <summary>VEI d'une éruption explosive : chaque degré ~5 à 10 fois plus rare (2 : la plus courante).</summary>
    private int StratoVei()
    {
        double u = _random.NextDouble();
        return u < 0.45 ? 1 : u < 0.8 ? 2 : u < 0.94 ? 3 : u < 0.985 ? 4 : u < 0.998 ? 5 : 6;
    }

    /// <summary>
    /// Hauteur du panache au-dessus de la bouche (m), d'après Newhall et Self (1982) : VEI 2 :
    /// 1 à 5 km, VEI 3 : 3 à 15 km, VEI 4 : 10 à 25 km, VEI 5 : plus de 25 km (Saint-Helens 1980 :
    /// ~24 km), VEI 6 : ~35 km (Pinatubo 1991). Pour VEI 0-1, haut de la fourchette (panache de
    /// gaz et de vapeur au-dessus des fontaines de lave, comme au Kīlauea en 2018).
    /// </summary>
    public static float PlumeHeight(int vei) => vei switch
    {
        0 => 2000f, 1 => 4000f, 2 => 7000f, 3 => 12000f, 4 => 20000f, 5 => 28000f, _ => 35000f,
    };

    /// <summary>Forçage injecté (W/m²) : seules les éruptions de VEI 4 ou plus atteignent la stratosphère.</summary>
    public static float AerosolInjection(int vei) => vei < 4 ? 0f : Vei6ForcingWm2 * MathF.Pow(5f, vei - 6);

    /// <summary>Intensité ressentie (échelle de Mercalli, approximative) à distanceKm d'un séisme
    /// de magnitude M : I ≈ 1,5 M − 3 log10(R) + 1,2, R tenant compte d'une profondeur de ~10 km.</summary>
    public static float FeltIntensity(float magnitude, double distanceKm)
    {
        double r = Math.Sqrt(distanceKm * distanceKm + 100.0);
        return (float)Math.Max(0.0, 1.5 * magnitude - 3.0 * Math.Log10(r) + 1.2);
    }

    private int PickFaultCell()
    {
        float target = _random.NextSingle() * _totalFaultWeight;
        // Recherche dichotomique dans les poids cumulés serait plus rapide ; ~1 000 cellules suffisent ici
        for (int i = 0; i < _faultCells.Length; i++)
        {
            target -= _faultWeights[i];
            if (target <= 0f)
                return _faultCells[i];
        }
        return _faultCells[^1];
    }

    private int Poisson(double mean)
    {
        if (mean > 30)
        {
            double u1 = Math.Max(1e-12, _random.NextDouble()), u2 = _random.NextDouble();
            return Math.Max(0, (int)Math.Round(mean + Math.Sqrt(mean) * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
        }
        double limit = Math.Exp(-mean), product = _random.NextDouble();
        int k = 0;
        while (product > limit)
        {
            k++;
            product *= _random.NextDouble();
        }
        return k;
    }
}
