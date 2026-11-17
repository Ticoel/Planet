using System;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;

namespace Planet.Simulation.Terrain;

/// <summary>Résultat d'un échantillon du relief détaillé.</summary>
/// <summary>
/// Replat creusé dans le relief pour un bassin (source chaude, geyser, mare de boue) : une
/// terrasse horizontale de 2,2 rayons, raccordée au terrain en 1 rayon de plus, avec au centre
/// une cuvette peu profonde. L'eau ne tient pas sur une pente : sur un versant, une source bâtit
/// justement de telles terrasses de dépôts (geysérite, travertin), en déblai côté amont et en
/// remblai côté aval.
/// </summary>
/// <param name="BasinRadiusM">Rayon de la cuvette (m) ; 0 : celui du replat.</param>
/// <param name="Water">La cuvette est pleine d'eau (source chaude, vasque d'un geyser) : une
/// vraie surface d'eau, comme la mer, les lacs et les fleuves, à quelques centimètres sous le bord.</param>
public readonly record struct TerrainBench(Double3 Direction, double RadiusM, double BasinRadiusM = 0, bool Water = false)
{
    public double Basin => BasinRadiusM > 0 ? BasinRadiusM : RadiusM;
}

/// <param name="Debris">Sol remanié par la croûte modulaire (étape 16a) : −1 arraché (sol frais),
/// +1 éboulis ; 0 : sol naturel.</param>
/// <param name="DebrisDay">Jour (horloge climatique) où le sol a été remanié.</param>
/// <param name="Wetland">Delta ou vasière côtière (0 à 1) : plaine de boue au ras de l'eau.</param>
/// <param name="Floodable">L'eau de ce point monte avec les crues (vallée d'un fleuve, lac) : 1 ;
/// 0 ailleurs (bassins des sources, abords cachés sous le sol), sinon une crue y dressait une
/// nappe d'eau.</param>
public readonly record struct TerrainSample(double Height, double WaterLevel, double River, float Debris = 0, float DebrisDay = 0, float Wetland = 0,
                                            float Floodable = 0)
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
    public static double RadiusMeters => PlanetConstants.RadiusKm * 1000.0;

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
    // ... et jamais plus fin que cette part de la plus courte longueur d'onde du maillage : une arête
    // plus vive que les mailles y dessine des dents de scie (le passage d'un niveau de détail à
    // l'autre est fondu par le morphing)
    // Demi-largeur (en unités de bruit) de la bande, autour d'une crête, où l'érosion s'estompe
    private const double ErosionCrestWidth = 0.12;
    private const double RidgeRoundingPerWavelength = 0.12;

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
    // Cours d'eau creusés dans le relief : rivières dès quelques dizaines de m³/s (les torrents
    // naissent en tête de chaque tronçon de source)
    private const double ValleyMinDischarge = 25.0;
    private const int ValleyMinUpstreamCells = 1;
    private const double MeanderAmplitude = 2500.0, MeanderWavelength = 14000.0;
    // Côtes : hauteur des falaises (m) selon le relief. Sur Terre, les côtes rocheuses à falaises
    // forment la majorité des rivages (~3/4 selon Emery et Kuhn 1982) ; avant, seul ~1/3 des côtes
    // en avait, basses et adoucies sur ~200 m vues de loin : on ne les voyait presque pas
    private const double FlatCliffHeight = 30.0, RuggedCliffHeight = 220.0;
    // Lacs : part des cellules voisines en lac à partir de laquelle un point est dans le lac
    private const double LakeThreshold = 0.45;
    private const double LakeEdgeShare = 0.08;

    private readonly int[] _downstream;
    private readonly float[] _discharge;
    private readonly bool[] _riverCell;
    // Tracé de chaque fleuve d'une cellule à la suivante : courbe lissée (Catmull-Rom) passant
    // par les centres des cellules, ondulée à grande échelle ; null hors des fleuves
    private readonly Double3[][] _riverCurve;
    // Tronçon de tête (aucun fleuve en amont) : sa vallée naît progressivement
    private bool[] _riverHead;
    // Tronçon d'embouchure : la cellule en aval est en mer
    private bool[] _riverMouth;
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
    /// <param name="landforms">Volcans et cratères (étape 13), copies figées ; null : aucun.</param>
    /// <param name="seaLevelM">Niveau moyen de la mer du moment (m ; plus bas pendant une
    /// glaciation) : les deltas et les vasières se calent sur lui.</param>
    public TerrainHeightField(PlanetGrid grid, float[] elevationKm, int seed, float[] runoffMm = null, Landform[] landforms = null,
                              TerrainBench[] benches = null, double seaLevelM = 0)
    {
        SeaLevelM = seaLevelM;
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
        BuildDeltas(elevationKm);
        BuildLandformIndex(landforms);
        BuildBenches(benches);
    }

    // --- Replats des bassins hydrothermaux (étape 14a) ------------------------------------------

    /// <summary>Profondeur de la cuvette sous la terrasse (m).</summary>
    public const double BenchBasinDepthM = 0.6;
    private const double BenchFlat = 2.2, BenchReach = 3.2;

    private TerrainBench[] _benches = Array.Empty<TerrainBench>();
    private double[] _benchLevel = Array.Empty<double>();
    private int[][] _cellBenches;

    /// <summary>Altitude de la terrasse de chaque replat : celle du terrain naturel en son centre.</summary>
    private void BuildBenches(TerrainBench[] benches)
    {
        _cellBenches = new int[Grid.CellCount][];
        if (benches == null || benches.Length == 0)
            return;
        var kept = new System.Collections.Generic.List<TerrainBench>();
        var levels = new System.Collections.Generic.List<double>();
        var lists = new System.Collections.Generic.List<int>[Grid.CellCount];
        int hint = -1;
        foreach (var bench in benches)
        {
            // (terrain lissé à l'échelle du bassin : la terrasse ne se cale pas sur une bosse)
            double level = SampleNatural(bench.Direction, 2 * bench.RadiusM, ref hint).Height;
            if (level < 2)
                continue;   // sous la mer ou au ras de l'eau : pas de bassin
            int index = kept.Count;
            kept.Add(bench);
            levels.Add(level);
            // Rangé dans sa cellule et ses voisines (un bassin peut être à cheval sur une limite)
            (lists[hint] ??= new()).Add(index);
            foreach (int n in Grid.Neighbors[hint])
                (lists[n] ??= new()).Add(index);
        }
        _benches = kept.ToArray();
        _benchLevel = levels.ToArray();
        for (int c = 0; c < lists.Length; c++)
            _cellBenches[c] = lists[c]?.ToArray();
    }

    /// <summary>Altitude du fond du bassin au centre d'un replat proche de la direction, ou NaN.</summary>
    public double BenchFloor(Double3 direction, ref int hint)
    {
        Span<int> cells = stackalloc int[64];
        Gather(direction, ref hint, cells);
        int[] list = _cellBenches?[cells[0]];
        if (list == null)
            return double.NaN;
        foreach (int i in list)
            if (direction.DistanceTo(_benches[i].Direction) * RadiusMeters < _benches[i].Basin)
                return _benchLevel[i] - BenchBasinDepthM;
        return double.NaN;
    }

    /// <summary>Profondeur de l'eau sous le bord d'une cuvette pleine (m).</summary>
    public const double BenchWaterFreeboardM = 0.12;

    /// <summary>
    /// Relief avec les replats, et niveau de l'eau des bassins pleins (−∞ s'il n'y en a pas) : au
    /// niveau du bord moins quelques centimètres dans la cuvette ; juste autour, un niveau caché
    /// sous le sol, pour que la surface de l'eau s'interpole sans nappe entre deux sommets.
    /// </summary>
    private (double height, double water) ApplyBenches(Double3 direction, int cell, double height)
    {
        int[] list = _cellBenches[cell];
        double water = double.NegativeInfinity;
        if (list == null)
            return (height, water);
        bool nearPool = false;
        foreach (int i in list)
        {
            double radius = _benches[i].RadiusM, basin = _benches[i].Basin;
            double d = direction.DistanceTo(_benches[i].Direction) * RadiusMeters;
            if (d >= 1.3 * BenchReach * radius)
                continue;
            // Contour irrégulier (lobes), propre à chaque bassin : pas de disque parfait
            var (ax, ay) = LavaFlow.TangentFrame(_benches[i].Direction);
            Double3 offset = direction - _benches[i].Direction;
            double angle = Math.Atan2(offset.Dot(ay), offset.Dot(ax)), phase = i * 2.399;
            d /= 1 + 0.16 * Math.Sin(3 * angle + phase) + 0.08 * Math.Sin(5 * angle + 2.1 * phase);
            if (d >= BenchReach * radius)
                continue;
            // Terrasse horizontale, cuvette au centre (bord en pente douce sur 15 % du rayon)
            double terrace = _benchLevel[i] - BenchBasinDepthM * (1 - SmoothStep(0.85, 1.0, d / basin));
            height = terrace + (height - terrace) * SmoothStep(BenchFlat, BenchReach, d / radius);
            if (_benches[i].Water)
            {
                if (d < 0.97 * basin)
                    water = Math.Max(water, _benchLevel[i] - BenchWaterFreeboardM);
                else if (d < 1.6 * basin)
                    nearPool = true;
            }
        }
        if (double.IsNegativeInfinity(water) && nearPool)
            water = height - 1.0;
        return (height, water);
    }

    // --- Volcans et cratères (étape 13) -----------------------------------------------------

    // Reliefs ponctuels qui touchent chaque cellule (index des reliefs, null : aucun)
    private Landform[] _landforms = Array.Empty<Landform>();
    private int[][] _cellLandforms;
    private VolcanoDetail[] _volcanoDetails;
    private (Double3 x, Double3 y)[] _landformFrames;

    /// <summary>
    /// Niveau d'eau « pas de mer » d'un point des terres loin des côtes (ni lac ni fleuve) : la
    /// mer ne peut pas y monter, même si le sol est sous son niveau (dépression, cuvette sèche).
    /// </summary>
    public const double NoSeaLevel = -12345.0;

    /// <summary>La cellule ou l'une de ses voisines est-elle en mer (côte, ou mer) ?</summary>
    public bool NearOcean(int cell)
    {
        if (_elevation[cell] <= 0)
            return true;
        foreach (int n in Grid.Neighbors[cell])
            if (_elevation[n] <= 0)
                return true;
        return false;
    }

    /// <summary>Volcans et cratères (pour placer les tunnels de lave).</summary>
    public System.Collections.Generic.IReadOnlyList<Landform> Landforms => _landforms;

    /// <summary>Range chaque relief dans les cellules dont il peut toucher la zone (son rayon
    /// d'influence + le rayon d'une cellule) : un point n'examine que ceux de sa cellule.</summary>
    private void BuildLandformIndex(Landform[] landforms)
    {
        _landforms = landforms ?? Array.Empty<Landform>();
        _cellLandforms = new int[Grid.CellCount][];
        _volcanoDetails = new VolcanoDetail[_landforms.Length];
        _landformFrames = new (Double3, Double3)[_landforms.Length];
        for (int i = 0; i < _landforms.Length; i++)
        {
            _landformFrames[i] = LavaFlow.TangentFrame(Double3.From(_landforms[i].Position).Normalized());
            if (_landforms[i].Kind != LandformKind.ImpactCrater)
                _volcanoDetails[i] = VolcanoDetail.For(_landforms[i]);
        }
        if (_landforms.Length == 0)
            return;
        var lists = new System.Collections.Generic.List<int>[Grid.CellCount];
        double cellRadius = 0.75 * Grid.MeanEdgeAngle;
        for (int i = 0; i < _landforms.Length; i++)
        {
            var landform = _landforms[i];
            double reach = InfluenceRadiusM(landform) / RadiusMeters + cellRadius;
            Double3 center = Double3.From(landform.Position).Normalized();
            int start = Grid.NearestCell(landform.Position, 0);
            // Parcours en largeur depuis la cellule du relief, limité à sa zone d'influence
            var queue = new System.Collections.Generic.Queue<int>();
            var seen = new System.Collections.Generic.HashSet<int> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                (lists[c] ??= new()).Add(i);
                foreach (int n in Grid.Neighbors[c])
                    if (seen.Add(n) && Math.Acos(Math.Clamp(center.Dot(_positions[n]), -1, 1)) < reach)
                        queue.Enqueue(n);
            }
        }
        for (int c = 0; c < lists.Length; c++)
            _cellLandforms[c] = lists[c]?.ToArray();
    }

    /// <summary>Distance (m) au-delà de laquelle un relief ne change plus rien (éjectas : 3 rayons).</summary>
    private static double InfluenceRadiusM(Landform landform) =>
        landform.Kind == LandformKind.ImpactCrater ? 3.0 * landform.RadiusM : landform.RadiusM;

    /// <summary>
    /// Hauteur ajoutée (m) par les volcans et les cratères voisins du point.
    /// <list type="bullet">
    /// <item>Volcan bouclier : dôme convexe (1 − q²)^1,5, caldeira au sommet ;</item>
    /// <item>stratovolcan : cône aux flancs concaves (1 − q)^1,6, cratère au sommet ;</item>
    /// <item>cratère d'impact (Pike 1977) : cuvette jusqu'à la profondeur d, rempart de
    /// ~0,04 D (petits cratères) qui retombe en 1/r³ sur les éjectas ; au-delà de 15 km, fond
    /// plat et piton central.</item>
    /// </list>
    /// </summary>
    private double LandformHeight(Double3 direction, int cell, double minWavelength)
    {
        int[] list = _cellLandforms?[cell];
        if (list == null)
            return 0;
        double total = 0;
        // Cuvettes des cratères qui se chevauchent : elles ne s'additionnent pas (un impact creuse
        // dans les débris et la roche broyée du précédent, pas sous son fond intact). Norme 8 des
        // profondeurs : un cratère seul est inchangé, des cratères empilés vont à peine plus bas
        // que le plus profond (20 bassins géants au même endroit, additionnés, creusaient à
        // −19 km) ; sans cassure, les pentes restent continues là où deux cuvettes se croisent
        double bowls = 0;
        foreach (int i in list)
        {
            var landform = _landforms[i];
            Double3 center = Double3.From(landform.Position).Normalized();
            double r = Math.Acos(Math.Clamp(direction.Dot(center), -1, 1)) * RadiusMeters;
            double radius = landform.RadiusM;
            if (r >= InfluenceRadiusM(landform))
                continue;
            double q = r / radius;
            double scale = landform.Freshness * landform.Growth;
            switch (landform.Kind)
            {
                case LandformKind.ShieldVolcano:
                {
                    // Caldeira sommitale : ~5 % du diamètre, profonde de ~4 % de la hauteur
                    // (Mauna Loa : Mokuʻāweoweo, 3 × 5 km, 180 m de profondeur)
                    // (caldeira, coulées, rifts, cônes : VolcanoDetail)
                    double h = Math.Pow(1 - q * q, 1.5);
                    total += scale * landform.PeakHeightM * h + VolcanoDetailAt(i, direction, center, q, minWavelength);
                    break;
                }
                case LandformKind.Stratovolcano:
                {
                    // Cratère sommital : quelques centaines de mètres de large, ~150 m de profondeur
                    // (Etna, Fuji : 500 à 800 m de diamètre)
                    double h = Math.Pow(1 - q, 1.6);
                    total += scale * landform.PeakHeightM * h + VolcanoDetailAt(i, direction, center, q, minWavelength);
                    break;
                }
                default:
                {
                    double depth = landform.PeakHeightM;
                    double diameterKm = radius / 500.0;
                    double rim = 1000.0 * (diameterKm < 15 ? 0.036 * Math.Pow(diameterKm, 1.014) : 0.236 * Math.Pow(diameterKm, 0.399));
                    double h;
                    if (q < 1)
                    {
                        // Fond : parabolique (simple) ou plat (complexe, puissance 4), piton central
                        double power = diameterKm < 15 ? 2 : 4;
                        h = -depth + (depth + rim) * Math.Pow(q, power);
                        if (diameterKm >= 15)
                            h += 0.3 * depth * Math.Exp(-Math.Pow(q / 0.12, 2));
                    }
                    else
                    {
                        h = rim * Math.Pow(q, -3) * (1 - SmoothStep(2.2, 3.0, q));
                    }
                    h *= landform.Freshness;
                    if (h >= 0)
                        total += h;
                    else
                    {
                        // (en km, pour que la puissance 8 reste loin des limites des doubles)
                        double x2 = h * h * 1e-6, x4 = x2 * x2;
                        bowls += x4 * x4;
                    }
                    break;
                }
            }
        }
        return bowls > 0 ? total - 1000 * Math.Pow(bowls, 0.125) : total;
    }

    /// <summary>Détails du volcan i (rifts, cônes, coulées, ravines ; voir <see cref="VolcanoDetail"/>).</summary>
    private double VolcanoDetailAt(int i, Double3 direction, Double3 center, double q, double minWavelength)
    {
        var landform = _landforms[i];
        var (x, y) = _landformFrames[i];
        Double3 offset = (direction - center) * RadiusMeters;
        return _volcanoDetails[i].Height(landform, offset.Dot(x), offset.Dot(y), q, landform.Freshness * landform.Growth, minWavelength);
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
        _riverHead = new bool[count];
        _riverMouth = new bool[count];
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
            _riverHead[c] = up < 0;
            _riverMouth[c] = _elevation[down] <= 0;
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
        int nearest = Grid.NearestCell(p.ToVector3(), hint);
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
        TerrainSample sample = SampleNatural(direction, minWavelength, ref hint);
        if (_benches.Length > 0)
        {
            var (benchHeight, pool) = ApplyBenches(direction, hint, sample.Height);
            sample = sample with { Height = benchHeight, WaterLevel = Math.Max(sample.WaterLevel, pool) };
        }
        var edits = Edits;
        if (edits == null || edits.Count == 0)
            return sample;
        double offset = edits.Offset(direction, out float debris, out float day);
        return sample with { Height = sample.Height + offset, Debris = debris, DebrisDay = day };
    }

    /// <summary>
    /// Croûte modulaire (étape 16a) : calques de modification ajoutés au relief (glissements,
    /// éboulements, lave figée). Null : relief naturel seul. Partagé par les reliefs successifs
    /// d'une même planète (le relief de base ne change plus une fois la géologie figée).
    /// </summary>
    public CrustEdits Edits { get; set; }

    // --- Dunes ----------------------------------------------------------------------------------

    /// <summary>
    /// Part de sable des déserts de chaque cellule (0 à 1, d'après les biomes du climat simulé ;
    /// null : pas de dunes). Remplacée d'un bloc (lue depuis les threads du terrain).
    /// </summary>
    public float[] DuneShare { get; set; }

    /// <summary>Espacement (m) des grandes dunes et leur hauteur (m) : 300 à 600 m, 20 à 40 m dans les ergs.</summary>
    private const double DuneWavelength = 1100, DuneHeightM = 34;

    /// <summary>
    /// Dunes des déserts de sable : crêtes vives et creux larges (bruit en crêtes), allongées
    /// du nord au sud (perpendiculaires aux alizés, qui soufflent d'est en ouest), avec des dunes
    /// plus petites par-dessus ; isolées en croissants là où le sable manque ; en mers de sable
    /// (ergs) séparées de plaines caillouteuses (regs) ; seulement sur le plat.
    /// </summary>
    private double Dunes(Double3 position, Double3 direction, ReadOnlySpan<int> cells, double baseHeight, double baseSlope, double minWavelength)
    {
        var shares = DuneShare;
        if (shares == null || baseHeight < 5)
            return 0;
        double share = BlendValue(direction, cells, shares);
        if (share <= 0.01)
            return 0;
        double N(double x, double y, double z, int salt) => GradientNoise.Sample(x, y, z, Seed + salt);
        var (plain, hill, _, _) = ReliefMix(baseHeight, baseSlope);
        double erg = SmoothStep(-0.15, 0.3, N(position.X / 60000, position.Y / 60000, position.Z / 60000, 501));
        double where = share * Math.Min(1, plain + 0.5 * hill) * (1 - SmoothStep(0.01, 0.03, baseSlope)) * erg;
        if (where <= 0.01)
            return 0;
        double total = 0;
        for (int octave = 0; octave < 2; octave++)
        {
            double wavelength = octave == 0 ? DuneWavelength : DuneWavelength / 4;
            double fine = 1 - SmoothStep(0.2 * wavelength, 0.45 * wavelength, minWavelength);
            if (fine <= 0)
                continue;
            int salt = 502 + 10 * octave;
            // (étiré le long de l'axe des pôles : crêtes nord-sud)
            double ridge = 1 - Math.Abs(N(position.X / wavelength, position.Y / (4 * wavelength), position.Z / wavelength, salt));
            double crest = Math.Pow(Math.Clamp(ridge, 0, 1), 3);
            double supply = SmoothStep(-0.45, 0.45, N(position.X / (2.5 * wavelength), position.Y / (2.5 * wavelength), position.Z / (2.5 * wavelength), salt + 1));
            total += (octave == 0 ? 1.0 : 0.2) * crest * supply * fine;
        }
        return DuneHeightM * where * total;
    }

    /// <summary>Valeur par cellule mélangée entre les cellules proches (mêmes poids que l'altitude).</summary>
    private double BlendValue(Double3 p, ReadOnlySpan<int> cells, float[] values)
    {
        double weightSum = 0, sum = 0;
        foreach (int cell in cells)
        {
            double q2 = (p - _positions[cell]).LengthSquared() / _supportSquared[cell];
            if (q2 >= 1)
                continue;
            double q = Math.Sqrt(q2);
            double w = Math.Pow(1 - q, 4) * (4 * q + 1);
            weightSum += w;
            sum += w * values[cell];
        }
        return weightSum > 0 ? sum / weightSum : values[cells[0]];
    }

    /// <summary>Relief sans la croûte modulaire (le relief « naturel », replats compris).</summary>
    public double BaseTerrainHeight(Double3 direction, double minWavelength, ref int hint)
    {
        TerrainSample sample = SampleNatural(direction, minWavelength, ref hint);
        return _benches.Length > 0 ? ApplyBenches(direction, hint, sample.Height).height : sample.Height;
    }

    /// <summary>Relief détaillé sans les replats des bassins.</summary>
    private TerrainSample SampleNatural(Double3 direction, double minWavelength, ref int hint)
    {
        Span<int> cells = stackalloc int[64];
        int count = Gather(direction, ref hint, cells);
        ReadOnlySpan<int> near = cells[..count];
        double baseHeight = Blend(direction, near, out Double3 slope, out double lakeShare, out double lakeLevel);
        double baseSlope = slope.Length();
        Double3 position = direction * RadiusMeters;

        // Vallées des fleuves (calculées d'abord : le fond des vallées est lissé)
        var valley = Valley(direction, position, near, baseHeight, minWavelength);
        double river = valley.River;

        double coherence = SlopeCoherence(position / RadiusMeters, near);
        double folds = Folds(position, baseHeight, baseSlope, coherence, minWavelength, out double foldShare);
        double noiseScale = 1 - FoldNoiseReduction * foldShare;
        double detail = Detail(position, baseHeight, baseSlope, minWavelength) * noiseScale;
        // Fond des vallées aplani : seulement les détails plus fins que sa largeur. (Avant, tout le
        // relief fin y était raboté : à la source d'un torrent, le fond ne fait que 2 m de large,
        // et une bosse de 900 m de la montagne y devenait une fente de 800 m de profondeur ; vue
        // de loin, une aiguille ou un puits. Et combler les creux dressait ailleurs le fond en
        // digue au-dessus des alentours)
        double smoothest = Math.Max(minWavelength, 4 * valley.FloorHalf);
        if (valley.Floor > 0 && smoothest > minWavelength)
        {
            double broadDetail = Detail(position, baseHeight, baseSlope, smoothest) * noiseScale;
            double broadFolds = Folds(position, baseHeight, baseSlope, coherence, smoothest, out _);
            detail = broadDetail + (detail - broadDetail) * (1 - 0.85 * valley.Floor);
            folds = broadFolds + (folds - broadFolds) * (1 - valley.Floor);
        }
        // Volcans et cratères avant les vallées : les fleuves creusent leurs flancs. (Ajoutés
        // après, ils soulevaient un lit creusé sur la plaine d'en dessous, parfois annulé par la
        // règle du fond au-dessus de la mer : un lit à sec, des centaines de mètres sous l'eau)
        double height = baseHeight + detail + folds + LandformHeight(direction, near[0], minWavelength)
                        + Dunes(position, direction, near, baseHeight, baseSlope, minWavelength) * (1 - valley.Floor);
        // Le fond des vallées reste au-dessus de la mer tant que le sol l'est : le fleuve coule
        // jusqu'à la côte au lieu de transformer toute la plaine en bras de mer
        // (sans cassure : au-dessous de 1 m, on ne creuse plus du tout ; avant, le fond passait
        // d'un coup de 1 m à toute la profondeur de la vallée, d'où une falaise le long du rivage)
        // (le lit, lui, se creuse toujours sous le fond, même au ras de la mer : sinon le fleuve
        // s'asséchait dans ses derniers mètres avant la côte). Le creusement du lit sert aussi à
        // poser l'eau sur le lit final, après toutes les retouches du sol qui suivent
        double uncarved = height;
        double bedCut = valley.BedCut;
        height = Math.Max(uncarved - valley.ValleyCarve, Math.Min(uncarved, 1.0)) - bedCut;
        // Embouchure : le fleuve creuse jusqu'à son niveau de base, la mer ; sa vallée descend
        // jusqu'au rivage au lieu de rester perchée sur le plateau (le fleuve tombait d'une
        // falaise de 200 m, en mur d'eau plat)
        // (le fond et le lit descendent ensemble : le lit garde sa profondeur jusqu'à la mer)
        if (valley.Mouth > 0 && height + bedCut > 1.0)
            height -= (height + bedCut - 1.0) * valley.Mouth;
        // Deltas et vasières : plaine de vase au ras de la mer, sans falaise
        var wetShare = WetlandShare(direction, position, near[0], baseHeight, baseSlope);
        var (wetHeight, wetland) = ApplyWetland(position, height, wetShare, minWavelength);
        bedCut *= 1 - wetland;
        double cliffHeight = Cliffs(position, height, baseHeight, baseSlope, minWavelength);
        // (ni dans les deltas et les vasières, ni au débouché d'une vallée : sur son fond plat, à
        // 1 m, la falaise dressait une plate-forme de 30 m, d'où le fleuve tombait en cascade)
        double noCliff = Math.Min(1, Math.Max(2 * Math.Max(wetShare.delta, wetShare.coastal), Math.Max(valley.Floor, valley.Mouth)));
        height = wetHeight + (cliffHeight - height) * (1 - noCliff);

        // Lacs : au cœur de leur zone, l'eau est au niveau du lac ; vers le bord de la zone, ce
        // niveau s'abaisse progressivement jusqu'à passer sous le sol. La rive tombe là où le
        // terrain croise ce niveau : elle est toujours continue, sans toucher au relief. (Avant :
        // le lac s'arrêtait net sur la limite entre deux cellules, ou, en remontant le sol au
        // bord, formait un mur.)
        double water = 0;
        float floodable = 0;
        if (lakeShare > LakeEdgeShare && lakeLevel > 0)
        {
            floodable = 1;
            double full = SmoothStep(LakeEdgeShare, LakeThreshold, lakeShare);
            double dry = height - 20.0;   // niveau sûrement sous le sol
            // Même sous le sol, on garde ce niveau (et non 0) : entre deux sommets, l'eau
            // s'interpole alors en douceur jusqu'à la rive
            water = Math.Max(0.0, dry + (lakeLevel - dry) * full);
        }
        // Fleuves : de la vraie eau (la surface de l'eau, comme pour la mer et les lacs), pas une
        // couleur peinte. Le lit est creusé sous les berges selon le débit ; l'eau le remplit
        // jusqu'à 40 cm sous elles (surface plane d'une berge à l'autre). Hors du lit, son niveau
        // reste sous le sol (caché), pour que l'eau s'interpole sans nappe entre deux sommets
        if (valley.Depth > 0 && height + Math.Max(0, bedCut) > 1.0)
        {
            // Berges : le sol final (volcans, cratères, embouchure, deltas compris) plus le
            // creusement du lit. (Avant, l'eau se calculait sur le sol d'avant ces retouches : sur
            // le flanc d'un volcan, elle passait des centaines de mètres sous le lit, et le
            // fleuve disparaissait par tronçons)
            double bank = height + Math.Max(0, bedCut);
            // Plaine d'inondation : le niveau du fleuve (fond de vallée moins 40 cm), plan, caché
            // sous le sol jusque sous les versants ; une crue le fait monter (shader de l'eau) et
            // il apparaît, à plat, sur le fond de la vallée, jusqu'où il touche les versants.
            // (Avant, sous les versants, un niveau qui suivait le sol 3 m plus bas : une crue de
            // 3 m le faisait affleurer partout, en nappe drapée sur les pentes)
            double floodplain = Math.Min(height - 0.05, bank - Math.Max(0, valley.FloorDepth - valley.ValleyCarve) - 0.4);
            double riverWater = valley.Bed > 0.01 ? bank - 0.4 : floodplain;
            water = Math.Max(water, Math.Max(riverWater, floodplain));
            floodable = 1;
        }
        return new TerrainSample(height, water, river, Wetland: (float)wetland, Floodable: floodable);
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
            double soft = Math.Min(0.3, Math.Max(RidgeRounding, RidgeRoundingPerWavelength * lowest) / wavelength);
            double rounded = Math.Sqrt(x * x + soft * soft);
            double linear = 1 - (rounded - soft) * 1.6;
            double ridge = linear * linear;
            double weight = ridgeWeight * erosionWeight * fade;
            double amplitude = MountainSpectrum[k] * weight;
            ridged += (ridge - RidgedMean) * amplitude;
            ridgeWeight = Math.Clamp(ridge * 1.4, 0.2, 1.0);

            // Érosion : pente de cette octave (dérivée du bruit tournée vers le repère de la planète)
            // (pente adoucie largement autour de la crête : avec l'arrondi étroit de l'arête, la
            // pente ne s'annulait que sur quelques mètres ; l'érosion y cessait d'un coup et les
            // octaves fines, à pleine amplitude sur ce seul ruban, dressaient une lame de quelques
            // dizaines de mètres tout le long des grandes crêtes)
            double dr = -2 * linear * 1.6 * (x / Math.Sqrt(x * x + ErosionCrestWidth * ErosionCrestWidth)) * amplitude * inverse;
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

    /// <summary>Vallée et lit du cours d'eau le plus proche (voir <see cref="Valley"/>).</summary>
    /// <param name="Depth">Creusement total : vallée plus lit.</param>
    /// <param name="BedCut">Creusement du lit sous le fond de la vallée (m).</param>
    private readonly record struct ValleyShape(double Depth, double Floor, double River, double Bed, double ValleyCarve, double FloorDepth, double FloorHalf = 0,
                                               double Mouth = 0, double BedCut = 0);

    /// <summary>
    /// Vallée et lit du cours d'eau le plus proche, selon son débit Q (m³/s) et le relief :
    /// <list type="bullet">
    /// <item><b>Lit</b> (géométrie hydraulique, Leopold et Maddock 1953) : largeur ≈ 3,5·Q^0,5,
    /// profondeur ≈ 0,25·Q^0,4 : un grand fleuve de 1 000 m³/s fait ~110 m de large et 4 m de
    /// fond, une rivière de 10 m³/s ~11 m, un torrent de 1 m³/s ~3,5 m ; près de la source, le
    /// débit croît depuis presque rien (torrents de tête).</item>
    /// <item><b>Vallée</b> : en plaine, large et peu profonde, avec une plaine d'inondation d'une
    /// dizaine de fois la largeur du lit et des versants doux ; en montagne, étroite et profonde, en
    /// V (gorges), le fond à peine plus large que le lit.</item>
    /// <item><b>Méandres</b> : longueur d'onde ~12 fois la largeur du lit, amplitude ~3 fois.</item>
    /// </list>
    /// </summary>
    private ValleyShape Valley(Double3 direction, Double3 position, ReadOnlySpan<int> cells, double baseHeight, double minWavelength)
    {
        bool any = false;
        foreach (int cell in cells)
            if (_riverCell[cell]) { any = true; break; }
        if (!any || baseHeight <= -50)
            return default;

        // Relief : 0 en plaine, 1 en montagne (vallées en V, gorges)
        double mountain = SmoothStep(300, 1800, baseHeight);
        var best = default(ValleyShape);
        foreach (int cell in cells)
        {
            if (!_riverCell[cell])
                continue;
            Double3[] curve = _riverCurve[cell];
            Double3 middle = curve[RiverCurveSegments / 2];
            double reach = (curve[0] - middle).Length() + (curve[RiverCurveSegments] - middle).Length();
            double qCell = _discharge[cell];
            double widthMax = ValleyHalfWidth(qCell, mountain, out _, out _, out _, out _);
            if ((direction - middle).Length() * RadiusMeters > reach * RadiusMeters + widthMax + MeanderAmplitude)
                continue;
            // Méandres à l'échelle du lit : on déplace le point plutôt que le tracé
            double channelFull = 3.5 * Math.Sqrt(qCell);
            double wavelength = Math.Clamp(12 * channelFull, 300, MeanderWavelength);
            double amplitude = Math.Clamp(3 * channelFull, 25, MeanderAmplitude) * (1 - 0.7 * mountain);
            // Test rapide sans méandres (le point ne bouge que de √3 fois l'amplitude au plus) : loin du
            // tracé, pas de bruit à calculer (avant, trois bruits par cours d'eau et par point)
            if (CurveDistance(curve, direction, out _) - 1.8 * amplitude >= widthMax)
                continue;
            double mx = position.X / wavelength, my = position.Y / wavelength, mz = position.Z / wavelength;
            var meander = new Double3(
                GradientNoise.Sample(mx, my, mz, Seed + 301),
                GradientNoise.Sample(mx + 31.7, my - 12.9, mz + 5.3, Seed + 302),
                GradientNoise.Sample(mx - 7.1, my + 44.2, mz - 23.8, Seed + 303));
            meander -= direction * meander.Dot(direction);
            Double3 p = (direction + meander * (amplitude / RadiusMeters)).Normalized();
            double distance = CurveDistance(curve, p, out double along);
            // Tronçon de tête : le débit croît depuis la source (torrent, puis rivière)
            double q = _riverHead[cell] ? qCell * (0.01 + 0.99 * SmoothStep(0.0, 1.0, along)) : qCell;
            double width = ValleyHalfWidth(q, mountain, out double channelHalf, out double bankDepth, out double floorHalf, out double depth);
            if (distance >= width)
                continue;
            // À la source, la vallée naît peu à peu
            double birth = _riverHead[cell] ? SmoothStep(0.0, 0.25, along) : 1;
            double valleyCarve = depth * (1 - SmoothStep(floorHalf, width, distance)) * birth;
            // Lit : seulement si le maillage est assez fin pour lui (sinon, de loin, un trait d'eau)
            bool drawn = 2 * channelHalf > 0.5 * minWavelength;
            double bedProfile = drawn ? BedProfile(position, distance, channelHalf) : 1 - SmoothStep(channelHalf * 0.7, channelHalf * 1.05, distance);
            double bedCut = drawn ? (bankDepth + 0.4) * bedProfile : 0;
            double river = drawn
                ? bedProfile
                // De loin : part du trait d'eau dans la maille (fleuves visibles jusqu'à l'horizon,
                // d'autant plus marqués qu'ils sont larges)
                : Math.Min(1, 2 * channelHalf / Math.Max(minWavelength, 1)) * (1 - SmoothStep(0.25 * minWavelength, 0.6 * minWavelength, distance));
            // Plusieurs tracés proches (le même fleuve vu par deux cellules voisines, dont les
            // tracés se recouvrent ; un confluent) : chaque grandeur garde son maximum, sans
            // cassure ; le lit le plus creusé est creusé sous la vallée la plus creusée (le
            // creusement total est fait à la fin). (Avant, on prenait tout du tracé qui creusait
            // le plus : là où deux tracés creusaient presque autant, le choix changeait d'un point
            // à l'autre, et le fond de vallée avec lui : des aiguilles de centaines de mètres le
            // long des fleuves ; puis, en prenant le maximum de « vallée + lit » d'un côté et de
            // la vallée de l'autre, le lit d'un tracé sortait de l'eau de l'autre : le fleuve
            // s'arrêtait et reprenait plus loin)
            double floor = 1 - SmoothStep(floorHalf * 0.5, floorHalf * 1.5, distance);
            best = new ValleyShape(0,
                                   Math.Max(best.Floor, floor),
                                   Math.Max(best.River, river * birth),
                                   Math.Max(best.Bed, drawn ? bedProfile * birth : 0),
                                   Math.Max(best.ValleyCarve, valleyCarve),
                                   Math.Max(best.FloorDepth, depth * birth),
                                   // (largeur du fond pondérée par sa présence : sans cassure)
                                   Math.Max(best.FloorHalf, floorHalf * floor),
                                   // Vers l'embouchure, la vallée descend jusqu'à la mer (fond et versants)
                                   Math.Max(best.Mouth, _riverMouth[cell] ? SmoothStep(0.4, 1.0, along) * (1 - SmoothStep(floorHalf, width, distance)) : 0),
                                   Math.Max(best.BedCut, bedCut * birth));
        }
        return best with { Depth = best.ValleyCarve + best.BedCut };
    }

    /// <summary>
    /// Profil du lit (1 dans le chenal, 0 hors du lit) à une distance du tracé, berges
    /// irrégulières : la largeur varie le long du fleuve (±30 %, sur ~6 largeurs : mouilles et
    /// seuils), les bords sont découpés (deux échelles), et les grands fleuves ont des bancs de
    /// sable et des îles. (Avant : deux berges parallèles, parfaitement lisses.)
    /// </summary>
    private double BedProfile(Double3 position, double distance, double channelHalf)
    {
        if (distance > channelHalf * 1.7)
            return 0;
        double full = 2 * channelHalf;
        double N(double wavelength, int salt) => GradientNoise.Sample(position.X / wavelength, position.Y / wavelength, position.Z / wavelength, Seed + salt);
        double half = channelHalf * (1 + 0.3 * N(6 * full, 321));
        double edge = channelHalf * (0.18 * N(1.1 * full, 322) + 0.06 * N(Math.Max(12, 0.25 * full), 323));
        double profile = 1 - SmoothStep(half * 0.7, half * 1.05, distance + edge);
        // Bancs et îles (fleuves de plus de ~60 m de large) : le fond y remonte au-dessus de l'eau
        if (channelHalf > 30 && profile > 0)
        {
            double bar = SmoothStep(0.25, 0.5, N(2.2 * full, 324)) * (1 - SmoothStep(0.35 * half, 0.8 * half, distance)) * SmoothStep(30, 120, channelHalf);
            profile *= 1 - bar;
        }
        return profile;
    }

    /// <summary>Tracé courbe du cours d'eau d'une cellule (null si elle n'en a pas) : pour les tests.</summary>
    internal System.Collections.Generic.IReadOnlyList<Double3> RiverCurve(int cell) => _riverCell[cell] ? _riverCurve[cell] : null;

    /// <summary>Distance (m) d'un point au tracé courbe d'un cours d'eau, et position le long du tracé (0 à 1).</summary>
    private static double CurveDistance(Double3[] curve, Double3 p, out double along)
    {
        double best = double.MaxValue;
        along = 0;
        for (int k = 0; k < RiverCurveSegments; k++)
        {
            Double3 a = curve[k], ab = curve[k + 1] - a;
            double t = Math.Clamp((p - a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-30), 0, 1);
            double d = (a + ab * t).Normalized().DistanceTo(p) * RadiusMeters;
            if (d < best)
            {
                best = d;
                along = (k + t) / RiverCurveSegments;
            }
        }
        return best;
    }

    /// <summary>
    /// Dimensions d'une vallée de débit <paramref name="q"/> (m³/s) : demi-largeur du lit,
    /// profondeur du lit, demi-largeur du fond plat, profondeur de la vallée ; renvoie la
    /// demi-largeur totale (m).
    /// </summary>
    private static double ValleyHalfWidth(double q, double mountain, out double channelHalf, out double bankDepth, out double floorHalf, out double depth)
    {
        q = Math.Max(q, 0.05);
        channelHalf = Math.Clamp(1.75 * Math.Sqrt(q), 1, 600);
        bankDepth = Math.Clamp(0.25 * Math.Pow(q, 0.4), 0.3, 12);
        floorHalf = channelHalf * (1.3 + 8.7 * (1 - mountain));
        depth = 3 + 0.4 * Math.Sqrt(q) + (25 + 3 * Math.Sqrt(q)) * mountain;
        double sideSlope = 0.06 + 0.5 * mountain;
        return floorHalf + depth / sideSlope;
    }

    // --- Deltas et vasières (étape 16) ------------------------------------------------------------

    /// <summary>Niveau moyen de la mer pour lequel les deltas et vasières sont bâtis (m).</summary>
    public double SeaLevelM { get; }

    /// <summary>Débit minimal (m³/s) d'un fleuve pour bâtir un delta à son embouchure.</summary>
    public const double DeltaMinDischarge = 300;
    private readonly record struct Delta(Double3 Direction, double RadiusM, double Discharge);
    private Delta[] _deltas = Array.Empty<Delta>();
    private int[][] _cellDeltas;

    /// <summary>
    /// Deltas : à l'embouchure de chaque fleuve assez puissant, le fleuve dépose ses sédiments en
    /// une plaine basse qui avance sur la mer. Surface d'après le débit, au premier ordre (le
    /// Rhône, 1 700 m³/s, ~1 700 km² ; le Mississippi, 18 000 m³/s, ~30 000 km² ; Syvitski et
    /// Saito 2007) : ~1 km² par m³/s, en demi-disque, soit un rayon √(2Q/π) km, de 4 à 60 km.
    /// </summary>
    private void BuildDeltas(float[] elevationKm)
    {
        int count = Grid.CellCount;
        _cellDeltas = new int[count][];
        if (Hydrology == null)
            return;
        var deltas = new System.Collections.Generic.List<Delta>();
        for (int c = 0; c < count; c++)
        {
            if (elevationKm[c] <= 0 || _lake[c] || _discharge[c] < DeltaMinDischarge)
                continue;
            int down = _downstream[c];
            // Embouchure : la cellule aval est en mer (ou il n'y en a pas, la mer est à côté)
            int sea = down >= 0 && elevationKm[down] <= 0 ? down : -1;
            if (down < 0 || sea >= 0)
            {
                if (sea < 0)
                    foreach (int n in Grid.Neighbors[c])
                        if (elevationKm[n] <= 0 && (sea < 0 || elevationKm[n] > elevationKm[sea]))
                            sea = n;
                if (sea < 0)
                    continue;
                // Apex sur la côte : là où le relief passe sous la mer, entre l'embouchure et la mer
                double lo = 0, hi = 1;
                int hint = c;
                for (int it = 0; it < 24; it++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (BaseHeight((_positions[c] * (1 - mid) + _positions[sea] * mid).Normalized(), ref hint) > SeaLevelM) lo = mid; else hi = mid;
                }
                Double3 apex = (_positions[c] * (1 - lo) + _positions[sea] * lo).Normalized();
                double radius = Math.Clamp(Math.Sqrt(2 * _discharge[c] / Math.PI), 4, 60) * 1000;
                deltas.Add(new Delta(apex, radius, _discharge[c]));
            }
        }
        _deltas = deltas.ToArray();
        var lists = new System.Collections.Generic.List<int>[count];
        double spacing = Grid.MeanEdgeAngle * RadiusMeters;
        for (int k = 0; k < _deltas.Length; k++)
            for (int c = 0; c < count; c++)
                if (_positions[c].DistanceTo(_deltas[k].Direction) * RadiusMeters < _deltas[k].RadiusM + 1.6 * spacing)
                    (lists[c] ??= new()).Add(k);
        for (int c = 0; c < count; c++)
            _cellDeltas[c] = lists[c]?.ToArray();
    }

    /// <summary>Nombre de deltas de ce relief.</summary>
    public int DeltaCount => _deltas.Length;

    /// <summary>Deltas : sommet sur la côte (direction), rayon (m), débit du fleuve (m³/s).</summary>
    public System.Collections.Generic.IEnumerable<(Double3 apex, double radiusM, double discharge)> Deltas
    {
        get
        {
            foreach (var d in _deltas)
                yield return (d.Direction, d.RadiusM, d.Discharge);
        }
    }

    /// <summary>Débit (m³/s) du fleuve dont le delta touche la cellule, ou 0.</summary>
    public double DeltaDischarge(int cell)
    {
        double best = 0;
        if (_cellDeltas?[cell] is { } list)
            foreach (int k in list)
                best = Math.Max(best, _deltas[k].Discharge);
        return best;
    }

    /// <summary>
    /// Part du point dans un delta ou une vasière côtière (0 à 1). Vasières : sur ~35 % des
    /// côtes basses et plates (mers peu profondes, côtes à marée), là où il n'y a ni falaise ni
    /// relief : la mer y dépose ses vases fines.
    /// </summary>
    private (double delta, double coastal) WetlandShare(Double3 direction, Double3 position, int cell, double baseHeight, double baseSlope)
    {
        double share = 0, coastal = 0;
        if (_cellDeltas?[cell] is { } list)
        {
            double edge = double.NaN;
            foreach (int k in list)
            {
                double d = direction.DistanceTo(_deltas[k].Direction) * RadiusMeters / _deltas[k].RadiusM;
                if (d >= 1.25)
                    continue;
                // (bord du delta un peu irrégulier ; bruit calculé une seule fois)
                if (double.IsNaN(edge))
                    edge = 1 + 0.2 * GradientNoise.Sample(position.X / 9000.0, position.Y / 9000.0, position.Z / 9000.0, Seed + 701);
                share = Math.Max(share, 1 - SmoothStep(0.55, 1.0, d * edge));
            }
        }
        double b = baseHeight - SeaLevelM;
        if (b > -25 && b < 40 && baseSlope < 0.003)
        {
            double flat = 1 - SmoothStep(0.0008, 0.003, baseSlope);
            double low = SmoothStep(-25, -5, b) * (1 - SmoothStep(8, 40, b));
            double where = SmoothStep(0.25, 0.45, GradientNoise.Sample(position.X / 60000.0, position.Y / 60000.0, position.Z / 60000.0, Seed + 702));
            coastal = flat * low * where;
        }
        return (share, coastal);
    }

    /// <summary>
    /// Plaine de vase d'un delta ou d'une vasière : le relief s'aplatit au ras de la mer (~1 à
    /// 2,3 m, juste au-dessus des pleines mers ordinaires), découpé de chenaux ramifiés et de mares qui descendent sous le niveau de la mer
    /// (la mer y entre : vraie eau, avec la marée) ; la plaine avance sur les hauts-fonds. Rend la
    /// hauteur et la part de « vasière » du point (pour la couleur de la boue).
    /// </summary>
    private (double height, double wetland) ApplyWetland(Double3 position, double height, (double delta, double coastal) share, double minWavelength)
    {
        // Un delta bâtit sa propre plaine : il comble la mer devant l'embouchure (jusqu'à ~500 m
        // de fond : son front avance sur le talus) et nivelle les terres basses (jusqu'à ~200 m) ; une vasière côtière ne touche que le ras de l'eau
        double above = height - SeaLevelM;
        double weight = Math.Max(share.delta * (1 - SmoothStep(80, 200, above)) * SmoothStep(-600, -300, above),
                                 share.coastal * (1 - SmoothStep(10, 60, above)) * SmoothStep(-40, -6, above));
        if (weight < 0.001)
            return (height, 0);
        double Fine(double scale) => 1 - SmoothStep(0.5 * scale, scale, minWavelength);
        double N(double scale, int salt) => GradientNoise.Sample(position.X / scale, position.Y / scale, position.Z / scale, Seed + salt);
        // Plaine juste au-dessus des pleines mers ordinaires (~+1,2 m pour un marnage de vives-eaux
        // de ~2,3 m) : émergée la plupart du temps, ses creux inondés aux grandes marées
        double plain = 1.6 + 0.5 * N(900, 711) + 0.25 * N(250, 712) * Fine(250);
        // Chenaux : lignes où un bruit déformé s'annule (réseau qui se ramifie et serpente)
        double warp = 0.35 * N(5000, 713);
        double line = Math.Abs(GradientNoise.Sample(position.X / 2200.0 + warp, position.Y / 2200.0, position.Z / 2200.0 - warp, Seed + 714));
        double channel = (1 - SmoothStep(0.02, 0.07, line)) * Fine(150);
        // Petits chenaux de marée (quelques mètres à quelques dizaines de mètres, tous les ~300 m)
        // qui se ramifient dans la vase, et mares nombreuses
        double creekLine = Math.Abs(GradientNoise.Sample(position.X / 380.0 + 3 * warp, position.Y / 380.0, position.Z / 380.0 - 3 * warp, Seed + 716));
        double creek = (1 - SmoothStep(0.03, 0.08, creekLine)) * Fine(25) * SmoothStep(-0.3, 0.2, N(1500, 717));
        double pond = Math.Max(SmoothStep(0.32, 0.42, N(450, 715)) * Fine(200), 0.7 * SmoothStep(0.45, 0.55, N(120, 718)) * Fine(50));
        // (grands chenaux toujours en eau, petits chenaux vidés à marée basse, mares)
        double target = SeaLevelM + plain - 4.0 * channel - 2.5 * creek - 2.2 * pond;
        return (height + (target - height) * weight, weight);
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
        // Côtes rocheuses sur ~60 % du rivage, plages et côtes basses ailleurs ; falaises plus
        // hautes là où le relief est accidenté
        double rocky = SmoothStep(-0.2, 0.2, GradientNoise.Sample(position.X / 25000.0, position.Y / 25000.0, position.Z / 25000.0, Seed + 401));
        double cliff = rocky * (FlatCliffHeight + (RuggedCliffHeight - FlatCliffHeight) * Math.Min(1, hill + mountain + 2 * baseSlope));
        // Seulement près du rivage : là où le relief de grande échelle est encore bas par rapport
        // à la falaise (sinon toutes les plaines côtières se retrouvaient surélevées d'autant)
        cliff *= 1 - SmoothStep(0.5 * cliff, 2.0 * cliff + 1, baseHeight);
        // Raideur de la falaise limitée par la finesse du maillage (deux fois plus raide qu'avant)
        double rise = Math.Max(4.0, 0.5 * minWavelength);
        return height + cliff * (1 - Math.Exp(-height / rise));
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }
}
