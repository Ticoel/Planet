using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>
/// Courants océaniques de surface (étape 11a), diagnostiqués à partir des vents :
/// <list type="bullet">
/// <item><b>Gyres</b> : modèle de Stommel (1948). Le vent fait tourner l'eau (rotationnel de la
/// tension du vent) ; la force de Coriolis, plus forte vers les pôles (effet β), et le frottement
/// équilibrent cette rotation. On résout pour la fonction de courant ψ :
/// r ∇²ψ + β ∂ψ/∂x = rot τ / (ρ H), avec ψ = 0 le long des côtes. Les gyres se referment le long
/// des continents et les courants se resserrent contre les côtes ouest des océans (comme le Gulf
/// Stream ou le Kuroshio) ;</item>
/// <item><b>Dérive de surface</b> (Ekman) : quelques pour cent du vent, déviés de 45° vers la
/// droite dans l'hémisphère nord, vers la gauche au sud.</item>
/// </list>
/// Calcul sur une grille de niveau 6 (~110 km), puis report sur les cellules fines.
/// </summary>
public sealed class OceanCurrents
{
    private const int Level = 6;
    private const float EarthRotation = 7.292e-5f;      // rad/s
    private const float AirDensity = 1.2f;              // kg/m³
    private const float DragCoefficient = 1.3e-3f;
    private const float WaterDensity = 1025f;           // kg/m³
    private const float LayerDepth = 100f;              // m, couche entraînée par le vent
    // Largeur des courants de bord ouest (r / β) : quelques mailles de la grille
    private const float BoundaryWidth = 350e3f;         // m
    private const float BetaReference = 2.0e-11f;       // 1/(m·s), β aux latitudes moyennes
    private const float EkmanFactor = 0.012f;           // part du vent
    private const float SorFactor = 1.75f;
    private const int FirstIterations = 2500;
    private const int UpdateIterations = 400;

    public PlanetGrid FineGrid { get; }
    public PlanetGrid OceanGrid { get; }
    /// <summary>Courant de surface de chaque cellule fine (m/s, tangent ; nul sur les terres).</summary>
    public Vector3[] Current { get; private set; }
    /// <summary>Courant (vec4 : x, y, z, 0) pour la carte graphique.</summary>
    public float[] CurrentPacked { get; private set; }
    public float MaxSpeed { get; private set; }

    // Résultat d'un calcul en tâche de fond, en attente de Publish (fil principal)
    private Vector3[] _nextCurrent;
    private float[] _nextPacked;
    private float _nextMax;
    /// <summary>Dérive de surface due au vent (Ekman), désactivable pour isoler les gyres (tests).</summary>
    public bool IncludeEkman { get; set; } = true;
    /// <summary>Fonction de courant (m²/s) sur la grille océanique, pour les tests.</summary>
    public float[] StreamFunction => _psi;

    private readonly int[] _fineToOcean;
    private readonly int[] _oceanToFine;
    private readonly Vector3[] _east, _north;
    private readonly float[][] _faceWeights;     // longueur de face / aire (1/m)
    private readonly float[][] _laplacian;       // longueur de face / (aire × distance) (1/m²)
    private readonly Vector3[][] _directions;    // direction tangente vers chaque voisine
    private readonly float[] _beta;
    private readonly float[] _psi;
    private readonly float[] _forcing;
    private readonly bool[] _ocean;
    private readonly Vector3[] _oceanCurrent;
    private bool _hasSolution;

    public OceanCurrents(PlanetGrid fineGrid)
    {
        FineGrid = fineGrid;
        OceanGrid = fineGrid.Subdivisions <= Level ? fineGrid : PlanetGrid.Create(Level);
        int fineCount = fineGrid.CellCount, count = OceanGrid.CellCount;
        Current = new Vector3[fineCount];
        CurrentPacked = new float[fineCount * 4];

        _fineToOcean = new int[fineCount];
        int hint = 0;
        for (int c = 0; c < fineCount; c++)
            _fineToOcean[c] = hint = ReferenceEquals(OceanGrid, fineGrid) ? c : OceanGrid.NearestCell(fineGrid.Positions[c], hint);
        _oceanToFine = new int[count];
        hint = 0;
        for (int c = 0; c < count; c++)
            _oceanToFine[c] = hint = ReferenceEquals(OceanGrid, fineGrid) ? c : fineGrid.NearestCell(OceanGrid.Positions[c], hint);

        float radius = PlanetConstants.RadiusKm * 1000f;
        _faceWeights = OceanGrid.ComputeFaceWeights();
        _laplacian = new float[count][];
        _directions = new Vector3[count][];
        _east = new Vector3[count];
        _north = new Vector3[count];
        _beta = new float[count];
        for (int c = 0; c < count; c++)
        {
            Vector3 p = OceanGrid.Positions[c];
            Vector3 east = Vector3.Up.Cross(p);
            _east[c] = east.LengthSquared() < 1e-8f ? Vector3.Right : east.Normalized();
            _north[c] = p.Cross(_east[c]).Normalized();
            float cosLatitude = MathF.Sqrt(MathF.Max(0f, 1f - p.Y * p.Y));
            _beta[c] = 2f * EarthRotation * cosLatitude / radius;
            int[] neighbors = OceanGrid.Neighbors[c];
            _laplacian[c] = new float[neighbors.Length];
            _directions[c] = new Vector3[neighbors.Length];
            for (int k = 0; k < neighbors.Length; k++)
            {
                _faceWeights[c][k] /= radius;
                Vector3 d = OceanGrid.Positions[neighbors[k]] - p;
                _directions[c][k] = (d - p * d.Dot(p)).Normalized();
                _laplacian[c][k] = _faceWeights[c][k] / (d.Length() * radius);
            }
        }
        _psi = new float[count];
        _forcing = new float[count];
        _ocean = new bool[count];
        _oceanCurrent = new Vector3[count];
    }

    /// <summary>Nouveau relief : masque terre/mer (la fonction de courant repart de zéro).</summary>
    public void SetSurface(float[] fineElevationKm)
    {
        for (int c = 0; c < _ocean.Length; c++)
        {
            _ocean[c] = fineElevationKm[_oceanToFine[c]] < 0f;
            _psi[c] = 0f;
        }
        _hasSolution = false;
        lock (this)
        {
            Current = new Vector3[FineGrid.CellCount];
            CurrentPacked = new float[FineGrid.CellCount * 4];
            MaxSpeed = 0f;
            _nextCurrent = null;
            _nextPacked = null;
        }
    }

    /// <summary>Recalcule les courants à partir du vent de surface des cellules fines.</summary>
    public void Compute(Vector3[] fineWind)
    {
        int count = OceanGrid.CellCount;
        // Tension du vent (N/m²) puis rotationnel : rot τ = −div(k × τ)
        var stress = new Vector3[count];
        for (int c = 0; c < count; c++)
        {
            Vector3 wind = fineWind[_oceanToFine[c]];
            stress[c] = AirDensity * DragCoefficient * wind.Length() * wind;
        }
        for (int c = 0; c < count; c++)
        {
            Vector3 up = OceanGrid.Positions[c];
            int[] neighbors = OceanGrid.Neighbors[c];
            float divergence = 0f;
            for (int k = 0; k < neighbors.Length; k++)
            {
                Vector3 face = 0.5f * (up.Cross(stress[c]) + OceanGrid.Positions[neighbors[k]].Cross(stress[neighbors[k]]));
                divergence += face.Dot(_directions[c][k]) * _faceWeights[c][k];
            }
            _forcing[c] = -divergence / (WaterDensity * LayerDepth);
        }

        Solve(_hasSolution ? UpdateIterations : FirstIterations);
        _hasSolution = true;

        // Vitesse : u = k × ∇ψ (gradient de Green-Gauss, ψ = 0 sur les terres), plus la dérive d'Ekman
        for (int c = 0; c < count; c++)
        {
            if (!_ocean[c])
            {
                _oceanCurrent[c] = Vector3.Zero;
                continue;
            }
            Vector3 up = OceanGrid.Positions[c];
            int[] neighbors = OceanGrid.Neighbors[c];
            Vector3 gradient = Vector3.Zero;
            int seaNeighbors = 0;
            for (int k = 0; k < neighbors.Length; k++)
            {
                int n = neighbors[k];
                float psiN = _ocean[n] ? _psi[n] : 0f;
                gradient += _directions[c][k] * (0.5f * (psiN - _psi[c]) * _faceWeights[c][k]);
                if (_ocean[n]) seaNeighbors++;
            }
            Vector3 gyre = up.Cross(gradient);

            Vector3 wind = fineWind[_oceanToFine[c]];
            float latitudeSign = Mathf.Clamp(up.Y / 0.09f, -1f, 1f);   // nul à l'équateur, ±1 au-delà de ~5°
            float angle = -MathF.PI / 4f * latitudeSign;
            Vector3 ekman = (wind * MathF.Cos(angle) + up.Cross(wind) * MathF.Sin(angle)) * EkmanFactor;
            // Près des côtes, la dérive ne peut pas entrer dans les terres
            ekman *= (float)seaNeighbors / neighbors.Length;
            for (int k = 0; k < neighbors.Length; k++)
                if (!_ocean[neighbors[k]])
                {
                    float into = ekman.Dot(_directions[c][k]);
                    if (into > 0f)
                        ekman -= _directions[c][k] * into;
                }
            Vector3 current = IncludeEkman ? gyre + ekman : gyre;
            _oceanCurrent[c] = current - up * current.Dot(up);
        }

        // Report sur les cellules fines (cellules de mer seulement), dans de nouveaux tableaux :
        // le calcul peut tourner en tâche de fond pendant que le climat lit les anciens
        float max = 0f;
        var next = new Vector3[FineGrid.CellCount];
        var packed = new float[FineGrid.CellCount * 4];
        for (int c = 0; c < FineGrid.CellCount; c++)
        {
            int o = _fineToOcean[c];
            Vector3 current = _ocean[o] ? _oceanCurrent[o] : Vector3.Zero;
            next[c] = current;
            packed[c * 4] = current.X;
            packed[c * 4 + 1] = current.Y;
            packed[c * 4 + 2] = current.Z;
            max = MathF.Max(max, current.Length());
        }
        lock (this)
        {
            _nextCurrent = next;
            _nextPacked = packed;
            _nextMax = max;
        }
    }

    /// <summary>Rend visible le dernier calcul (fil principal). Vrai s'il y avait du nouveau.</summary>
    public bool Publish()
    {
        lock (this)
        {
            if (_nextCurrent == null)
                return false;
            Current = _nextCurrent;
            CurrentPacked = _nextPacked;
            MaxSpeed = _nextMax;
            _nextCurrent = null;
            _nextPacked = null;
            return true;
        }
    }

    /// <summary>
    /// Résolution de r ∇²ψ + β ∂ψ/∂x = F par sur-relaxation (Gauss-Seidel accéléré, séquentiel :
    /// résultat reproductible). ∂ψ/∂x : gradient de Green-Gauss projeté vers l'est.
    /// </summary>
    private void Solve(int iterations)
    {
        int count = OceanGrid.CellCount;
        float friction = BetaReference * BoundaryWidth;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            for (int c = 0; c < count; c++)
            {
                if (!_ocean[c])
                    continue;
                int[] neighbors = OceanGrid.Neighbors[c];
                float diagonal = 0f, offDiagonal = 0f;
                for (int k = 0; k < neighbors.Length; k++)
                {
                    float coefficient = friction * _laplacian[c][k]
                                        + _beta[c] * 0.5f * _faceWeights[c][k] * _directions[c][k].Dot(_east[c]);
                    diagonal += coefficient;
                    int n = neighbors[k];
                    if (_ocean[n])
                        offDiagonal += coefficient * _psi[n];
                }
                if (MathF.Abs(diagonal) < 1e-30f)
                    continue;
                float target = (offDiagonal - _forcing[c]) / diagonal;
                _psi[c] += SorFactor * (target - _psi[c]);
            }
        }
    }
}
