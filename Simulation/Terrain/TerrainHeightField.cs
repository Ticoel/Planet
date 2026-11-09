using System;
using Godot;
using Planet.Simulation.Climate;

namespace Planet.Simulation.Terrain;

/// <summary>Résultat d'un échantillon du relief détaillé.</summary>
public readonly record struct TerrainSample(double Height, double WaterLevel, double River)
{
    /// <summary>Altitude de la surface visible (le sol, ou l'eau qui le recouvre).</summary>
    public double Surface => Math.Max(Height, WaterLevel);
}

/// <summary>
/// Altitude détaillée en tout point de la planète, à l'échelle réelle (étape 9).
/// <list type="number">
/// <item><b>Grandes formes</b> : l'altitude simulée (une valeur par cellule de ~50 km à la
/// subdivision 7), interpolée sans cassure ni pli. Chaque cellule propose un plan (son altitude
/// prolongée par sa pente) ; les plans des cellules proches sont mélangés avec des poids
/// radiaux lisses à support compact (fonction de Wendland C2) de rayon 1,6 fois l'écart entre
/// cellules. La surface est lisse partout et suit l'altitude simulée à quelques dizaines de
/// mètres près au centre des cellules. (Essais écartés : coordonnées barycentriques → plis
/// rectilignes le long des arêtes de la grille ; rayon plus petit que l'écart entre cellules
/// → plateaux hexagonaux.)</item>
/// <item><b>Détail</b> : bruit fractal jusqu'à l'échelle du mètre, dont la forme dépend du
/// relief simulé : plaines presque plates, collines, montagnes à crêtes (bruit « ridged »
/// multifractal, rugosité concentrée sur les crêtes), plancher océanique. Le domaine est
/// légèrement déformé (« domain warping ») pour casser l'aspect régulier du bruit.</item>
/// <item><b>Érosion</b> (étape 9d) : dans les montagnes, chaque octave est atténuée là où le
/// relief déjà construit est raide (méthode des dérivées d'Íñigo Quílez) : versants lisses,
/// détail concentré sur les crêtes et les replats, comme sur un relief usé par l'eau.</item>
/// <item><b>Plis</b> : sur les flancs des chaînes, des crêtes parallèles suivent les courbes de
/// niveau du relief simulé, donc l'axe de la chaîne construite par la collision des plaques
/// (comme les plis du Jura ou du Zagros). Leur espacement est l'écart d'altitude entre deux plis
/// divisé par la pente : quelques km sur les flancs raides, plus sur les pentes douces.</item>
/// <item><b>Vallées</b> : les fleuves calculés sur la grille (priority-flood, débit de chaque
/// cellule) creusent des vallées le long de leur cours, plus larges et profondes avec le débit,
/// avec un fond plat et des méandres ; leur lit est marqué comme eau.</item>
/// <item><b>Lacs</b> : les cuvettes remplies par la simulation hydrologique ont un plan d'eau.</item>
/// <item><b>Côtes</b> : par endroits, falaises (le sol remonte brusquement en quittant la mer),
/// plus hautes dans les reliefs ; ailleurs, plages en pente douce.</item>
/// </list>
/// Le calcul est déterministe (même graine, même relief) et en double précision. Les
/// longueurs d'onde plus courtes que <c>minWavelength</c> sont omises : un morceau de terrain
/// éloigné n'a pas besoin des détails du mètre, ce qui rend le calcul bien plus rapide.
/// Thread-safe : chaque appelant garde son propre indice de recherche (<c>hint</c>).
/// </summary>
public sealed class TerrainHeightField
{
    public static readonly double RadiusMeters = PlanetConstants.RadiusKm * 1000.0;

    // Plus grande longueur d'onde du détail (m) ; les octaves suivantes la divisent par deux
    public const double MaxDetailWavelength = 64000.0;
    // Plus petite longueur d'onde représentable (m)
    public const double MinDetailWavelength = 1.0;

    // Spectre du détail. Pour chaque type de relief : amplitude à la plus grande longueur d'onde
    // (m), puis exposant de Hurst H par gamme d'échelles : l'amplitude est divisée par 2^H à
    // chaque octave (H petit = relief rugueux, qui garde de la force aux petites échelles).
    // Calibrage sur le relief terrestre : dans une chaîne de montagnes, 2 à 3 km de dénivelé
    // entre vallées et sommets sur 10 à 20 km ; collines de 100 à 300 m sur quelques km.
    // (Première version trop faible : ±400 m en montagne, relief presque plat vu du sol.)
    private static readonly double[] PlainSpectrum = Spectrum(80, 0.9, 0.9, 1.3);
    private static readonly double[] HillSpectrum = Spectrum(650, 0.7, 1.0, 1.3);
    private static readonly double[] MountainSpectrum = Spectrum(2300, 0.45, 0.85, 1.3);
    private static readonly double[] SeafloorSpectrum = Spectrum(300, 0.9, 1.0, 1.3);
    // Limites des gammes d'échelles (m) : grandes formes / versants / petites échelles. Sous
    // 300 m, le relief s'adoucit (éboulis, sols) : sans cela les pentes deviendraient de plus
    // en plus fortes aux petites échelles (60° partout au mètre près)
    private const double LargeScaleLimit = 4000.0, SmoothingWavelength = 300.0;
    // Les chaînes les plus hautes sont aussi les plus rugueuses (facteur selon l'altitude simulée)
    private const double MountainReferenceHeight = 2500.0, MountainMinFactor = 0.5, MountainMaxFactor = 1.0;

    /// <summary>Amplitude de chaque octave (64 km, 32 km, … 1 m).</summary>
    private static double[] Spectrum(double amplitude, double largeHurst, double midHurst, double smallHurst)
    {
        var amplitudes = new double[MaxOctaves];
        double wavelength = MaxDetailWavelength;
        for (int k = 0; k < MaxOctaves; k++, wavelength *= 0.5)
        {
            amplitudes[k] = amplitude;
            double hurst = wavelength > LargeScaleLimit ? largeHurst : wavelength > SmoothingWavelength ? midHurst : smallHurst;
            amplitude *= Math.Pow(0.5, hurst);
        }
        return amplitudes;
    }

    // Déformation du domaine : amplitude (m) et longueur d'onde (m)
    private const double WarpAmplitude = 6000.0, WarpWavelength = 90000.0;
    // Moyenne du signal « ridged » (1 − 1,6 |n − 0,12|)², mesurée sur 2 millions de points, retirée
    // pour ne pas surélever les montagnes
    private const double RidgedMean = 0.455;
    // Arrondi des arêtes du relief de montagne (m de longueur d'onde équivalente)
    private const double RidgeRounding = 30.0;

    // Le bruit de gradient vaut 0 sur les nœuds de son réseau cubique : les crêtes (|n| = 0)
    // suivaient donc souvent les arêtes du réseau, en segments rectilignes. Chaque octave est
    // tournée d'une rotation aléatoire (réseaux non alignés entre eux), et les crêtes sont prises
    // au niveau n = RidgeLevel plutôt qu'à 0, à l'écart des nœuds.
    private const double RidgeLevel = 0.12;
    private const int MaxOctaves = 24;
    private static readonly double[] OctaveRotations = BuildRotations();

    private static double[] BuildRotations()
    {
        var random = new Random(20261108);
        var matrices = new double[MaxOctaves * 9];
        for (int k = 0; k < MaxOctaves; k++)
        {
            // Quaternion unitaire aléatoire → matrice de rotation
            double a = random.NextDouble() * 2 - 1, b = random.NextDouble() * 2 - 1;
            double c = random.NextDouble() * 2 - 1, d = random.NextDouble() * 2 - 1;
            double norm = Math.Sqrt(a * a + b * b + c * c + d * d);
            a /= norm; b /= norm; c /= norm; d /= norm;
            double[] m =
            {
                a * a + b * b - c * c - d * d, 2 * (b * c - a * d), 2 * (b * d + a * c),
                2 * (b * c + a * d), a * a - b * b + c * c - d * d, 2 * (c * d - a * b),
                2 * (b * d - a * c), 2 * (c * d + a * b), a * a - b * b - c * c + d * d,
            };
            Array.Copy(m, 0, matrices, k * 9, 9);
        }
        return matrices;
    }

    public PlanetGrid Grid { get; }
    public int Seed { get; }
    /// <summary>Réseau hydrographique utilisé pour les vallées et les lacs (null sans ruissellement).</summary>
    public HydrologyResult Hydrology { get; }

    // Érosion : atténuation des octaves de montagne selon la pente déjà construite
    private const double ErosionStrength = 2.5;
    // Plis : écart d'altitude du relief simulé entre deux plis (m), amplitudes (m)
    private const double FoldHeightSpacing = 260.0, MountainFoldAmplitude = 700.0, HillFoldAmplitude = 150.0;
    // Là où les plis sont bien marqués, le bruit isotrope des montagnes est réduit d'autant
    private const double FoldNoiseReduction = 0.4;
    // Vallées : débit minimal (m³/s), largeur et profondeur selon le débit
    private const double ValleyMinDischarge = 200.0;
    private const int ValleyMinUpstreamCells = 3;
    private const double MeanderAmplitude = 2500.0, MeanderWavelength = 14000.0;
    // Côtes : hauteur des falaises (m) selon le relief
    private const double FlatCliffHeight = 25.0, RuggedCliffHeight = 160.0;
    // Lacs : part des cellules voisines en lac à partir de laquelle un point est dans le lac
    private const double LakeThreshold = 0.45;
    private const double LakeEdgeShare = 0.08;

    private readonly int[] _downstream;
    private readonly float[] _discharge;
    private readonly bool[] _riverCell;
    // Tracé de chaque fleuve d'une cellule à la suivante : courbe lissée (Catmull-Rom) passant
    // par les centres des cellules, ondulée à grande échelle ; null hors des fleuves
    private readonly Double3[][] _riverCurve;
    private const int RiverCurveSegments = 12;
    // Grandes boucles du tracé : écart latéral maximal, en part de la longueur du tronçon
    private const double RiverBendAmplitude = 0.18, RiverBendWavelength = 45000.0;
    private readonly bool[] _lake;
    private readonly double[] _lakeLevel;   // m

    private readonly Double3[] _positions;
    private readonly double[] _elevation;   // m
    private readonly Double3[] _gradient;   // pente (m par m), vecteur tangent
    private readonly double[] _supportSquared;   // rayon d'influence de chaque cellule (sphère unité), au carré

    // Rayon d'influence d'une cellule, en multiple de la distance moyenne à ses voisines
    private const double SupportFraction = 1.6;

    /// <param name="runoffMm">Ruissellement annuel de chaque cellule (mm/an), pour les vallées
    /// et les lacs ; null : ni vallées ni lacs.</param>
    public TerrainHeightField(PlanetGrid grid, float[] elevationKm, int seed, float[] runoffMm = null)
    {
        Grid = grid;
        Seed = seed;
        int count = grid.CellCount;
        _positions = new Double3[count];
        _elevation = new double[count];
        _gradient = new Double3[count];
        for (int c = 0; c < count; c++)
        {
            _positions[c] = Double3.From(grid.Positions[c]).Normalized();
            _elevation[c] = elevationKm[c] * 1000.0;
        }
        _supportSquared = new double[count];
        System.Threading.Tasks.Parallel.For(0, count, c =>
        {
            _gradient[c] = LeastSquaresGradient(c);
            double mean = 0;
            foreach (int n in grid.Neighbors[c])
                mean += _positions[c].DistanceTo(_positions[n]);
            mean /= grid.Neighbors[c].Length;
            _supportSquared[c] = Math.Pow(SupportFraction * mean, 2);
        });

        _downstream = new int[count];
        _discharge = new float[count];
        _riverCell = new bool[count];
        _lake = new bool[count];
        _lakeLevel = new double[count];
        Array.Fill(_downstream, -1);
        if (runoffMm != null)
        {
            Hydrology = HydrologyModel.Compute(grid, elevationKm, runoffMm, new float[count]);
            for (int c = 0; c < count; c++)
            {
                _downstream[c] = Hydrology.Downstream[c];
                _discharge[c] = Hydrology.Discharge[c];
                _riverCell[c] = elevationKm[c] > 0 && Hydrology.Downstream[c] >= 0
                                && Hydrology.Discharge[c] >= ValleyMinDischarge
                                && Hydrology.UpstreamCells[c] >= ValleyMinUpstreamCells;
                _lake[c] = Hydrology.Lake[c];
                _lakeLevel[c] = Hydrology.FilledElevation[c] * 1000.0;
            }
        }
        _riverCurve = BuildRiverCurves(count);
    }

    /// <summary>
    /// Tracés courbes des fleuves. Entre deux cellules, une ligne droite de ~60 km se voyait de
    /// loin : on fait passer une courbe lisse (spline de Catmull-Rom) par les centres successifs
    /// (affluent principal en amont, cellule suivante en aval), puis on l'ondule latéralement
    /// avec un bruit (~45 km), à amplitude nulle aux deux bouts pour garder la continuité.
    /// </summary>
    private Double3[][] BuildRiverCurves(int count)
    {
        var curves = new Double3[count][];
        // Affluent principal : la cellule de fleuve d'amont au plus fort débit
        var mainUpstream = new int[count];
        Array.Fill(mainUpstream, -1);
        for (int c = 0; c < count; c++)
        {
            if (!_riverCell[c])
                continue;
            int d = _downstream[c];
            if (mainUpstream[d] < 0 || _discharge[c] > _discharge[mainUpstream[d]])
                mainUpstream[d] = c;
        }
        Double3 End(int c)
        {
            int d = _downstream[c];
            // Embouchure : on s'arrête à mi-chemin, sur la côte
            return _elevation[d] > 0 ? _positions[d] : (_positions[c] + _positions[d]).Normalized();
        }
        for (int c = 0; c < count; c++)
        {
            if (!_riverCell[c])
                continue;
            Double3 a = _positions[c], b = End(c);
            int up = mainUpstream[c], down = _downstream[c];
            Double3 before = up >= 0 ? _positions[up] : a - (b - a);
            Double3 after = _riverCell[down] ? End(down) : b + (b - a);
            Double3 ta = (b - before) * 0.5, tb = (after - a) * 0.5;
            double length = (b - a).Length();
            var points = new Double3[RiverCurveSegments + 1];
            for (int k = 0; k <= RiverCurveSegments; k++)
            {
                double t = (double)k / RiverCurveSegments, t2 = t * t, t3 = t2 * t;
                Double3 point = a * (2 * t3 - 3 * t2 + 1) + ta * (t3 - 2 * t2 + t) + b * (-2 * t3 + 3 * t2) + tb * (t3 - t2);
                point = point.Normalized();
                // Ondulation latérale (perpendiculaire au tracé, dans le plan du sol)
                Double3 tangent = (a * (6 * t2 - 6 * t) + ta * (3 * t2 - 4 * t + 1) + b * (-6 * t2 + 6 * t) + tb * (3 * t2 - 2 * t));
                Double3 side = tangent.Cross(point);
                if (side.LengthSquared() > 1e-20)
                {
                    Double3 q = point * (RadiusMeters / RiverBendWavelength);
                    double bend = GradientNoise.Sample(q.X, q.Y, q.Z, Seed + 311);
                    double envelope = Math.Sin(Math.PI * t);
                    point = (point + side.Normalized() * (bend * RiverBendAmplitude * length * envelope)).Normalized();
                }
                points[k] = point;
            }
            curves[c] = points;
        }
        return curves;
    }

    /// <summary>Pente au centre d'une cellule par moindres carrés sur ses voisines.</summary>
    private Double3 LeastSquaresGradient(int cell)
    {
        Double3 p = _positions[cell];
        Double3 east = Double3.UnitY.Cross(p);
        east = east.LengthSquared() < 1e-12 ? new Double3(1, 0, 0) : east.Normalized();
        Double3 north = p.Cross(east).Normalized();
        double mxx = 0, mxy = 0, myy = 0, bx = 0, by = 0;
        foreach (int n in Grid.Neighbors[cell])
        {
            Double3 d = (_positions[n] - p) * RadiusMeters;
            double dx = d.Dot(east), dy = d.Dot(north), dh = _elevation[n] - _elevation[cell];
            mxx += dx * dx; mxy += dx * dy; myy += dy * dy;
            bx += dh * dx; by += dh * dy;
        }
        double det = mxx * myy - mxy * mxy;
        double gx = (myy * bx - mxy * by) / det;
        double gy = (-mxy * bx + mxx * by) / det;
        return east * gx + north * gy;
    }

    // --- Grandes formes ----------------------------------------------------------------------

    /// <summary>
    /// Triangle de la grille qui contient la direction, et coordonnées barycentriques
    /// (outil de diagnostic : l'interpolation n'en a plus besoin).
    /// <paramref name="hint"/> : dernier triangle trouvé (accélère les appels voisins), −1 au départ.
    /// </summary>
    public int FindTriangle(Double3 direction, ref int hint, out double wa, out double wb, out double wc)
    {
        if (hint >= 0 && Barycentric(hint, direction, out wa, out wb, out wc))
            return hint;

        int start = hint >= 0 ? Grid.Triangles[hint * 3] : 0;
        int nearest = Grid.NearestCell(direction.ToVector3(), start);
        int best = -1;
        double bestMin = double.MinValue;
        // Le triangle contenant le point a presque toujours la cellule la plus proche pour sommet ;
        // à défaut on regarde les triangles des voisines
        int[] neighbors = Grid.Neighbors[nearest];
        for (int k = -1; k < neighbors.Length; k++)
        {
            int cell = k < 0 ? nearest : neighbors[k];
            foreach (int triangle in Grid.CellTriangles[cell])
                {
                    if (Barycentric(triangle, direction, out wa, out wb, out wc))
                    {
                        hint = triangle;
                        return triangle;
                    }
                    double min = Math.Min(wa, Math.Min(wb, wc));
                    if (min > bestMin)
                    {
                        bestMin = min;
                        best = triangle;
                    }
                }
        }
        // Point exactement sur une arête (erreurs d'arrondi) : le triangle le moins « dehors »
        Barycentric(best, direction, out wa, out wb, out wc);
        hint = best;
        return best;
    }

    private bool Barycentric(int triangle, Double3 p, out double wa, out double wb, out double wc)
    {
        Double3 a = _positions[Grid.Triangles[triangle * 3]];
        Double3 b = _positions[Grid.Triangles[triangle * 3 + 1]];
        Double3 c = _positions[Grid.Triangles[triangle * 3 + 2]];
        // Volumes des tétraèdres (O, p, arête) : coordonnées barycentriques de la projection
        // centrale de p sur le plan du triangle
        wa = p.Dot(b.Cross(c));
        wb = p.Dot(c.Cross(a));
        wc = p.Dot(a.Cross(b));
        double sum = wa + wb + wc;
        // Quel que soit le sens de parcours du triangle ; on écarte le triangle diamétralement opposé
        if (Math.Abs(sum) < 1e-300 || p.Dot(a + b + c) <= 0)
        {
            wa = wb = wc = -1;
            return false;
        }
        wa /= sum; wb /= sum; wc /= sum;
        const double tolerance = -1e-12;
        return wa >= tolerance && wb >= tolerance && wc >= tolerance;
    }

    /// <summary>Altitude des grandes formes (m) : l'altitude simulée interpolée sans cassure.</summary>
    public double BaseHeight(Double3 direction, ref int hint)
    {
        Span<int> cells = stackalloc int[64];
        int count = Gather(direction, ref hint, cells);
        return Blend(direction, cells[..count], out _, out _, out _);
    }

    /// <summary>
    /// Cellules qui peuvent influencer un point : la plus proche, ses voisines et les voisines
    /// de celles-ci (rayon d'influence de 1,6 écart : les plus lointaines n'atteignent pas le point).
    /// <paramref name="hint"/> : dernière cellule la plus proche (−1 au départ).
    /// </summary>
    private int Gather(Double3 p, ref int hint, Span<int> cells)
    {
        int nearest = Grid.NearestCell(p.ToVector3(), hint >= 0 ? hint : 0);
        // La marche en float peut s'arrêter sur une voisine à égalité : on vérifie en double
        double best = p.DistanceTo(_positions[nearest]);
        foreach (int n in Grid.Neighbors[nearest])
        {
            double d = p.DistanceTo(_positions[n]);
            if (d < best) { best = d; nearest = n; }
        }
        hint = nearest;

        int count = 0;
        cells[count++] = nearest;
        foreach (int n in Grid.Neighbors[nearest])
            cells[count++] = n;
        int firstRing = count;
        for (int k = 1; k < firstRing; k++)
            foreach (int m in Grid.Neighbors[cells[k]])
                if (cells[..count].IndexOf(m) < 0)
                    cells[count++] = m;
        return count;
    }

    /// <summary>
    /// Mélange des plans des cellules proches (poids de Wendland C2), et de leurs lacs : part du
    /// point couverte par des lacs voisins et niveau moyen de ces lacs.
    /// </summary>
    private double Blend(Double3 p, ReadOnlySpan<int> cells, out Double3 slope, out double lakeShare, out double lakeLevel)
    {
        double weightSum = 0, heightSum = 0, lakeWeight = 0, lakeLevelSum = 0;
        Double3 slopeSum = Double3.Zero;
        foreach (int cell in cells)
        {
            Double3 offset = p - _positions[cell];
            double q2 = offset.LengthSquared() / _supportSquared[cell];
            if (q2 >= 1)
                continue;
            double q = Math.Sqrt(q2);
            double w = Math.Pow(1 - q, 4) * (4 * q + 1);   // Wendland C2
            weightSum += w;
            heightSum += w * (_elevation[cell] + _gradient[cell].Dot(offset) * RadiusMeters);
            slopeSum += _gradient[cell] * w;
            if (_lake[cell])
            {
                lakeWeight += w;
                lakeLevelSum += w * _lakeLevel[cell];
            }
        }
        if (weightSum <= 0)
        {
            // Ne devrait pas arriver (le point est toujours assez près d'une cellule)
            int nearest = cells[0];
            slope = _gradient[nearest];
            lakeShare = 0;
            lakeLevel = 0;
            return _elevation[nearest] + _gradient[nearest].Dot(p - _positions[nearest]) * RadiusMeters;
        }
        slope = slopeSum / weightSum;
        lakeShare = lakeWeight / weightSum;
        lakeLevel = lakeWeight > 0 ? lakeLevelSum / lakeWeight : 0;
        return heightSum / weightSum;
    }

    // --- Détail ----------------------------------------------------------------------------------

    /// <summary>
    /// Altitude détaillée (m, 0 = niveau de la mer) dans une direction, sans les longueurs
    /// d'onde inférieures à <paramref name="minWavelength"/> (m).
    /// </summary>
    public double Height(Double3 direction, double minWavelength, ref int hint) =>
        Sample(direction, minWavelength, ref hint).Height;

    /// <summary>
    /// Relief détaillé complet : altitude du sol (m, 0 = niveau de la mer), niveau de l'eau qui
    /// le recouvre éventuellement (mer à 0 m, ou lac), et part du point occupée par le lit d'un
    /// fleuve (0 à 1).
    /// </summary>
    public TerrainSample Sample(Double3 direction, double minWavelength, ref int hint)
    {
        Span<int> cells = stackalloc int[64];
        int count = Gather(direction, ref hint, cells);
        ReadOnlySpan<int> near = cells[..count];
        double baseHeight = Blend(direction, near, out Double3 slope, out double lakeShare, out double lakeLevel);
        double baseSlope = slope.Length();
        Double3 position = direction * RadiusMeters;

        // Vallées des fleuves (calculées d'abord : le fond des vallées est lissé)
        var (valley, river) = Valley(direction, position, near, baseHeight, minWavelength);

        double folds = Folds(position, baseHeight, baseSlope, SlopeCoherence(position / RadiusMeters, near), minWavelength, out double foldShare);
        double height = baseHeight
                        + Detail(position, baseHeight, baseSlope, minWavelength) * (1 - 0.85 * valley.floor) * (1 - FoldNoiseReduction * foldShare)
                        + folds * (1 - valley.floor);
        // Le fond des vallées reste au-dessus de la mer tant que le sol l'est : le fleuve coule
        // jusqu'à la côte au lieu de transformer toute la plaine en bras de mer
        height = height > 1.0 ? Math.Max(height - valley.depth, 1.0) : height - valley.depth;
        height = Cliffs(position, height, baseHeight, baseSlope, minWavelength);

        // Lacs : au cœur de leur zone, l'eau est au niveau du lac ; vers le bord de la zone, ce
        // niveau s'abaisse progressivement jusqu'à passer sous le sol. La rive tombe là où le
        // terrain croise ce niveau : elle est toujours continue, sans toucher au relief. (Avant :
        // le lac s'arrêtait net sur la limite entre deux cellules, ou, en remontant le sol au
        // bord, formait un mur.)
        double water = 0;
        if (lakeShare > LakeEdgeShare && lakeLevel > 0)
        {
            double full = SmoothStep(LakeEdgeShare, LakeThreshold, lakeShare);
            double dry = height - 20.0;   // niveau sûrement sous le sol
            // Même sous le sol, on garde ce niveau (et non 0) : entre deux sommets, l'eau
            // s'interpole alors en douceur jusqu'à la rive
            water = Math.Max(0.0, dry + (lakeLevel - dry) * full);
        }
        return new TerrainSample(height, water, river);
    }

    /// <summary>
    /// Cohérence de la pente autour d'un point (0 à 1) : 1 si les pentes des cellules voisines
    /// pointent toutes dans la même direction (flanc d'une chaîne allongée), faible au sommet ou
    /// autour d'un massif en dôme.
    /// </summary>
    private double SlopeCoherence(Double3 p, ReadOnlySpan<int> cells)
    {
        Double3 sum = Double3.Zero;
        double length = 0;
        foreach (int cell in cells)
        {
            double q2 = (p - _positions[cell]).LengthSquared() / (2.5 * _supportSquared[cell]);
            if (q2 >= 1)
                continue;
            double w = 1 - q2;
            sum += _gradient[cell] * w;
            length += _gradient[cell].Length() * w;
        }
        return length > 1e-12 ? sum.Length() / length : 1;
    }

    /// <summary>Part de chaque type de relief selon l'altitude et la pente simulées.</summary>
    public static (double plain, double hill, double mountain, double seafloor) ReliefMix(double baseHeight, double baseSlope)
    {
        double land = SmoothStep(-150, 50, baseHeight);
        // Montagnes : relief élevé ou pente forte à grande échelle (flancs des chaînes)
        double mountain = Math.Max(SmoothStep(900, 2800, baseHeight), SmoothStep(0.015, 0.05, baseSlope)) * land;
        double hill = SmoothStep(150, 900, baseHeight) * land * (1 - mountain);
        double plain = land * (1 - mountain - hill);
        return (Math.Max(0, plain), hill, mountain, 1 - land);
    }

    private double Detail(Double3 position, double baseHeight, double baseSlope, double minWavelength)
    {
        var (plain, hill, mountain, seafloor) = ReliefMix(baseHeight, baseSlope);

        // Déformation du domaine (trois bruits basse fréquence décalés)
        double wx = position.X / WarpWavelength, wy = position.Y / WarpWavelength, wz = position.Z / WarpWavelength;
        double px = position.X + WarpAmplitude * GradientNoise.Sample(wx, wy, wz, Seed + 101);
        double py = position.Y + WarpAmplitude * GradientNoise.Sample(wx + 17.3, wy - 4.1, wz + 9.7, Seed + 102);
        double pz = position.Z + WarpAmplitude * GradientNoise.Sample(wx - 8.6, wy + 21.9, wz - 13.2, Seed + 103);

        double lowest = Math.Max(minWavelength, MinDetailWavelength);
        double fbmPlain = 0, fbmHill = 0, fbmSea = 0, ridged = 0;
        double ridgeWeight = 1, erosionWeight = 1;
        double gx = 0, gy = 0, gz = 0;   // pente du relief de montagne déjà construit (m/m)
        int octave = 0;
        for (double wavelength = MaxDetailWavelength; wavelength >= lowest; wavelength *= 0.5, octave++)
        {
            // Dernière octave atténuée progressivement : pas de saut quand le seuil change
            double fade = Math.Clamp(wavelength / lowest - 1.0, 0.0, 1.0);
            double inverse = 1.0 / wavelength;
            int r = Math.Min(octave, MaxOctaves - 1) * 9;
            double qx = (OctaveRotations[r] * px + OctaveRotations[r + 1] * py + OctaveRotations[r + 2] * pz) * inverse;
            double qy = (OctaveRotations[r + 3] * px + OctaveRotations[r + 4] * py + OctaveRotations[r + 5] * pz) * inverse;
            double qz = (OctaveRotations[r + 6] * px + OctaveRotations[r + 7] * py + OctaveRotations[r + 8] * pz) * inverse;
            double n = GradientNoise.Sample(qx, qy, qz, Seed + octave, out double dnx, out double dny, out double dnz);

            int k = Math.Min(octave, MaxOctaves - 1);
            fbmPlain += n * PlainSpectrum[k] * fade;
            fbmHill += n * HillSpectrum[k] * fade;
            fbmSea += n * SeafloorSpectrum[k] * fade;

            // Multifractal « ridged » : crêtes là où le bruit s'annule ; chaque octave est pondérée
            // par la précédente, si bien que la rugosité se concentre sur les crêtes
            // L'arête est arrondie sur une trentaine de mètres (valeur absolue « douce ») : une
            // arête vive, plus fine que le maillage, y dessinait des dents de scie. Largeur fixe,
            // la même à tous les niveaux de détail (sinon le relief sauterait d'un niveau à l'autre)
            double x = n - RidgeLevel;
            double soft = Math.Min(0.3, RidgeRounding / wavelength);
            double rounded = Math.Sqrt(x * x + soft * soft);
            double linear = 1 - (rounded - soft) * 1.6;
            double ridge = linear * linear;
            double weight = ridgeWeight * erosionWeight * fade;
            double amplitude = MountainSpectrum[k] * weight;
            ridged += (ridge - RidgedMean) * amplitude;
            ridgeWeight = Math.Clamp(ridge * 1.4, 0.2, 1.0);

            // Érosion : pente de cette octave (dérivée du bruit tournée vers le repère de la planète)
            double dr = -2 * linear * 1.6 * (x / rounded) * amplitude * inverse;
            gx += dr * (OctaveRotations[r] * dnx + OctaveRotations[r + 3] * dny + OctaveRotations[r + 6] * dnz);
            gy += dr * (OctaveRotations[r + 1] * dnx + OctaveRotations[r + 4] * dny + OctaveRotations[r + 7] * dnz);
            gz += dr * (OctaveRotations[r + 2] * dnx + OctaveRotations[r + 5] * dny + OctaveRotations[r + 8] * dnz);
            erosionWeight = 1.0 / (1.0 + ErosionStrength * (gx * gx + gy * gy + gz * gz));
        }

        double ruggedness = Math.Clamp(baseHeight / MountainReferenceHeight, MountainMinFactor, MountainMaxFactor);
        return plain * fbmPlain + hill * fbmHill + mountain * ruggedness * ridged + seafloor * fbmSea;
    }

    /// <summary>
    /// Plis parallèles aux courbes de niveau du relief simulé : la phase est l'altitude simulée
    /// divisée par l'écart d'altitude entre deux plis (plus un bruit qui ondule les crêtes).
    /// </summary>
    private double Folds(Double3 position, double baseHeight, double baseSlope, double coherence, double minWavelength, out double share)
    {
        share = 0;
        var (_, hill, mountain, _) = ReliefMix(baseHeight, baseSlope);
        double amplitude = mountain * MountainFoldAmplitude + hill * HillFoldAmplitude;
        if (amplitude <= 0 || baseSlope < 1e-4)
            return 0;
        // Espacement horizontal entre deux plis ; inutile si plus fin que le maillage
        double spacing = FoldHeightSpacing / baseSlope;
        // Seulement sur les flancs de chaînes allongées, où la pente garde la même direction sur
        // des dizaines de km : autour d'un massif en dôme, les plis qui suivent les courbes de
        // niveau dessinaient des anneaux concentriques en escalier
        double visibility = Math.Clamp(spacing / (2 * minWavelength) - 1, 0, 1)
                            * SmoothStep(0.004, 0.02, baseSlope)
                            * SmoothStep(0.7, 0.92, coherence);
        if (visibility <= 0)
            return 0;
        // Crêtes ondulées (deux échelles) et morcelées en segments décalés, comme de vrais plis
        double wobble = GradientNoise.Sample(position.X / 40000.0, position.Y / 40000.0, position.Z / 40000.0, Seed + 211)
                        + 0.25 * GradientNoise.Sample(position.X / 15000.0, position.Y / 15000.0, position.Z / 15000.0, Seed + 214);
        double strength = Math.Clamp(0.75 + 0.6 * GradientNoise.Sample(position.X / 90000.0, position.Y / 90000.0, position.Z / 90000.0, Seed + 212), 0, 1)
                          * SmoothStep(-0.6, 0.1, GradientNoise.Sample(position.X / 35000.0, position.Y / 35000.0, position.Z / 35000.0, Seed + 213));
        double phase = baseHeight / FoldHeightSpacing + 1.0 * wobble;
        share = visibility * strength * Math.Min(1, mountain + hill);
        return amplitude * visibility * strength * Math.Cos(2 * Math.PI * phase);
    }

    /// <summary>
    /// Vallée creusée par le fleuve le plus proche : profondeur et largeur selon le débit, fond
    /// plat, méandres (le tracé entre deux cellules est ondulé par un bruit). Renvoie la
    /// profondeur creusée, la part du point en fond de vallée, et la part dans le lit du fleuve.
    /// </summary>
    private ((double depth, double floor) valley, double river) Valley(
        Double3 direction, Double3 position, ReadOnlySpan<int> cells, double baseHeight, double minWavelength)
    {
        bool any = false;
        foreach (int cell in cells)
            if (_riverCell[cell]) { any = true; break; }
        if (!any || baseHeight <= -50)
            return ((0, 0), 0);

        // Méandres : on déplace le point plutôt que le tracé
        double mx = position.X / MeanderWavelength, my = position.Y / MeanderWavelength, mz = position.Z / MeanderWavelength;
        var meander = new Double3(
            GradientNoise.Sample(mx, my, mz, Seed + 301),
            GradientNoise.Sample(mx + 31.7, my - 12.9, mz + 5.3, Seed + 302),
            GradientNoise.Sample(mx - 7.1, my + 44.2, mz - 23.8, Seed + 303));
        // Seule la partie horizontale compte (sinon le point s'écarte du sol et n'est jamais sur le lit)
        meander -= direction * meander.Dot(direction);
        Double3 p = (direction + meander * (MeanderAmplitude / RadiusMeters)).Normalized();

        double bestDepth = 0, bestFloor = 0, bestRiver = 0;
        foreach (int cell in cells)
        {
            if (!_riverCell[cell])
                continue;
            double q = _discharge[cell];
            double width = 300 + 25 * Math.Sqrt(q);              // demi-largeur de la vallée (m)
            Double3[] curve = _riverCurve[cell];
            // Tronçon trop loin : on passe (test grossier sur la sphère englobante de la courbe)
            Double3 middle = curve[RiverCurveSegments / 2];
            double reach = (curve[0] - middle).Length() + (curve[RiverCurveSegments] - middle).Length();
            if ((p - middle).Length() * RadiusMeters > reach * RadiusMeters + width)
                continue;
            // Point le plus proche du tracé courbe (petits segments : sur la corde, ramené sur
            // la sphère)
            double distance = double.MaxValue;
            for (int k = 0; k < RiverCurveSegments; k++)
            {
                Double3 a = curve[k], ab = curve[k + 1] - a;
                double t = Math.Clamp((p - a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-30), 0, 1);
                distance = Math.Min(distance, (a + ab * t).Normalized().DistanceTo(p) * RadiusMeters);
            }
            if (distance >= width)
                continue;
            double channel = 5 + 3 * Math.Sqrt(q);               // largeur du lit (m)
            double floorHalf = Math.Max(channel, 0.12 * width);  // demi-largeur du fond plat
            double depth = Math.Min(60 + 6 * Math.Sqrt(q), 0.6 * Math.Max(baseHeight, 0) + 15);
            double profile = 1 - SmoothStep(floorHalf, width, distance);
            double carve = depth * profile + (distance < channel * 0.5 ? 3.0 : 0.0);
            if (carve > bestDepth)
            {
                bestDepth = carve;
                bestFloor = 1 - SmoothStep(floorHalf * 0.5, floorHalf * 2, distance);
                // Le lit n'est visible que si le maillage est assez fin pour lui
                bestRiver = channel > minWavelength ? 1 - SmoothStep(channel * 0.4, channel * 0.55, distance) : 0;
            }
        }
        return ((bestDepth, bestFloor), bestRiver);
    }

    /// <summary>
    /// Falaises côtières : par endroits (bruit à ~25 km), le sol remonte brusquement de quelques
    /// dizaines à ~150 m en quittant la mer ; ailleurs la côte reste en pente douce (plages).
    /// </summary>
    private double Cliffs(Double3 position, double height, double baseHeight, double baseSlope, double minWavelength)
    {
        if (height <= 0 || baseHeight < -300 || baseHeight > 1500)
            return height;
        var (_, hill, mountain, _) = ReliefMix(baseHeight, baseSlope);
        double rocky = SmoothStep(0.05, 0.45, GradientNoise.Sample(position.X / 25000.0, position.Y / 25000.0, position.Z / 25000.0, Seed + 401));
        double cliff = rocky * (FlatCliffHeight + (RuggedCliffHeight - FlatCliffHeight) * Math.Min(1, hill + mountain));
        // Raideur de la falaise limitée par la finesse du maillage
        double rise = Math.Max(8.0, minWavelength);
        return height + cliff * (1 - Math.Exp(-height / rise));
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }
}
