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

    public PlanetActivity(int seed) => _random = new Random(seed * 31 + 7);

    /// <summary>Éruptions en cours.</summary>
    public IReadOnlyList<Eruption> Eruptions => _eruptions;
    /// <summary>Séismes du dernier pas.</summary>
    public List<Earthquake> RecentEarthquakes { get; } = new();
    /// <summary>Voile d'aérosols volcaniques : forçage radiatif (W/m², positif = moins de Soleil).</summary>
    public float AerosolForcing { get; private set; }
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
        _eruptions.Clear();
        AerosolForcing = 0f;
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
            double interval = volcano.Kind == LandformKind.ShieldVolcano ? ShieldIntervalYears : StratoIntervalYears;
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
        AerosolForcing *= (float)Math.Exp(-dtDays / AerosolLifetimeDays);

        // Séismes : nombre attendu de magnitude ≥ 5 pendant le pas (au plus 50 tirés)
        double expected = EarthquakesAbove5PerYear * years;
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
