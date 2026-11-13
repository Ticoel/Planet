using System;
using System.Collections.Generic;

namespace Planet.Simulation.Terrain;

/// <summary>Un morceau de matériau en mouvement, pour l'affichage (blocs, poussière).</summary>
/// <param name="Direction">Direction (unitaire) depuis le centre de la planète.</param>
/// <param name="Height">Altitude (m) de la surface du flot à cet endroit.</param>
/// <param name="Speed">Vitesse (m/s).</param>
/// <param name="Size">Taille d'un bloc représentatif (m).</param>
public readonly record struct DebrisParticle(Double3 Direction, double Height, float Speed, float Size, int Id);

/// <summary>
/// Un mouvement de terrain en temps réel (étape 16a) : glissement, éboulement ou avalanche
/// rocheuse, simulé sur une grille posée sur le relief détaillé, et inscrit dans la croûte
/// modulaire (<see cref="CrustPatch"/>) : la niche d'arrachement se creuse, la masse descend la
/// pente, puis s'étale et se dépose ; le relief garde ensuite la cicatrice et le dépôt.
/// <list type="number">
/// <item><b>Rupture</b> : la zone qui cède est une ellipse allongée dans la pente, en forme de
/// cuillère (épaisseur maximale au centre, nulle au bord), là où la pente dépasse le seuil de la
/// sorte de mouvement. Épaisseur moyenne d'après la loi aire-volume des glissements (Larsen et
/// al. 2010) ; quelques mètres pour un éboulement.</item>
/// <item><b>Écoulement</b> : la masse arrachée est découpée en paquets (méthode lagrangienne,
/// comme les modèles de parcours SPH-« DAN3D », McDougall et Hungr 2004). Chaque paquet glisse
/// sous l'effet de la pente de la surface du flot (le poids des paquets voisins l'étale), freiné
/// par la loi de Voellmy (Voellmy 1955, modèle RAMMS de Christen et al. 2010) :
/// frottement de Coulomb μ g cos θ + frottement turbulent g v² / (ξ h).
/// μ : 0,6 pour des blocs (éboulis à ~32°), 0,22 pour un sol détrempé, 0,12 pour une grande
/// avalanche rocheuse (qui va bien plus loin qu'elle ne tombe).</item>
/// <item><b>Dépôt</b> : un paquet arrêté (frottement plus fort que la pente) se dépose ; le dépôt
/// meuble ne tient pas au-delà de l'angle de talus naturel (~35°) : l'excédent glisse vers le
/// bas (comme un tas de sable).</item>
/// </list>
/// Le volume est conservé (arraché = en mouvement + déposé). Les bords de la grille restent
/// intacts : le calque se raccorde sans marche au relief qui l'entoure. Unités : m, s.
/// </summary>
public sealed class Landslide
{
    private const double G = 9.81;
    /// <summary>Rangées du bord jamais modifiées (raccord avec le relief autour).</summary>
    public const int Margin = 4;
    private const double ReposeTan = 0.70;     // angle de talus naturel ~35°
    private const double StopSpeed = 0.15;     // m/s
    private const double StopSeconds = 0.4;
    private const double MaxSeconds = 240;
    private const int MaxParticles = 6000;

    private sealed class Packet
    {
        public double X, Y, Vx, Vy, Volume, Still;
        public float Size;
        public int Id;
    }

    private readonly double[] _initial;   // sol avant l'événement (calques précédents compris)
    private readonly double[] _ground;    // sol actuel (sans la masse en mouvement)
    private readonly double[] _flow;      // épaisseur de la masse en mouvement (m)
    private readonly double[] _deposit;   // dépôt meuble (m), seul à pouvoir encore glisser
    private readonly double[] _scour;     // vitesse maximale du flot passé ici (m/s)
    private readonly List<Packet> _packets = new();
    private readonly double _mu, _xi;

    public SlideKind Kind { get; }
    public CrustPatch Patch { get; }
    public int Size => Patch.Size;
    public double CellM => Patch.CellM;
    public Double3 Source { get; }
    public double ReleasedVolume { get; }
    public double DepositedVolume { get; private set; }
    public double MovingVolume { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double MaxSpeed { get; private set; }
    public bool Finished => _packets.Count == 0;
    public int PacketCount => _packets.Count;

    /// <summary>
    /// Prépare un mouvement de terrain qui part de <paramref name="source"/>.
    /// </summary>
    /// <param name="height">Relief (m) dans une direction, calques de la croûte compris.</param>
    /// <param name="scale">Facteur de taille (1 : taille type de la sorte de mouvement).</param>
    public Landslide(Func<Double3, double> height, Double3 source, SlideKind kind, double scale, int seed, float day)
    {
        Kind = kind;
        Source = source.Normalized();
        var random = new Random(seed);
        scale = Math.Clamp(scale, 0.3, 3);
        (double releaseRadius, double minSlopeDeg, double minCell, double mu, double xi) = kind switch
        {
            SlideKind.Rockfall => (5.0 * scale, 38.0, 1.5, 0.60, 400.0),
            SlideKind.RockAvalanche => (90.0 * scale, 24.0, 8.0, 0.12, 600.0),
            _ => (28.0 * scale, 20.0, 2.5, 0.22, 250.0),
        };
        _mu = mu;
        _xi = xi;

        // Pente au départ et parcours probable (ligne d'énergie de Scheidegger)
        var (_, downhill, h0) = SlopeStability.LocalSlope(height, Source, Math.Max(3, releaseRadius / 3));
        double length = releaseRadius * 1.7, width = releaseRadius;
        double area = Math.PI * length * width;
        double meanDepth = kind == SlideKind.Rockfall ? 1.6 * Math.Sqrt(scale) : SlopeStability.MeanDepth(area) * (kind == SlideKind.RockAvalanche ? 1.6 : 1.0);
        double estimatedVolume = area * meanDepth;
        double ratio = SlopeStability.TravelAngleRatio(estimatedVolume);
        double r = TerrainHeightField.RadiusMeters;
        Double3 Along(double s) => (Source + downhill * (s / r)).Normalized();
        double runout = releaseRadius * 3;
        double step = Math.Max(2, releaseRadius / 2);
        for (double s = step; s < 6000; s += step)
        {
            // Plus loin que la ligne d'énergie, la masse ne peut pas aller
            runout = s;
            if (h0 - height(Along(s)) < ratio * s)
                break;
        }
        runout *= 1.25;
        double half = runout * 0.5 + 2.5 * releaseRadius + Margin * minCell;
        double cell = Math.Max(minCell, 2 * half / 200);
        int size = Math.Clamp(2 * (int)Math.Ceiling(half / cell), 48, 200);
        cell = 2 * half / size;
        // Centre décalé vers l'aval : la niche près du bord amont, le dépôt au milieu
        Double3 center = (Source + downhill * ((runout * 0.5 - 0.5 * releaseRadius) / r)).Normalized();
        Patch = new CrustPatch(center, size, cell, day, kind.ToString());

        int n = size * size;
        _initial = new double[n];
        _ground = new double[n];
        _flow = new double[n];
        _deposit = new double[n];
        _scour = new double[n];
        var patch = Patch;
        System.Threading.Tasks.Parallel.For(0, size, j =>
        {
            for (int i = 0; i < size; i++)
                _initial[j * size + i] = height(patch.DirectionOf(i, j));
        });
        Array.Copy(_initial, _ground, n);

        // Rupture : ellipse en cuillère autour de la source, allongée dans la pente
        var (sx, sy) = patch.Project(Source);
        double ux = downhill.Dot(patch.AxisX), uy = downhill.Dot(patch.AxisY);
        double un = Math.Sqrt(ux * ux + uy * uy);
        if (un < 1e-9) { ux = 1; uy = 0; } else { ux /= un; uy /= un; }
        double minSlope = minSlopeDeg * Math.PI / 180.0;
        double peak = 2 * meanDepth;   // cuillère : moyenne = moitié du maximum
        double released = 0;
        var cells = new List<(int i, int j, double depth)>();
        for (int j = Margin; j < size - Margin; j++)
            for (int i = Margin; i < size - Margin; i++)
            {
                double x = (i + 0.5) * cell - patch.HalfExtentM - sx, y = (j + 0.5) * cell - patch.HalfExtentM - sy;
                double along = x * ux + y * uy, across = -x * uy + y * ux;
                // (bord de la niche un peu irrégulier)
                double wobble = 1 + 0.15 * Math.Sin(3 * Math.Atan2(across, along) + seed);
                double q = (along * along) / (length * length) + (across * across) / (width * width);
                q /= wobble * wobble;
                if (q >= 1)
                    continue;
                if (CellSlope(_initial, i, j) < minSlope)
                    continue;
                double depth = peak * (1 - q);
                cells.Add((i, j, depth));
            }
        // Pente trop douce partout : une petite niche quand même (on a demandé un mouvement ici)
        if (cells.Count == 0)
        {
            var (ci, cj) = ((int)((sx + patch.HalfExtentM) / cell), (int)((sy + patch.HalfExtentM) / cell));
            for (int j = cj - 1; j <= cj + 1; j++)
                for (int i = ci - 1; i <= ci + 1; i++)
                    if (i >= Margin && j >= Margin && i < size - Margin && j < size - Margin)
                        cells.Add((i, j, meanDepth));
        }
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cells.Count / (double)MaxParticles)));
        int id = 0;
        foreach (var (i, j, depth) in cells)
        {
            int k = j * size + i;
            _ground[k] -= depth;
            _scour[k] = 1;
            released += depth * cell * cell;
            // Un paquet par cellule (ou par groupe de cellules pour les très grands glissements)
            if (stride > 1 && ((i % stride) != 0 || (j % stride) != 0))
            {
                // (son volume va au paquet le plus proche, créé plus bas)
                continue;
            }
            double v = depth * cell * cell * stride * stride;
            _packets.Add(new Packet
            {
                X = (i + 0.5 + 0.3 * (random.NextDouble() - 0.5)) * cell,
                Y = (j + 0.5 + 0.3 * (random.NextDouble() - 0.5)) * cell,
                Volume = v,
                Size = (float)Math.Clamp(Math.Cbrt(v) * (kind == SlideKind.Rockfall ? 0.7 : 0.35), 0.3, 4.0),
                Id = id++,
            });
        }
        // Les volumes des paquets valent exactement le volume arraché
        double packetSum = 0;
        foreach (var p in _packets) packetSum += p.Volume;
        foreach (var p in _packets) p.Volume *= released / packetSum;
        ReleasedVolume = released;
        MovingVolume = released;
    }

    /// <summary>Pente (rad) d'une grille au centre de la cellule (i, j).</summary>
    private double CellSlope(double[] grid, int i, int j)
    {
        int s = Size;
        double gx = (grid[j * s + Math.Min(i + 1, s - 1)] - grid[j * s + Math.Max(i - 1, 0)]) / (2 * CellM);
        double gy = (grid[Math.Min(j + 1, s - 1) * s + i] - grid[Math.Max(j - 1, 0) * s + i]) / (2 * CellM);
        return Math.Atan(Math.Sqrt(gx * gx + gy * gy));
    }

    /// <summary>Valeur interpolée d'une grille au point (x, y) en mètres depuis le coin.</summary>
    private double Bilinear(double[] grid, double x, double y)
    {
        double fx = Math.Clamp(x / CellM - 0.5, 0, Size - 1.001), fy = Math.Clamp(y / CellM - 0.5, 0, Size - 1.001);
        int i = (int)fx, j = (int)fy;
        double tx = fx - i, ty = fy - j;
        int k = j * Size + i;
        return grid[k] * (1 - tx) * (1 - ty) + grid[k + 1] * tx * (1 - ty) + grid[k + Size] * (1 - tx) * ty + grid[k + Size + 1] * tx * ty;
    }

    /// <summary>Répartit un volume sur les 4 cellules voisines du point (épaisseur ajoutée).</summary>
    private void Splat(double[] grid, double x, double y, double volume)
    {
        double fx = Math.Clamp(x / CellM - 0.5, Margin, Size - Margin - 1.001), fy = Math.Clamp(y / CellM - 0.5, Margin, Size - Margin - 1.001);
        int i = (int)fx, j = (int)fy;
        double tx = fx - i, ty = fy - j;
        int k = j * Size + i;
        double h = volume / (CellM * CellM);
        grid[k] += h * (1 - tx) * (1 - ty);
        grid[k + 1] += h * tx * (1 - ty);
        grid[k + Size] += h * (1 - tx) * ty;
        grid[k + Size + 1] += h * tx * ty;
    }

    /// <summary>Fait avancer le mouvement de <paramref name="seconds"/> secondes.</summary>
    public void Step(double seconds)
    {
        double remaining = seconds;
        while (remaining > 1e-6 && _packets.Count > 0)
        {
            double speed = 0.5;
            foreach (var p in _packets)
                speed = Math.Max(speed, Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy));
            double dt = Math.Min(remaining, Math.Min(0.05, 0.4 * CellM / speed));
            Substep(dt);
            remaining -= dt;
        }
    }

    private void Substep(double dt)
    {
        Array.Clear(_flow);
        foreach (var p in _packets)
            Splat(_flow, p.X, p.Y, p.Volume);

        double lo = (Margin + 0.5) * CellM, hi = (Size - Margin - 0.5) * CellM;
        double c = CellM;
        bool timeUp = ElapsedSeconds + dt > MaxSeconds;
        for (int index = _packets.Count - 1; index >= 0; index--)
        {
            var p = _packets[index];
            // Pente de la surface du flot (sol + masse en mouvement) : le poids des voisins étale le flot
            double sx = (Bilinear(_ground, p.X + c, p.Y) + Bilinear(_flow, p.X + c, p.Y)
                         - Bilinear(_ground, p.X - c, p.Y) - Bilinear(_flow, p.X - c, p.Y)) / (2 * c);
            double sy = (Bilinear(_ground, p.X, p.Y + c) + Bilinear(_flow, p.X, p.Y + c)
                         - Bilinear(_ground, p.X, p.Y - c) - Bilinear(_flow, p.X, p.Y - c)) / (2 * c);
            double gx = (Bilinear(_ground, p.X + c, p.Y) - Bilinear(_ground, p.X - c, p.Y)) / (2 * c);
            double gy = (Bilinear(_ground, p.X, p.Y + c) - Bilinear(_ground, p.X, p.Y - c)) / (2 * c);
            double grad2 = sx * sx + sy * sy;
            double cosTheta = 1 / Math.Sqrt(1 + gx * gx + gy * gy);
            double depth = Math.Max(0.3, Bilinear(_flow, p.X, p.Y));
            // Accélération le long de la pente (g sin θ, projetée sur le plan)
            double drive = G / Math.Sqrt(1 + grad2);
            double ax = -sx * drive, ay = -sy * drive;
            p.Vx += ax * dt;
            p.Vy += ay * dt;
            double v = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            // Voellmy : Coulomb + turbulence
            double friction = (_mu * G * cosTheta + G * v * v / (_xi * depth)) * dt;
            if (v <= friction)
            {
                p.Vx = 0;
                p.Vy = 0;
                v = 0;
            }
            else
            {
                p.Vx -= friction * p.Vx / v;
                p.Vy -= friction * p.Vy / v;
                v -= friction;
            }
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            bool atEdge = false;
            if (p.X < lo || p.X > hi || p.Y < lo || p.Y > hi)
            {
                p.X = Math.Clamp(p.X, lo, hi);
                p.Y = Math.Clamp(p.Y, lo, hi);
                p.Vx = p.Vy = 0;
                atEdge = true;
            }
            MaxSpeed = Math.Max(MaxSpeed, v);
            int ci = Math.Clamp((int)(p.X / c), 0, Size - 1), cj = Math.Clamp((int)(p.Y / c), 0, Size - 1);
            int k = cj * Size + ci;
            _scour[k] = Math.Max(_scour[k], v);

            // Arrêt : immobile assez longtemps, la pente ne suffit plus à vaincre le frottement
            p.Still = v < StopSpeed ? p.Still + dt : 0;
            bool held = Math.Sqrt(ax * ax + ay * ay) <= _mu * G * cosTheta * 1.05;
            if ((p.Still > StopSeconds && held) || atEdge || timeUp || p.Still > 4 * StopSeconds)
            {
                Splat(_ground, p.X, p.Y, p.Volume);
                Splat(_deposit, p.X, p.Y, p.Volume);
                DepositedVolume += p.Volume;
                MovingVolume -= p.Volume;
                _packets.RemoveAt(index);
            }
        }
        RelaxDeposits(2);
        ElapsedSeconds += dt;
        if (_packets.Count == 0)
        {
            RelaxDeposits(30);
            MovingVolume = 0;
        }
    }

    /// <summary>
    /// Le dépôt meuble ne tient pas au-delà de l'angle de talus : l'excédent glisse vers la
    /// voisine la plus basse (algorithme de « thermal erosion », Musgrave et al. 1989), sans
    /// toucher aux rangées du bord.
    /// </summary>
    private void RelaxDeposits(int iterations)
    {
        int s = Size;
        for (int it = 0; it < iterations; it++)
        {
            bool moved = false;
            for (int j = Margin; j < s - Margin; j++)
                for (int i = Margin; i < s - Margin; i++)
                {
                    int k = j * s + i;
                    if (_deposit[k] <= 1e-4)
                        continue;
                    int best = -1;
                    double bestExcess = 0, bestDistance = 1;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            if (di == 0 && dj == 0)
                                continue;
                            int ni = i + di, nj = j + dj;
                            if (ni < Margin || nj < Margin || ni >= s - Margin || nj >= s - Margin)
                                continue;
                            double distance = (di != 0 && dj != 0 ? Math.Sqrt(2) : 1) * CellM;
                            double excess = _ground[k] - _ground[nj * s + ni] - ReposeTan * distance;
                            if (excess > bestExcess)
                            {
                                bestExcess = excess;
                                best = nj * s + ni;
                                bestDistance = distance;
                            }
                        }
                    if (best < 0)
                        continue;
                    double amount = Math.Min(_deposit[k], bestExcess * 0.5);
                    _ground[k] -= amount;
                    _deposit[k] -= amount;
                    _ground[best] += amount;
                    _deposit[best] += amount;
                    moved = true;
                }
            if (!moved)
                break;
        }
    }

    /// <summary>
    /// Calque de la croûte à publier : hauteur ajoutée (sol actuel + masse en mouvement − sol
    /// d'avant) et sol remanié (niche et couloir arrachés, dépôt d'éboulis).
    /// </summary>
    public (float[] delta, float[] debris) BuildLayer()
    {
        int n = Size * Size;
        var delta = new float[n];
        var debris = new float[n];
        Array.Clear(_flow);
        foreach (var p in _packets)
            Splat(_flow, p.X, p.Y, p.Volume);
        for (int k = 0; k < n; k++)
        {
            double d = _ground[k] + _flow[k] - _initial[k];
            delta[k] = (float)d;
            double removed = _initial[k] - _ground[k];
            if (_deposit[k] + _flow[k] > 0.05)
                debris[k] = (float)Math.Min(1, (_deposit[k] + _flow[k]) / 0.4);
            else if (removed > 0.05)
                debris[k] = -(float)Math.Min(1, removed / 0.5);
            else if (_scour[k] > 2)
                debris[k] = -(float)Math.Min(0.6, _scour[k] / 15);   // couloir : végétation arrachée
        }
        // Bords : rien (raccord sans marche)
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                if (i < Margin || j < Margin || i >= Size - Margin || j >= Size - Margin)
                {
                    delta[j * Size + i] = 0;
                    debris[j * Size + i] = 0;
                }
        return (delta, debris);
    }

    /// <summary>Paquets en mouvement (pour dessiner les blocs et la poussière).</summary>
    public void GetParticles(List<DebrisParticle> output)
    {
        output.Clear();
        foreach (var p in _packets)
        {
            double x = p.X - Patch.HalfExtentM, y = p.Y - Patch.HalfExtentM;
            double h = Bilinear(_ground, p.X, p.Y) + Bilinear(_flow, p.X, p.Y);
            output.Add(new DebrisParticle(Patch.DirectionAt(x, y), h, (float)Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy), p.Size, p.Id));
        }
    }

    /// <summary>Centre de masse de ce qui bouge encore (direction), ou la source.</summary>
    public Double3 MovingCenter()
    {
        if (_packets.Count == 0)
            return Source;
        double x = 0, y = 0, v = 0;
        foreach (var p in _packets)
        {
            x += p.X * p.Volume;
            y += p.Y * p.Volume;
            v += p.Volume;
        }
        return Patch.DirectionAt(x / v - Patch.HalfExtentM, y / v - Patch.HalfExtentM);
    }

    /// <summary>Sol actuel (m) au centre de la cellule (i, j) (tests).</summary>
    public double GroundAt(int i, int j) => _ground[j * Size + i];
    /// <summary>Sol d'avant l'événement (m) au centre de la cellule (i, j) (tests).</summary>
    public double InitialAt(int i, int j) => _initial[j * Size + i];
    /// <summary>Volume total présent dans la grille (m³) : arraché compté négatif, dépôts et flot positifs.</summary>
    public double NetVolume()
    {
        double sum = 0;
        for (int k = 0; k < _ground.Length; k++)
            sum += _ground[k] - _initial[k];
        foreach (var p in _packets)
            sum += p.Volume / (CellM * CellM);
        return sum * CellM * CellM;
    }
}
