using System;
using System.Threading.Tasks;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>
/// Vents de surface « diagnostiques » : déduits à chaque instant du champ de température,
/// comme dans les modèles de climat simplifiés.
/// <list type="number">
/// <item><b>Pression</b> = ceintures de la circulation générale (basses pressions équatoriales,
/// anticyclones subtropicaux vers 30°, dépressions vers 60°, hautes pressions polaires), qui
/// suivent l'équateur thermique au fil des saisons, + anomalies thermiques : une région plus
/// chaude que sa latitude (continent en été) a une pression plus basse, une région plus froide
/// (continent en hiver) une pression plus haute. D'où moussons et anticyclones continentaux.</item>
/// <item><b>Vent</b> = équilibre entre la force de pression (des hautes vers les basses pressions),
/// la force de Coriolis due à la rotation (déviation à droite dans l'hémisphère nord) et le
/// frottement du sol (qui fait traverser les isobares vers les basses pressions).</item>
/// </list>
/// Les vents sont des structures de grande échelle : ils sont calculés sur une grille climatique
/// grossière (~10 000 cellules) puis reportés sur la grille fine.
/// </summary>
public sealed class WindModel
{
    private const int MaxClimateLevel = 5;

    // Ceintures de pression : écarts à 1013 hPa (hPa), centrés sur des latitudes décalées
    // avec l'équateur thermique, largeur gaussienne de 12°
    private static readonly (float latitudeDeg, float anomalyHpa)[] PressureBelts =
    {
        (0f, -7f), (30f, 10f), (-30f, 10f), (62f, -12f), (-62f, -12f), (90f, 6f), (-90f, 6f),
    };
    private const float BeltWidthDeg = 12f;
    private const float ReferencePressureHpa = 1013f;

    // Anomalie de pression par degré d'écart à la température moyenne de la latitude (hPa/K)
    private const float ThermalPressurePerKelvin = 1.0f;
    // Échelle de lissage des anomalies thermiques de pression (km)
    private const float ThermalSmoothingKm = 600f;

    // Équateur thermique : latitude la plus chaude, bornée à ±25°. Les ceintures de pression ne
    // le suivent qu'en partie (35 %) et dans la limite de ±12° : sur Terre, la convergence
    // intertropicale moyenne oscille entre ~5° S et ~10° N. Piège constaté : décaler les ceintures
    // de toute la latitude la plus chaude (jusqu'à 23°) inverse les alizés
    private const float MaxThermalEquatorDeg = 25f;
    private const float BeltShiftFactor = 0.35f;
    private const float MaxBeltShiftDeg = 12f;

    // Masse volumique de l'air en surface (kg/m³), rotation de la planète (rad/s, jour sidéral
    // terrestre), frottement de surface (s⁻¹, ~ amortissement en 7 h)
    private const float AirDensity = 1.2f;
    private const float RotationRate = 7.292e-5f;
    private const float SurfaceFriction = 4e-5f;
    // Frottement supplémentaire près de l'équateur (convection profonde qui brasse la quantité
    // de mouvement) : ×(1 + 2 e^(−(φ/10°)²)). Sans lui, la force de Coriolis s'annulant,
    // le vent traversant l'équateur atteignait 10 m/s
    private const float EquatorialFrictionBoost = 2f;
    private const float EquatorialFrictionWidthDeg = 10f;
    private const float MaxWindSpeed = 30f;

    public PlanetGrid FineGrid { get; }
    public PlanetGrid ClimateGrid { get; }

    /// <summary>Latitude de la zone de convergence intertropicale, où convergent les alizés (degrés).</summary>
    public float ItczLatitudeDeg { get; private set; }

    /// <summary>Pression au niveau de la mer de chaque cellule fine (hPa).</summary>
    public float[] Pressure { get; }

    /// <summary>Vent de chaque cellule fine (m/s), vecteur 3D tangent à la sphère.</summary>
    public Vector3[] Wind { get; }

    /// <summary>Vent au format des tampons du shader (vec4 par cellule) : x, y, z = vent (m/s),
    /// w = convergence (1/s, positive là où l'air converge et s'élève : pluies de la zone de
    /// convergence intertropicale et des dépressions).</summary>
    public float[] WindPacked { get; }

    /// <summary>Convergence du vent de surface de chaque cellule fine (1/s).</summary>
    public float[] Convergence { get; }

    /// <summary>Vitesse maximale du vent (m/s).</summary>
    public float MaxSpeed { get; private set; }

    private readonly int[] _fineToClimate;
    private readonly int[] _climateCellCounts;
    private readonly Vector3[] _east;
    private readonly Vector3[] _north;
    // Inverse de la matrice des déplacements vers les voisines (gradient par moindres carrés)
    private readonly float[] _gradientInverse;
    private readonly float[] _climateTemperature;
    private readonly float[] _climatePressure;
    private readonly float[] _thermal;
    private readonly float[] _thermalScratch;
    private readonly Vector3[] _climateWind;
    private readonly float[] _climateConvergence;
    // Longueur de face / aire de chaque cellule de la grille climatique (1/m)
    private readonly float[][] _faceWeights;
    private readonly int _smoothingIterations;

    public WindModel(PlanetGrid fineGrid)
    {
        FineGrid = fineGrid;
        int level = Math.Min(MaxClimateLevel, fineGrid.Subdivisions);
        ClimateGrid = level == fineGrid.Subdivisions ? fineGrid : PlanetGrid.Create(level);
        int climateCount = ClimateGrid.CellCount;

        Pressure = new float[fineGrid.CellCount];
        Wind = new Vector3[fineGrid.CellCount];
        WindPacked = new float[fineGrid.CellCount * 4];
        Convergence = new float[fineGrid.CellCount];

        // Correspondance cellule fine → cellule climatique la plus proche
        _fineToClimate = new int[fineGrid.CellCount];
        _climateCellCounts = new int[climateCount];
        int start = 0;
        for (int cell = 0; cell < fineGrid.CellCount; cell++)
        {
            start = ReferenceEquals(ClimateGrid, fineGrid) ? cell : ClimateGrid.NearestCell(fineGrid.Positions[cell], start);
            _fineToClimate[cell] = start;
            _climateCellCounts[start]++;
        }

        // Repère local (est, nord) et matrice du gradient de chaque cellule climatique
        _east = new Vector3[climateCount];
        _north = new Vector3[climateCount];
        _gradientInverse = new float[climateCount * 4];
        float radiusMeters = PlanetConstants.RadiusKm * 1000f;
        for (int c = 0; c < climateCount; c++)
        {
            Vector3 p = ClimateGrid.Positions[c];
            Vector3 east = Vector3.Up.Cross(p);
            east = east.LengthSquared() < 1e-8f ? Vector3.Right : east.Normalized();
            _east[c] = east;
            _north[c] = p.Cross(east).Normalized();

            float mxx = 0f, mxy = 0f, myy = 0f;
            foreach (int n in ClimateGrid.Neighbors[c])
            {
                Vector3 d = ClimateGrid.Positions[n] - p;
                float dx = d.Dot(east) * radiusMeters, dy = d.Dot(_north[c]) * radiusMeters;
                mxx += dx * dx;
                mxy += dx * dy;
                myy += dy * dy;
            }
            float det = mxx * myy - mxy * mxy;
            _gradientInverse[c * 4] = myy / det;
            _gradientInverse[c * 4 + 1] = -mxy / det;
            _gradientInverse[c * 4 + 2] = -mxy / det;
            _gradientInverse[c * 4 + 3] = mxx / det;
        }

        _climateTemperature = new float[climateCount];
        _climatePressure = new float[climateCount];
        _thermal = new float[climateCount];
        _thermalScratch = new float[climateCount];
        _climateWind = new Vector3[climateCount];
        _climateConvergence = new float[climateCount];
        _faceWeights = ClimateGrid.ComputeFaceWeights();
        foreach (float[] weights in _faceWeights)
            for (int k = 0; k < weights.Length; k++)
                weights[k] /= PlanetConstants.RadiusKm * 1000f;

        float edgeKm = ClimateGrid.MeanEdgeAngle * PlanetConstants.RadiusKm;
        _smoothingIterations = Math.Max(1, (int)MathF.Round(ThermalSmoothingKm * ThermalSmoothingKm / (edgeKm * edgeKm)));
    }

    /// <summary>Recalcule pression et vents à partir de la température au niveau de la mer.</summary>
    public void Update(float[] seaLevelTemperature)
    {
        int climateCount = ClimateGrid.CellCount;

        // 1. Température moyenne de chaque cellule climatique
        Array.Clear(_climateTemperature);
        for (int cell = 0; cell < FineGrid.CellCount; cell++)
            _climateTemperature[_fineToClimate[cell]] += seaLevelTemperature[cell];
        for (int c = 0; c < climateCount; c++)
            _climateTemperature[c] /= Math.Max(1, _climateCellCounts[c]);

        // 2. Moyennes par bande de latitude (2°) et équateur thermique
        const int bands = 90;
        var bandSum = new double[bands];
        var bandCount = new int[bands];
        for (int c = 0; c < climateCount; c++)
        {
            int band = BandOf(ClimateGrid.Positions[c].Y, bands);
            bandSum[band] += _climateTemperature[c];
            bandCount[band]++;
        }
        var bandMean = new float[bands];
        for (int b = 0; b < bands; b++)
            bandMean[b] = bandCount[b] > 0 ? (float)(bandSum[b] / bandCount[b]) : float.NaN;
        FillGaps(bandMean);
        ItczLatitudeDeg = Mathf.Clamp(BeltShiftFactor * FindThermalEquator(bandMean), -MaxBeltShiftDeg, MaxBeltShiftDeg);

        // 3. Anomalie thermique (écart à la moyenne de la latitude), lissée
        for (int c = 0; c < climateCount; c++)
        {
            float zonal = bandMean[BandOf(ClimateGrid.Positions[c].Y, bands)];
            _thermal[c] = -ThermalPressurePerKelvin * (_climateTemperature[c] - zonal);
        }
        for (int iteration = 0; iteration < _smoothingIterations; iteration++)
        {
            Parallel.For(0, climateCount, c =>
            {
                float sum = _thermal[c];
                foreach (int n in ClimateGrid.Neighbors[c])
                    sum += _thermal[n];
                _thermalScratch[c] = sum / (ClimateGrid.Neighbors[c].Length + 1);
            });
            Array.Copy(_thermalScratch, _thermal, climateCount);
        }

        // 4. Pression totale (hPa)
        float itcz = ItczLatitudeDeg;
        for (int c = 0; c < climateCount; c++)
        {
            float latitude = Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(ClimateGrid.Positions[c].Y, -1f, 1f)));
            // Les ceintures suivent l'équateur thermique, de moins en moins vers les pôles
            float shifted = latitude - itcz * (1f - MathF.Abs(latitude) / 90f);
            float belts = 0f;
            foreach (var (beltLatitude, anomaly) in PressureBelts)
            {
                float x = (shifted - beltLatitude) / BeltWidthDeg;
                belts += anomaly * MathF.Exp(-0.5f * x * x);
            }
            _climatePressure[c] = ReferencePressureHpa + belts + _thermal[c];
        }

        // 5. Vent : équilibre pression / Coriolis / frottement
        Parallel.For(0, climateCount, c => _climateWind[c] = ComputeWind(c));

        // 6. Convergence : flux entrant moins sortant à travers les faces de chaque cellule
        //    (pondéré par longueur de face / aire, géométrie exacte des cellules)
        Parallel.For(0, climateCount, c =>
        {
            Vector3 p = ClimateGrid.Positions[c];
            float outflow = 0f;
            int[] neighbors = ClimateGrid.Neighbors[c];
            for (int k = 0; k < neighbors.Length; k++)
            {
                int n = neighbors[k];
                Vector3 d = ClimateGrid.Positions[n] - p;
                Vector3 direction = (d - p * d.Dot(p)).Normalized();
                outflow += 0.5f * (_climateWind[c] + _climateWind[n]).Dot(direction) * _faceWeights[c][k];
            }
            _climateConvergence[c] = -outflow;
        });

        // 7. Report sur la grille fine
        float maxSpeed = 0f;
        for (int cell = 0; cell < FineGrid.CellCount; cell++)
        {
            int c = _fineToClimate[cell];
            Pressure[cell] = _climatePressure[c];
            Vector3 wind = _climateWind[c];
            Wind[cell] = wind;
            WindPacked[cell * 4] = wind.X;
            WindPacked[cell * 4 + 1] = wind.Y;
            WindPacked[cell * 4 + 2] = wind.Z;
            WindPacked[cell * 4 + 3] = _climateConvergence[c];
            Convergence[cell] = _climateConvergence[c];
            maxSpeed = MathF.Max(maxSpeed, wind.Length());
        }
        MaxSpeed = maxSpeed;
    }

    private Vector3 ComputeWind(int c)
    {
        Vector3 p = ClimateGrid.Positions[c];
        float radiusMeters = PlanetConstants.RadiusKm * 1000f;

        // Gradient de pression (Pa/m) par moindres carrés sur les voisines
        float bx = 0f, by = 0f;
        foreach (int n in ClimateGrid.Neighbors[c])
        {
            Vector3 d = ClimateGrid.Positions[n] - p;
            float dx = d.Dot(_east[c]) * radiusMeters, dy = d.Dot(_north[c]) * radiusMeters;
            float dp = (_climatePressure[n] - _climatePressure[c]) * 100f;   // hPa → Pa
            bx += dp * dx;
            by += dp * dy;
        }
        float gradX = _gradientInverse[c * 4] * bx + _gradientInverse[c * 4 + 1] * by;
        float gradY = _gradientInverse[c * 4 + 2] * bx + _gradientInverse[c * 4 + 3] * by;

        // Accélération due à la pression, vers les basses pressions
        float gx = -gradX / AirDensity, gy = -gradY / AirDensity;

        // f k×v + r v = G, résolu exactement : u (vers l'est), v (vers le nord)
        float f = 2f * RotationRate * Mathf.Clamp(p.Y, -1f, 1f);
        float latitudeDeg = Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(p.Y, -1f, 1f)));
        float x = latitudeDeg / EquatorialFrictionWidthDeg;
        float r = SurfaceFriction * (1f + EquatorialFrictionBoost * MathF.Exp(-x * x));
        float denominator = r * r + f * f;
        float u = (r * gx + f * gy) / denominator;
        float v = (r * gy - f * gx) / denominator;

        Vector3 wind = _east[c] * u + _north[c] * v;
        float speed = wind.Length();
        return speed > MaxWindSpeed ? wind * (MaxWindSpeed / speed) : wind;
    }

    private static int BandOf(float sinLatitude, int bands)
    {
        float latitude = Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(sinLatitude, -1f, 1f)));
        return Math.Clamp((int)((latitude + 90f) / 180f * bands), 0, bands - 1);
    }

    private static void FillGaps(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (float.IsNaN(values[i]))
                values[i] = i > 0 ? values[i - 1] : 0f;
    }

    /// <summary>Latitude la plus chaude (moyenne par bande, lissée), bornée à ±25°.</summary>
    private static float FindThermalEquator(float[] bandMean)
    {
        int bands = bandMean.Length;
        float best = float.NegativeInfinity, bestLatitude = 0f;
        for (int b = 1; b < bands - 1; b++)
        {
            float latitude = (b + 0.5f) / bands * 180f - 90f;
            if (MathF.Abs(latitude) > MaxThermalEquatorDeg)
                continue;
            float smoothed = 0.25f * bandMean[b - 1] + 0.5f * bandMean[b] + 0.25f * bandMean[b + 1];
            if (smoothed > best)
            {
                best = smoothed;
                bestLatitude = latitude;
            }
        }
        return bestLatitude;
    }
}
