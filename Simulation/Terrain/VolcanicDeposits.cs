using System;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Ce qu'une éruption laisse dans le paysage (étape 16a), partout sur la planète, inscrit dans la
/// croûte modulaire : chaque volcan cumule ses dépôts d'éruption en éruption dans deux calques
/// (coulées, cendres), si bien que son cône grandit, que ses flancs se couvrent de coulées et que
/// ses alentours s'ensevelissent sous les cendres au fil des siècles.
/// <list type="bullet">
/// <item><b>Volumes</b> selon l'indice d'explosivité (Newhall et Self 1982) : téphras de
/// ~10^(VEI+5) m³ (10 millions de m³ pour un VEI 2, un milliard pour un VEI 4) ; coulées de
/// 10 à 50 millions de m³ pour une éruption effusive (VEI 0 ou 1, type hawaïen ou islandais).</item>
/// <item><b>Cendres</b> : épaisseur qui décroît en exponentielle de la distance, T = T₀ e^(−r/b)
/// (Pyle 1989), étirée sous le vent ; b croît avec l'éruption (0,8 km pour un VEI 2, 6 km pour un
/// VEI 5) et T₀ découle du volume (V = 2π T₀ b²).</item>
/// <item><b>Coulées</b> : quelques coulées partent de la bouche et descendent la ligne de plus
/// grande pente du relief (calques précédents compris : les nouvelles coulées passent sur ou à
/// côté des anciennes), larges de ~200 m et épaisses de ~3 m (coulées basaltiques type,
/// Walker 1973), jusqu'à épuiser leur volume, ou s'étalent en lac sur le plat, ou s'arrêtent en
/// entrant dans la mer.</item>
/// </list>
/// Les bords des calques s'amincissent jusqu'à zéro : raccord sans marche avec le relief autour.
/// </summary>
public static class VolcanicDeposits
{
    /// <summary>Demi-côté (m) et taille des calques de cendres et de coulées d'un volcan.</summary>
    public const double TephraHalfExtentM = 16000, LavaHalfExtentM = 9000;
    public const int TephraSize = 160, LavaSize = 200;
    public const float LavaCode = 2f, TephraCode = 3f;
    private const int Margin = 4, Taper = 10;
    private const double FlowWidthM = 200, FlowThicknessM = 3;

    /// <summary>Volume de téphras (m³) d'une éruption ; 0 pour une éruption effusive.</summary>
    public static double TephraVolume(int vei) => vei < 2 ? 0 : Math.Pow(10, vei + 5);

    /// <summary>Volume de lave (m³) : surtout les éruptions effusives, un peu les explosives modérées.</summary>
    public static double LavaVolume(int vei) => vei switch
    {
        <= 0 => 1e7,
        1 => 3e7,
        2 => 5e6,
        _ => 0,
    };

    /// <summary>Calque vide d'un volcan, centré sur sa bouche.</summary>
    public static CrustPatch NewPatch(Double3 vent, bool tephra, float day) => tephra
        ? new CrustPatch(vent, TephraSize, 2 * TephraHalfExtentM / TephraSize, day, "Ash")
        : new CrustPatch(vent, LavaSize, 2 * LavaHalfExtentM / LavaSize, day, "Lava");

    /// <summary>Amincissement vers le bord (0 sur les rangées du bord, 1 au-delà de quelques rangées).</summary>
    private static double EdgeFactor(int i, int j, int size)
    {
        int d = Math.Min(Math.Min(i, j), Math.Min(size - 1 - i, size - 1 - j)) - Margin;
        if (d < 0)
            return 0;
        double t = Math.Min(1.0, d / (double)Taper);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Couche de cendres (m) d'une éruption sur le calque de cendres de son volcan.
    /// </summary>
    /// <param name="downwind">Direction du vent en altitude (unitaire, tangente) ou zéro.</param>
    /// <param name="windFactor">0 : calme ; 1 : vent fort (dépôt étiré et décalé sous le vent).</param>
    public static float[] Tephra(CrustPatch patch, int vei, Double3 downwind, double windFactor, out double placedM3)
    {
        int n = patch.Size;
        var layer = new float[n * n];
        placedM3 = 0;
        double volume = TephraVolume(vei);
        if (volume <= 0)
            return layer;
        double b = 800 * Math.Pow(2, vei - 2);
        windFactor = Math.Clamp(windFactor, 0, 1);
        double stretch = 1 + 0.8 * windFactor;
        double t0 = volume / (2 * Math.PI * b * b * stretch);
        double ux = downwind.Dot(patch.AxisX), uy = downwind.Dot(patch.AxisY);
        double norm = Math.Sqrt(ux * ux + uy * uy);
        if (norm < 1e-9) { ux = 1; uy = 0; windFactor = 0; } else { ux /= norm; uy /= norm; }
        double cell = patch.CellM;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double edge = EdgeFactor(i, j, n);
                if (edge <= 0)
                    continue;
                double x = (i + 0.5) * cell - patch.HalfExtentM, y = (j + 0.5) * cell - patch.HalfExtentM;
                double along = x * ux + y * uy, across = -x * uy + y * ux;
                // (le nuage dérive : l'épaisseur maximale est un peu sous le vent de la bouche)
                along = (along - 0.4 * b * windFactor) / stretch;
                double r = Math.Sqrt(along * along + across * across);
                double t = t0 * Math.Exp(-r / b) * edge;
                layer[j * n + i] = (float)t;
                placedM3 += t * cell * cell;
            }
        return layer;
    }

    /// <summary>
    /// Coulées de lave (m) d'une éruption sur le calque de coulées de son volcan : quelques
    /// coulées qui descendent la pente du relief actuel.
    /// </summary>
    /// <param name="height">Relief (m), calques de la croûte compris.</param>
    public static float[] Lava(CrustPatch patch, Func<Double3, double> height, int vei, int seed, out double placedM3)
    {
        int n = patch.Size;
        var total = new float[n * n];
        placedM3 = 0;
        double volume = LavaVolume(vei);
        if (volume <= 0)
            return total;
        var random = new Random(seed);
        int flows = 2 + random.Next(3);
        double cell = patch.CellM;
        double half = patch.HalfExtentM;
        double limit = half - (Margin + Taper) * cell;
        var flow = new float[n * n];
        double H(double x, double y) => height(patch.DirectionAt(x, y));
        for (int f = 0; f < flows; f++)
        {
            Array.Clear(flow);
            double length = volume / flows / (FlowWidthM * FlowThicknessM * 0.9);
            // Départ près de la bouche (fissures de flanc), dans une direction au hasard
            double angle = random.NextDouble() * 2 * Math.PI;
            double x = 60 * Math.Cos(angle), y = 60 * Math.Sin(angle);
            double dx = Math.Cos(angle), dy = Math.Sin(angle);
            double step = Math.Max(20, cell * 0.5);
            double travelled = 0;
            int flat = 0;
            while (travelled < length && Math.Abs(x) < limit && Math.Abs(y) < limit)
            {
                double g = cell;
                double gx = (H(x + g, y) - H(x - g, y)) / (2 * g), gy = (H(x, y + g) - H(x, y - g)) / (2 * g);
                double slope = Math.Sqrt(gx * gx + gy * gy);
                // Vers le bas, avec un peu d'inertie et de hasard (les coulées serpentent)
                double ndx = slope > 1e-4 ? -gx / slope : dx, ndy = slope > 1e-4 ? -gy / slope : dy;
                double wobble = (random.NextDouble() - 0.5) * 0.6;
                dx = 0.55 * ndx + 0.45 * dx - wobble * dy;
                dy = 0.55 * ndy + 0.45 * dy + wobble * dx;
                double norm = Math.Sqrt(dx * dx + dy * dy);
                dx /= norm;
                dy /= norm;
                // Sur le plat, la lave s'étale en lac au lieu d'avancer
                flat = slope < 0.01 ? flat + 1 : 0;
                double width = FlowWidthM * (1 + 0.25 * Math.Min(flat, 8));
                double thickness = FlowThicknessM * (1 + 0.15 * Math.Min(flat, 8));
                Paint(flow, patch, x, y, width, thickness);
                if (H(x, y) < -5 || flat > 12)
                    break;   // dans la mer, ou en lac : la coulée s'arrête
                x += dx * step;
                y += dy * step;
                travelled += step;
            }
            for (int k = 0; k < total.Length; k++)
                total[k] += flow[k];
        }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                total[k] *= (float)EdgeFactor(i, j, n);
                placedM3 += total[k] * cell * cell;
            }
        return total;
    }

    /// <summary>Dépôt d'un tronçon de coulée : profil bombé en travers (le maximum l'emporte, pas de cumul).</summary>
    private static void Paint(float[] flow, CrustPatch patch, double x, double y, double width, double thickness)
    {
        int n = patch.Size;
        double cell = patch.CellM;
        double r = width / 2;
        int ci = (int)((x + patch.HalfExtentM) / cell), cj = (int)((y + patch.HalfExtentM) / cell);
        int reach = (int)Math.Ceiling(r / cell) + 1;
        for (int j = Math.Max(0, cj - reach); j <= Math.Min(n - 1, cj + reach); j++)
            for (int i = Math.Max(0, ci - reach); i <= Math.Min(n - 1, ci + reach); i++)
            {
                double px = (i + 0.5) * cell - patch.HalfExtentM - x, py = (j + 0.5) * cell - patch.HalfExtentM - y;
                double q = (px * px + py * py) / (r * r);
                if (q >= 1)
                    continue;
                float t = (float)(thickness * Math.Sqrt(1 - q));
                int k = j * n + i;
                if (t > flow[k])
                    flow[k] = t;
            }
    }
}
