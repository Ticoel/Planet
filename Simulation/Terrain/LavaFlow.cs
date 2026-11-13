using System;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Coulée de lave simulée comme un fluide sur le relief, autour d'une bouche éruptive : un
/// <b>automate cellulaire</b> sur une grille carrée (256 × 256 cellules de 40 m, soit ~10 km de
/// côté) posée sur le plan tangent à la planète, comme les modèles opérationnels de prévision
/// des coulées de l'Etna (SCIARA, Crisci et al. 2004 ; MAGFLOW, Vicari et al. 2007).
/// <list type="bullet">
/// <item><b>Écoulement</b> : chaque cellule compare la surface de sa lave (sol + épaisseur) à
/// celles de ses 8 voisines et leur cède de la lave en proportion des dénivelés, vers le bas
/// seulement : la lave suit les vallées, contourne les reliefs, remplit les creux puis déborde.</item>
/// <item><b>Seuil d'écoulement</b> : la lave est un fluide de Bingham ; elle ne coule que si son
/// épaisseur dépasse une épaisseur critique (~1 m pour une lave très chaude, plusieurs mètres en
/// refroidissant), d'où des fronts épais et des coulées qui s'arrêtent d'elles-mêmes.</item>
/// <item><b>Refroidissement</b> : la chaleur se perd par la surface, d'autant plus vite que la
/// coulée est mince ; la lave froide se fige en basalte, qui s'ajoute au sol (les coulées
/// suivantes passent dessus ou à côté).</item>
/// </list>
/// Volume conservé : ce qui sort de la grille est compté à part. Unités : mètres, secondes.
/// </summary>
public sealed class LavaFlow
{
    public const int Size = 256;
    public const double CellM = 40;
    /// <summary>Durée simulée d'une itération (s) : une lave rapide (~3 m/s) traverse une cellule.</summary>
    public const double StepSeconds = 12;
    /// <summary>Demi-côté de la grille (m).</summary>
    public const double HalfExtentM = Size * CellM / 2;

    private const double CriticalHotM = 0.8;        // épaisseur critique de la lave à la sortie
    private const double CriticalColdM = 6;         // ... presque figée
    private const double CoolingSecondsPerM = 4 * 3600;   // ~4 h de refroidissement par mètre d'épaisseur
    private const double Relaxation = 0.4;          // part du déséquilibre corrigée par itération (stabilité)
    private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, 1, -1 };
    private static readonly double[] Distance = { 1, 1, 1, 1, Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2) };

    private readonly double[] _ground;      // sol, basalte compris (m)
    private readonly double[] _thickness;   // lave liquide (m)
    private readonly double[] _heat;        // température réduite : 1 à la sortie, 0 = figée
    private readonly double[] _basalt;      // épaisseur de lave figée (m)
    private readonly double[] _deltaThickness, _deltaHeatContent;
    private int _minX, _maxX, _minY, _maxY;   // rectangle des cellules qui ont de la lave

    public Double3 Vent { get; }
    /// <summary>Repère du plan tangent : axes x et y de la grille.</summary>
    public Double3 AxisX { get; }
    public Double3 AxisY { get; }
    public double ErupedVolume { get; private set; }
    public double LostVolume { get; private set; }
    public double SimulatedSeconds { get; private set; }

    /// <summary>Grille posée sur le relief détaillé autour de la bouche.</summary>
    public LavaFlow(TerrainHeightField field, Double3 vent)
        : this(vent, (direction) =>
        {
            int hint = -1;
            // (sans les détails plus fins que ~4 cellules : les petits creux du relief détaillé
            // retiendraient la lave en flaques épaisses ; le shader la dessine sur le vrai relief)
            return field.Height(direction, 4 * CellM, ref hint);
        })
    {
    }

    /// <summary>Grille posée sur un relief quelconque (tests).</summary>
    public LavaFlow(Double3 vent, Func<Double3, double> height)
    {
        Vent = vent.Normalized();
        (AxisX, AxisY) = TangentFrame(Vent);
        int n = Size * Size;
        _ground = new double[n];
        _thickness = new double[n];
        _heat = new double[n];
        _basalt = new double[n];
        _deltaThickness = new double[n];
        _deltaHeatContent = new double[n];
        System.Threading.Tasks.Parallel.For(0, Size, j =>
        {
            for (int i = 0; i < Size; i++)
                _ground[j * Size + i] = height(DirectionOf(i, j));
        });
        // La bouche : le point le plus haut à moins de ~600 m du centre (le relief détaillé place
        // le sommet du cône un peu à côté de la position du volcan)
        // (un vrai sommet : plus haut que ses 8 voisines ; sur une pente sans sommet, le centre)
        int reach = (int)(600 / CellM), best = (Size / 2) * Size + Size / 2;
        double bestHeight = double.MinValue;
        for (int j = Size / 2 - reach; j <= Size / 2 + reach; j++)
            for (int i = Size / 2 - reach; i <= Size / 2 + reach; i++)
            {
                double h = _ground[j * Size + i];
                bool summit = h > bestHeight;
                for (int k = 0; k < 8 && summit; k++)
                    summit = _ground[(j + Dy[k]) * Size + i + Dx[k]] < h;
                if (summit)
                {
                    best = j * Size + i;
                    bestHeight = h;
                }
            }
        _ventCell = best;
        _minX = _maxX = best % Size;
        _minY = _maxY = best / Size;
    }

    private readonly int _ventCell;

    /// <summary>Cellule de la bouche (où sort la lave).</summary>
    public (int i, int j) VentCell => (_ventCell % Size, _ventCell / Size);

    /// <summary>Repère tangent en un point (le même que dans le shader du terrain).</summary>
    public static (Double3 x, Double3 y) TangentFrame(Double3 up)
    {
        Double3 reference = Math.Abs(up.Y) < 0.9 ? Double3.UnitY : new Double3(1, 0, 0);
        Double3 x = up.Cross(reference).Normalized();
        return (x, up.Cross(x));
    }

    /// <summary>Direction (unitaire) du centre de la cellule (i, j).</summary>
    public Double3 DirectionOf(int i, int j)
    {
        double x = (i + 0.5) * CellM - HalfExtentM, y = (j + 0.5) * CellM - HalfExtentM;
        return (Vent + AxisX * (x / TerrainHeightField.RadiusMeters) + AxisY * (y / TerrainHeightField.RadiusMeters)).Normalized();
    }

    public double Ground(int i, int j) => _ground[j * Size + i];
    public double Thickness(int i, int j) => _thickness[j * Size + i];
    public double Heat(int i, int j) => _heat[j * Size + i];
    public double Basalt(int i, int j) => _basalt[j * Size + i];

    /// <summary>
    /// Lave figée à inscrire dans la croûte modulaire (étape 16a) : épaisseur de basalte (m) de
    /// chaque cellule, ramenée à zéro sur les <paramref name="margin"/> rangées du bord (raccord
    /// sans marche avec le relief autour). Null s'il n'y a pas encore de basalte.
    /// </summary>
    public float[] BasaltLayer(int margin = 4)
    {
        var layer = new float[Size * Size];
        bool any = false;
        for (int j = margin; j < Size - margin; j++)
            for (int i = margin; i < Size - margin; i++)
            {
                double b = _basalt[j * Size + i];
                if (b > 0.02)
                {
                    layer[j * Size + i] = (float)b;
                    any = true;
                }
            }
        return any ? layer : null;
    }

    /// <summary>Volume de lave encore liquide plus figée sur la grille (m³).</summary>
    public double VolumeOnGrid()
    {
        double sum = 0;
        for (int k = 0; k < _thickness.Length; k++)
            sum += _thickness[k] + _basalt[k];
        return sum * CellM * CellM;
    }

    /// <summary>
    /// Fait avancer la coulée de <paramref name="iterations"/> pas de <see cref="StepSeconds"/>,
    /// avec un débit de sortie de lave (m³/s ; 0 quand l'éruption est finie : la lave en place
    /// continue de couler et de refroidir).
    /// </summary>
    public void Step(int iterations, double effusionRate)
    {
        int vent = _ventCell;
        for (int it = 0; it < iterations; it++)
        {
            if (effusionRate > 0)
            {
                // Sortie de lave sur les 4 cellules centrales, à la température de sortie
                double added = effusionRate * StepSeconds / (CellM * CellM) / 4;
                foreach (int c in new[] { vent, vent - 1, vent - Size, vent - Size - 1 })
                {
                    _heat[c] = (_heat[c] * _thickness[c] + added) / (_thickness[c] + added);
                    _thickness[c] += added;
                }
                ErupedVolume += effusionRate * StepSeconds;
            }
            Flow();
            Cool();
            SimulatedSeconds += StepSeconds;
        }
    }

    /// <summary>Épaisseur critique : la lave reste fluide tant qu'elle est chaude, puis durcit vite en fin de refroidissement.</summary>
    private static double Critical(double heat)
    {
        double cold = 1 - heat;
        return CriticalHotM + (CriticalColdM - CriticalHotM) * cold * cold * cold;
    }

    private void Flow()
    {
        // Rectangle actif, élargi d'une cellule (la lave peut avancer d'une cellule par pas)
        int x0 = Math.Max(0, _minX - 1), x1 = Math.Min(Size - 1, _maxX + 1);
        int y0 = Math.Max(0, _minY - 1), y1 = Math.Min(Size - 1, _maxY + 1);
        Span<double> drop = stackalloc double[8];
        for (int j = y0; j <= y1; j++)
            for (int i = x0; i <= x1; i++)
            {
                int c = j * Size + i;
                double h = _thickness[c];
                double mobile = h - Critical(_heat[c]);
                if (mobile <= 0)
                    continue;
                double head = _ground[c] + h;
                double total = 0, largest = 0;
                for (int k = 0; k < 8; k++)
                {
                    int ni = i + Dx[k], nj = j + Dy[k];
                    double neighborHead = ni < 0 || nj < 0 || ni >= Size || nj >= Size
                        ? _ground[c] - 1   // bord : la lave sort de la grille
                        : _ground[nj * Size + ni] + _thickness[nj * Size + ni];
                    double d = (head - neighborHead) / Distance[k];
                    drop[k] = d > 0 ? d : 0;
                    total += drop[k];
                    largest = Math.Max(largest, head - neighborHead);
                }
                if (total <= 0)
                    continue;
                // Ce qui part : pas plus que la lave mobile, ni que la moitié du plus grand
                // dénivelé (sinon la cellule se viderait plus bas que sa voisine : oscillations)
                double outflow = Relaxation * Math.Min(mobile, 0.5 * largest);
                double heatContent = outflow * _heat[c];
                _deltaThickness[c] -= outflow;
                _deltaHeatContent[c] -= heatContent;
                for (int k = 0; k < 8; k++)
                {
                    if (drop[k] <= 0)
                        continue;
                    double share = drop[k] / total;
                    int ni = i + Dx[k], nj = j + Dy[k];
                    if (ni < 0 || nj < 0 || ni >= Size || nj >= Size)
                    {
                        LostVolume += outflow * share * CellM * CellM;
                        continue;
                    }
                    int n = nj * Size + ni;
                    _deltaThickness[n] += outflow * share;
                    _deltaHeatContent[n] += heatContent * share;
                }
            }
        // Application des échanges (température : moyenne pondérée par l'épaisseur)
        for (int j = y0; j <= y1; j++)
            for (int i = x0; i <= x1; i++)
            {
                int c = j * Size + i;
                double dh = _deltaThickness[c];
                if (dh == 0)
                    continue;
                double before = _thickness[c];
                double after = Math.Max(0, before + dh);
                _heat[c] = after > 1e-9 ? Math.Clamp((before * _heat[c] + _deltaHeatContent[c]) / after, 0, 1) : 0;
                _thickness[c] = after;
                _deltaThickness[c] = 0;
                _deltaHeatContent[c] = 0;
                if (after > 0)
                {
                    _minX = Math.Min(_minX, i); _maxX = Math.Max(_maxX, i);
                    _minY = Math.Min(_minY, j); _maxY = Math.Max(_maxY, j);
                }
            }
    }

    private void Cool()
    {
        for (int j = _minY; j <= _maxY; j++)
            for (int i = _minX; i <= _maxX; i++)
            {
                int c = j * Size + i;
                double h = _thickness[c];
                if (h <= 0)
                    continue;
                // Perte par la surface : une coulée mince refroidit vite, une épaisse garde sa chaleur
                _heat[c] -= StepSeconds / (CoolingSecondsPerM * Math.Max(h, 0.3));
                if (_heat[c] <= 0)
                {
                    // Figée : devient du basalte, qui fait désormais partie du sol
                    _ground[c] += h;
                    _basalt[c] += h;
                    _thickness[c] = 0;
                    _heat[c] = 0;
                }
            }
    }

    /// <summary>
    /// Image de la coulée pour le shader du terrain, 2 octets par cellule : rouge = lave
    /// incandescente (0 à 255), vert = sol recouvert de lave (liquide ou figée).
    /// </summary>
    public void FillTexture(byte[] pixels)
    {
        for (int c = 0; c < _thickness.Length; c++)
        {
            double h = _thickness[c];
            double glow = h > 0.05 ? Math.Sqrt(_heat[c]) * Math.Min(1, h / 0.5) : 0;
            double cover = Math.Min(1, (h + _basalt[c]) / 0.3);
            pixels[c * 2] = (byte)(glow * 255);
            pixels[c * 2 + 1] = (byte)(cover * 255);
        }
    }
}
