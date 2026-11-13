using System;
using System.Collections.Generic;
using System.Threading;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Un calque de modification de la croûte (étape 16a) : une grille carrée posée sur le plan
/// tangent à la planète, qui ajoute au relief détaillé une hauteur (m, négative là où la croûte
/// a été arrachée, positive là où elle s'est déposée) et garde la trace du sol remanié (pour la
/// couleur : sol frais, éboulis).
/// <para>Repère : celui des coulées de lave (<see cref="LavaFlow"/>) : la cellule (i, j) a pour
/// centre la direction normalisée de Centre + x X / R + y Y / R ; l'inverse est la projection
/// gnomonique x = R (d·X) / (d·Centre).</para>
/// <para>Les données sont remplacées d'un bloc (<see cref="Publish"/>) : un thread qui calcule
/// un morceau de terrain lit toujours un état complet, jamais une grille à moitié écrite.</para>
/// </summary>
public sealed class CrustPatch
{
    /// <summary>Données publiées : hauteur ajoutée (m) et sol remanié (−1 : arraché, +1 : éboulis).</summary>
    public sealed record Layer(float[] Delta, float[] Debris);

    public int Size { get; }
    public double CellM { get; }
    public Double3 Center { get; }
    public Double3 AxisX { get; }
    public Double3 AxisY { get; }
    /// <summary>
    /// Jour (horloge climatique) du dernier événement : la végétation repousse ensuite. Avancé
    /// quand un calque cumule plusieurs événements (éruptions successives d'un même volcan).
    /// </summary>
    public float Day { get; set; }
    /// <summary>Origine : glissement, éboulement, lave, travaux de la vie (à venir)…</summary>
    public string Kind { get; }
    public double HalfExtentM => Size * CellM / 2;
    /// <summary>Rayon (m) du cercle qui contient toute la grille.</summary>
    public double ReachM => HalfExtentM * Math.Sqrt(2);
    /// <summary>Cosinus de l'angle au centre de la planète couvert par <see cref="ReachM"/>.</summary>
    public double CosReach { get; }

    private volatile Layer _layer;
    public Layer Data => _layer;

    public CrustPatch(Double3 center, int size, double cellM, float day, string kind, Double3? axisX = null, Double3? axisY = null)
    {
        Center = center.Normalized();
        if (axisX is { } x && axisY is { } y)
            (AxisX, AxisY) = (x, y);
        else
            (AxisX, AxisY) = LavaFlow.TangentFrame(Center);
        Size = size;
        CellM = cellM;
        Day = day;
        Kind = kind;
        CosReach = Math.Cos((ReachM + 2 * cellM) / TerrainHeightField.RadiusMeters);
        _layer = new Layer(new float[size * size], new float[size * size]);
    }

    /// <summary>Remplace les données (les tableaux ne doivent plus être modifiés ensuite).</summary>
    public void Publish(float[] delta, float[] debris) => _layer = new Layer(delta, debris);

    /// <summary>
    /// Ajoute une épaisseur (m) aux données actuelles (copie, puis publication d'un bloc). Le
    /// sol remanié prend la valeur <paramref name="debrisCode"/> là où l'ajout dépasse
    /// <paramref name="debrisMinM"/>. Les rangées du bord restent à zéro.
    /// </summary>
    public void Accumulate(float[] added, float debrisCode, float debrisMinM, int margin = 4)
    {
        var layer = _layer;
        var delta = (float[])layer.Delta.Clone();
        var debris = (float[])layer.Debris.Clone();
        for (int j = margin; j < Size - margin; j++)
            for (int i = margin; i < Size - margin; i++)
            {
                int k = j * Size + i;
                delta[k] += added[k];
                if (added[k] > debrisMinM)
                    debris[k] = debrisCode;
            }
        _layer = new Layer(delta, debris);
    }

    /// <summary>Direction (unitaire) du centre de la cellule (i, j).</summary>
    public Double3 DirectionOf(int i, int j) => DirectionAt((i + 0.5) * CellM - HalfExtentM, (j + 0.5) * CellM - HalfExtentM);

    /// <summary>Direction (unitaire) du point (x, y) du plan tangent (m).</summary>
    public Double3 DirectionAt(double x, double y) =>
        (Center + AxisX * (x / TerrainHeightField.RadiusMeters) + AxisY * (y / TerrainHeightField.RadiusMeters)).Normalized();

    /// <summary>Coordonnées (m) d'une direction sur le plan tangent.</summary>
    public (double x, double y) Project(Double3 direction)
    {
        double d = direction.Dot(Center);
        double r = TerrainHeightField.RadiusMeters / d;
        return (direction.Dot(AxisX) * r, direction.Dot(AxisY) * r);
    }

    /// <summary>
    /// Hauteur ajoutée (m) et sol remanié en un point (interpolation bilinéaire) ; 0 hors de la
    /// grille. Les bords de la grille restent à zéro (voir <see cref="Landslide"/>) : pas de marche.
    /// </summary>
    public double Sample(Double3 direction, out float debris)
    {
        debris = 0;
        double dot = direction.Dot(Center);
        if (dot < CosReach)
            return 0;
        var (x, y) = Project(direction);
        double fx = (x + HalfExtentM) / CellM - 0.5, fy = (y + HalfExtentM) / CellM - 0.5;
        if (fx < 0 || fy < 0 || fx >= Size - 1 || fy >= Size - 1)
            return 0;
        int i = (int)fx, j = (int)fy;
        double tx = fx - i, ty = fy - j;
        var layer = _layer;
        int k = j * Size + i;
        double w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
        debris = (float)(layer.Debris[k] * w00 + layer.Debris[k + 1] * w10 + layer.Debris[k + Size] * w01 + layer.Debris[k + Size + 1] * w11);
        return layer.Delta[k] * w00 + layer.Delta[k + 1] * w10 + layer.Delta[k + Size] * w01 + layer.Delta[k + Size + 1] * w11;
    }

    /// <summary>Volume net ajouté (m³ : positif si la grille a reçu plus qu'elle n'a perdu).</summary>
    public double NetVolume()
    {
        double sum = 0;
        foreach (float d in _layer.Delta)
            sum += d;
        return sum * CellM * CellM;
    }
}

/// <summary>
/// La croûte modulaire (étape 16a) : l'ensemble des calques de modification posés sur le relief
/// détaillé. Le relief reste une fonction calculée à la demande (<see cref="TerrainHeightField"/>) ;
/// chaque glissement de terrain, éboulement ou coulée de lave figée y ajoute son calque, et les
/// calques s'additionnent (un glissement peut partir d'un ancien dépôt). Comme le relief de base
/// ne bouge plus une fois la géologie figée (horloge climatique), les modifications restent.
/// <para>Thread-safe : la liste est remplacée d'un bloc à chaque ajout (copie), les threads du
/// terrain lisent toujours une liste complète.</para>
/// </summary>
public sealed class CrustEdits
{
    /// <summary>
    /// Index spatial : la sphère cubique découpée en cases de ce niveau (~80 km) ; chaque calque
    /// est rangé dans les cases qu'il touche, un point n'examine que ceux de sa case. Sans cela,
    /// chaque point du relief passait en revue tous les calques de la planète (des milliers
    /// après quelques siècles d'éruptions).
    /// </summary>
    private const int BucketLevel = 7;
    /// <summary>Rayon maximal d'un calque (m) : au-delà, il déborderait des cases voisines.</summary>
    public const double MaxReachM = 30000;

    private readonly object _lock = new();
    private volatile CrustPatch[] _patches = Array.Empty<CrustPatch>();
    private volatile Dictionary<long, CrustPatch[]> _buckets = new();
    private int _version;

    /// <summary>Augmente à chaque ajout ou publication (les morceaux de terrain sont à refaire).</summary>
    public int Version => Volatile.Read(ref _version);
    public IReadOnlyList<CrustPatch> Patches => _patches;
    public int Count => _patches.Length;

    private static long BucketOf(Double3 direction)
    {
        var (face, s, t) = CubeSphere.FromDirection(direction);
        int n = 1 << BucketLevel;
        int x = Math.Clamp((int)((s + 1) / 2 * n), 0, n - 1), y = Math.Clamp((int)((t + 1) / 2 * n), 0, n - 1);
        return ((long)face << 40) | ((long)x << 20) | (long)y;
    }

    /// <summary>Cases touchées par un calque : celles de son centre et de 16 points de son pourtour.</summary>
    private static HashSet<long> BucketsOf(CrustPatch patch)
    {
        var keys = new HashSet<long> { BucketOf(patch.Center) };
        double reach = Math.Min(patch.ReachM, MaxReachM) + 2 * patch.CellM;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8;
            keys.Add(BucketOf(patch.DirectionAt(reach * Math.Cos(a), reach * Math.Sin(a))));
            keys.Add(BucketOf(patch.DirectionAt(0.5 * reach * Math.Cos(a), 0.5 * reach * Math.Sin(a))));
        }
        return keys;
    }

    public void Add(CrustPatch patch)
    {
        lock (_lock)
        {
            var list = new CrustPatch[_patches.Length + 1];
            Array.Copy(_patches, list, _patches.Length);
            list[^1] = patch;
            // Index copié puis remplacé d'un bloc (les threads du terrain lisent l'ancien entre-temps)
            var buckets = new Dictionary<long, CrustPatch[]>(_buckets);
            foreach (long key in BucketsOf(patch))
            {
                buckets.TryGetValue(key, out var old);
                old ??= Array.Empty<CrustPatch>();
                var grown = new CrustPatch[old.Length + 1];
                Array.Copy(old, grown, old.Length);
                grown[^1] = patch;
                buckets[key] = grown;
            }
            _buckets = buckets;
            _patches = list;
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Publie de nouvelles données pour un calque déjà ajouté.</summary>
    public void Publish(CrustPatch patch, float[] delta, float[] debris)
    {
        patch.Publish(delta, debris);
        Interlocked.Increment(ref _version);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _patches = Array.Empty<CrustPatch>();
            _buckets = new Dictionary<long, CrustPatch[]>();
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Hauteur ajoutée (m) par tous les calques en un point, et sol remanié le plus récent.</summary>
    public double Offset(Double3 direction, out float debris, out float day)
    {
        debris = 0;
        day = 0;
        if (_patches.Length == 0 || !_buckets.TryGetValue(BucketOf(direction), out var patches))
            return 0;
        double sum = 0;
        foreach (var patch in patches)
        {
            if (direction.Dot(patch.Center) < patch.CosReach)
                continue;
            sum += patch.Sample(direction, out float d);
            // (le plus récent l'emporte : un nouvel éboulis recouvre un ancien sol)
            if (Math.Abs(d) > 0.02f)
            {
                debris = d;
                day = patch.Day;
            }
        }
        return sum;
    }
}
