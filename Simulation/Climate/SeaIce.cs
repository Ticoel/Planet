using System;
using Godot;

namespace Planet.Simulation.Climate;

/// <summary>
/// Banquise qui dérive (étape 11e). Concentration de glace (0 à 1) de chaque cellule de mer :
/// <list type="bullet">
/// <item><b>Formation et fonte</b> : la concentration tend vers celle que donne la température
/// (de 0 à −5 °C à 1 à −15 °C, comme dans le modèle de climat), en ~10 jours pour se former,
/// ~30 jours pour fondre.</item>
/// <item><b>Dérive</b> : la glace avance avec le courant de surface, plus ~2 % du vent dévié
/// de ~20° vers la droite au nord (vers la gauche au sud), règle empirique de Nansen tirée de
/// la dérive du Fram (1893-1896). Elle quitte ainsi les mers froides, s'empile contre les côtes
/// sous le vent et fond en dérivant vers des eaux plus chaudes.</item>
/// </list>
/// Transport par un schéma amont conservatif (ce qui sort d'une cellule entre dans sa voisine),
/// la glace ne traversant pas les côtes. La banquise sert à l'affichage ; le modèle de climat
/// garde sa banquise déduite de la température (albédo, isolation).
/// </summary>
public sealed class SeaIce
{
    public const float WindDriftFactor = 0.02f;
    public const float WindTurnRadians = 20f * MathF.PI / 180f;
    public const float RelaxationDays = 10f;
    public const float MeltSlowdown = 3f;

    private readonly PlanetGrid _grid;
    private readonly float[][] _faceWeights;   // longueur de face / aire (1/m)
    private readonly Vector3[][] _directions;
    private readonly bool[] _ocean;
    private float[] _next;
    private readonly Vector3[] _velocity;   // réutilisé à chaque pas (pas d'allocation par image)

    /// <summary>Concentration de glace de chaque cellule (0 à 1 ; 0 sur les terres).</summary>
    public float[] Concentration { get; private set; }

    public SeaIce(PlanetGrid grid)
    {
        _grid = grid;
        int count = grid.CellCount;
        float radius = PlanetConstants.RadiusKm * 1000f;
        _faceWeights = grid.ComputeFaceWeights();
        _directions = new Vector3[count][];
        for (int c = 0; c < count; c++)
        {
            Vector3 p = grid.Positions[c];
            int[] neighbors = grid.Neighbors[c];
            _directions[c] = new Vector3[neighbors.Length];
            for (int k = 0; k < neighbors.Length; k++)
            {
                _faceWeights[c][k] /= radius;
                Vector3 d = grid.Positions[neighbors[k]] - p;
                _directions[c][k] = (d - p * d.Dot(p)).Normalized();
            }
        }
        _ocean = new bool[count];
        Concentration = new float[count];
        _next = new float[count];
        _velocity = new Vector3[count];
    }

    /// <summary>Glace d'équilibre à une température de surface (°C).</summary>
    public static float Equilibrium(float surfaceCelsius) =>
        Mathf.Clamp((AtmosphereModel.IceStartCelsius - surfaceCelsius)
                    / (AtmosphereModel.IceStartCelsius - AtmosphereModel.IceFullCelsius), 0f, 1f);

    /// <summary>Nouveau relief : glace d'équilibre partout.</summary>
    public void Reset(float[] elevationKm, float[] surfaceCelsius)
    {
        for (int c = 0; c < _ocean.Length; c++)
        {
            _ocean[c] = elevationKm[c] < 0f;
            Concentration[c] = _ocean[c] ? Equilibrium(surfaceCelsius[c]) : 0f;
        }
    }

    /// <summary>Vitesse de dérive de la glace (m/s, tangente) en un point.</summary>
    public static Vector3 DriftVelocity(Vector3 up, Vector3 current, Vector3 wind)
    {
        // Rotation autour de la verticale : vers la droite (sens horaire vu d'en haut) au nord
        float angle = -WindTurnRadians * MathF.Sign(up.Y);
        Vector3 turned = wind * MathF.Cos(angle) + up.Cross(wind) * MathF.Sin(angle);
        return current + turned * WindDriftFactor;
    }

    private const int MaxSubsteps = 64;

    /// <summary>Avance de dtDays : dérive (courant + vent), puis formation et fonte.</summary>
    /// <param name="current">Courant de surface (m/s), ou null.</param>
    /// <param name="wind">Vent de surface (m/s), ou null.</param>
    public void Step(double dtDays, float[] surfaceCelsius, Vector3[] current, Vector3[] wind)
    {
        int count = _grid.CellCount;
        double seconds = dtDays * 86400.0;
        // Sous-pas pour la stabilité : la glace ne doit pas traverser plus d'une demi-cellule
        // (cellules de ~60 km, dérive jusqu'à ~0,5 m/s : ~1 jour par sous-pas)
        var velocity = _velocity;
        float maxRate = 0f;
        for (int c = 0; c < count; c++)
        {
            if (!_ocean[c])
                continue;
            velocity[c] = DriftVelocity(_grid.Positions[c], current?[c] ?? Vector3.Zero, wind?[c] ?? Vector3.Zero);
            float outflow = 0f;
            for (int k = 0; k < _directions[c].Length; k++)
                outflow += MathF.Max(0f, velocity[c].Dot(_directions[c][k])) * _faceWeights[c][k];
            maxRate = MathF.Max(maxRate, outflow);
        }
        // (borné : une vitesse aberrante ne doit jamais demander des milliards de sous-pas)
        double wanted = seconds * maxRate / 0.5;
        int substeps = wanted >= 1 && wanted < MaxSubsteps ? (int)Math.Ceiling(wanted) : wanted >= MaxSubsteps ? MaxSubsteps : 1;
        float dt = (float)(seconds / substeps);
        for (int step = 0; step < substeps; step++)
        {
            float[] a = Concentration;
            for (int c = 0; c < count; c++)
            {
                if (!_ocean[c])
                {
                    _next[c] = 0f;
                    continue;
                }
                float change = 0f;
                int[] neighbors = _grid.Neighbors[c];
                for (int k = 0; k < neighbors.Length; k++)
                {
                    int n = neighbors[k];
                    if (!_ocean[n])
                        continue;
                    float flow = 0.5f * (velocity[c] + velocity[n]).Dot(_directions[c][k]) * _faceWeights[c][k];
                    change -= flow > 0f ? flow * a[c] : flow * a[n];
                }
                _next[c] = a[c] + dt * change;
            }
            (Concentration, _next) = (_next, Concentration);
        }

        // Formation et fonte ; une cellule ne peut pas être plus que pleine (l'excédent, glace
        // empilée en crêtes de pression, est ignoré)
        // (la glace fond plus lentement qu'elle ne se forme : il faut des semaines pour fondre
        // 1 à 3 m de glace, d'où des langues de glace qui dérivent loin des mers où elle naît)
        float grow = 1f - MathF.Exp(-(float)dtDays / RelaxationDays);
        float melt = 1f - MathF.Exp(-(float)dtDays / (MeltSlowdown * RelaxationDays));
        for (int c = 0; c < count; c++)
        {
            if (!_ocean[c])
                continue;
            float a = Concentration[c];
            float target = Equilibrium(surfaceCelsius[c]);
            a += (target - a) * (target > a ? grow : melt);
            Concentration[c] = Mathf.Clamp(a, 0f, 1f);
        }
    }
}
