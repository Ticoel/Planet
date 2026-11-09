using System;
using System.Collections.Generic;
using Planet.Simulation.Tectonics;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Détails des volcans, ajoutés à leur forme d'ensemble (dôme du bouclier, cône du strato) pour
/// qu'ils ressemblent aux volcans terrestres plutôt qu'à des bosses lisses :
/// <list type="bullet">
/// <item><b>Zones de rift</b> (boucliers) : deux ou trois crêtes rayonnantes depuis le sommet, le
/// long desquelles le magma remonte par des fissures (Kīlauea, Mauna Loa : rifts est et sud-ouest) ;
/// elles portent une fissure et la plupart des cônes secondaires.</item>
/// <item><b>Cônes secondaires</b> : cônes de scories de quelques centaines de mètres, avec leur
/// petit cratère (l'Etna en compte plus de 300 ; les flancs du Mauna Kea en sont criblés).</item>
/// <item><b>Coulées de lave</b> : bourrelets allongés dans le sens de la pente, de 10 à 30 m.</item>
/// <item><b>Érosion</b> : un volcan éteint se creuse de vallées rayonnantes, d'autant plus
/// profondes qu'il est vieux (Kauai, Tahiti, le Cantal) ; un stratovolcan est raviné dès sa
/// naissance (barrancos des Canaries).</item>
/// </list>
/// Tout dépend d'une graine propre à chaque volcan (date de naissance, taille) : un volcan garde
/// ses détails quand sa plaque le déplace.
/// </summary>
public sealed class VolcanoDetail
{
    private readonly int _seed;
    private readonly double[] _rifts;                                   // directions (radians)
    private readonly (double u, double v, double radius, double height)[] _cones;

    private VolcanoDetail(int seed, double[] rifts, (double, double, double, double)[] cones)
    {
        _seed = seed;
        _rifts = rifts;
        _cones = cones;
    }

    public static VolcanoDetail For(Landform landform)
    {
        int seed = (int)(BitConverter.DoubleToInt64Bits(landform.BirthMyr) ^ (long)(landform.RadiusM * 977) ^ (long)landform.Kind * 7919);
        var random = new Random(seed);
        bool shield = landform.Kind == LandformKind.ShieldVolcano;
        // Rifts : deux ou trois, espacés de 100 à 180°
        int riftCount = shield ? 2 + random.Next(2) : 0;
        var rifts = new double[riftCount];
        double first = random.NextDouble() * 2 * Math.PI;
        for (int k = 0; k < riftCount; k++)
            rifts[k] = first + k * 2 * Math.PI / riftCount + (random.NextDouble() - 0.5) * 0.6;
        // Cônes secondaires : surtout le long des rifts (boucliers), au hasard sur les flancs (stratos)
        double radius = landform.RadiusM;
        int coneCount = shield ? 8 + (int)(radius / 2500) : 3 + random.Next(5);
        var cones = new List<(double, double, double, double)>();
        for (int c = 0; c < coneCount; c++)
        {
            double angle, q;
            if (riftCount > 0 && random.NextDouble() < 0.7)
            {
                angle = rifts[random.Next(riftCount)] + (random.NextDouble() - 0.5) * 0.12;
                q = 0.08 + 0.75 * random.NextDouble();
            }
            else
            {
                angle = random.NextDouble() * 2 * Math.PI;
                q = shield ? 0.1 + 0.8 * random.NextDouble() : 0.2 + 0.6 * random.NextDouble();
            }
            double coneRadius = 200 + 500 * random.NextDouble();
            // Cônes de scories : hauteur ~0,18 × diamètre de base (Wood 1980)
            double coneHeight = 0.36 * coneRadius * (0.7 + 0.6 * random.NextDouble());
            cones.Add((Math.Cos(angle) * q * radius, Math.Sin(angle) * q * radius, coneRadius, coneHeight));
        }
        return new VolcanoDetail(seed, rifts, cones.ToArray());
    }

    /// <summary>
    /// Hauteur ajoutée (m) au point (u, v) du plan tangent (m, origine au sommet).
    /// </summary>
    /// <param name="q">distance au sommet / rayon du volcan</param>
    /// <param name="scale">fraîcheur × croissance (détails récents : coulées, cônes, fissures)</param>
    /// <param name="minWavelength">plus petite longueur d'onde demandée (m) : les détails plus fins
    /// sont omis (sinon ils scintilleraient de loin)</param>
    public double Height(Landform landform, double u, double v, double q, double scale, double minWavelength = 1)
    {
        double radius = landform.RadiusM, peak = landform.PeakHeightM;
        bool shield = landform.Kind == LandformKind.ShieldVolcano;
        double r = Math.Sqrt(u * u + v * v);
        double angle = Math.Atan2(v, u);
        double edge = 1 - SmoothStep(0.8, 1.0, q);   // tout s'estompe au pied du volcan
        double h = 0;

        // Rifts : crête large et basse, fissure étroite au milieu
        foreach (double rift in _rifts)
        {
            double delta = Math.IEEERemainder(angle - rift, 2 * Math.PI);
            if (Math.Abs(delta) > Math.PI / 2)
                continue;
            double across = r * Math.Sin(Math.Abs(delta));
            double along = SmoothStep(0.04, 0.12, q) * (1 - SmoothStep(0.6, 0.95, q));
            double ridgeWidth = 0.05 * radius;
            h += scale * along * 0.07 * peak * Math.Exp(-across * across / (ridgeWidth * ridgeWidth));
            h -= scale * along * 20 * Math.Exp(-across * across / (50.0 * 50.0));
        }

        // Coulées : bourrelets allongés dans le sens de la pente (bruit étiré : fin en travers,
        // lâche le long du rayon ; les coulées convergent vers le sommet)
        double cos = r > 1 ? u / r : 1, sin = r > 1 ? v / r : 0;
        double across_ = radius / 450.0;
        double flows = GradientNoise.Sample(r / 2500.0, cos * across_, sin * across_, _seed)
                       + 0.5 * GradientNoise.Sample(r / 900.0, cos * across_ * 2.3, sin * across_ * 2.3, _seed + 1);
        h += scale * edge * (shield ? 9 : 7) * flows * SmoothStep(0.03, 0.1, q);

        // Ravines et vallées rayonnantes (bruit « en crêtes » : 1 − |n| marque des sillons) :
        // dès la naissance pour un strato, avec l'âge pour un bouclier
        double age = 1 - landform.Freshness;
        double depth = shield ? age * 0.3 * peak : (0.04 + 0.2 * age) * peak;
        if (depth > 1)
        {
            double gullyAcross = radius / (shield ? 2200.0 : 900.0);
            double n = GradientNoise.Sample(r / (shield ? 9000.0 : 3000.0), cos * gullyAcross, sin * gullyAcross, _seed + 2);
            double valley = SmoothStep(0.75, 1.0, 1 - Math.Abs(n));
            h -= depth * valley * SmoothStep(0.08, 0.3, q) * (1 - SmoothStep(0.85, 1.0, q)) * Math.Pow(1 - q, 0.5);
        }

        // Relief des champs de lave, du kilomètre à la dizaine de mètres : lobes arrondis des
        // coulées (bouclier, laves fluides) ou crêtes vives (strato, laves visqueuses, ravines)
        double roughness = 0, amplitude = shield ? 80 : 110, wavelength = 3000;
        for (int octave = 0; octave < 6 && wavelength > minWavelength; octave++)
        {
            double fade = Math.Clamp(wavelength / minWavelength - 1, 0, 1);   // pas de coupure nette
            double n = GradientNoise.Sample(u / wavelength, v / wavelength, octave * 3.7, _seed + 10 + octave);
            double shape = shield ? Math.Abs(n) * 2 - 0.6 : (1 - Math.Abs(n)) * (1 - Math.Abs(n)) * 2 - 0.9;
            roughness += amplitude * fade * shape;
            amplitude *= 0.48;
            wavelength *= 0.45;
        }
        h += edge * SmoothStep(0.02, 0.08, q) * roughness;

        h += Crater(landform, u, v, r, scale, minWavelength);

        // Cônes secondaires, avec leur cratère
        foreach (var (cu, cv, coneRadius, coneHeight) in _cones)
        {
            double du = u - cu, dv = v - cv;
            double d2 = du * du + dv * dv;
            if (d2 >= coneRadius * coneRadius)
                continue;
            double d = Math.Sqrt(d2) / coneRadius;
            double cone = Math.Pow(1 - d, 1.3);
            double crater = d < 0.3 ? 0.45 * (1 - d * d / 0.09) : 0;
            h += scale * coneHeight * (cone - crater);
        }
        return h;
    }

    /// <summary>
    /// Caldeira (bouclier) ou cratère (strato) au sommet : contour festonné (une caldeira naît de
    /// plusieurs effondrements qui se recoupent), léger bourrelet, parois raides découpées en
    /// gradins (effondrements successifs) et ravinées, éboulis au pied, fond plat de lave figée,
    /// et au centre un puits plus profond, la bouche active (Halemaʻumaʻu au Kīlauea).
    /// </summary>
    private double Crater(Landform landform, double u, double v, double r, double scale, double minWavelength)
    {
        bool shield = landform.Kind == LandformKind.ShieldVolcano;
        double cos = r > 1 ? u / r : 1, sin = r > 1 ? v / r : 0;
        double rim = (shield ? 0.045 : 0.027) * landform.RadiusM
                     * (1 + 0.12 * GradientNoise.Sample(cos * 2.5, sin * 2.5, 0.5, _seed + 20)
                          + 0.05 * GradientNoise.Sample(cos * 7, sin * 7, 1.5, _seed + 21));
        double x = r / rim;
        if (x >= 1.3)
            return 0;
        // (profondeur : ~5 % de la hauteur du bouclier, comme au Mauna Loa ; 8 % pour un strato)
        double depth = (shield ? 0.05 : 0.08) * landform.PeakHeightM * scale;
        // Bourrelet du bord
        double lip = 0.08 * depth * Math.Exp(-(x - 1) * (x - 1) / (0.1 * 0.1));
        if (x >= 1)
            return lip;
        // Parois : de la bordure (t = 1) au fond (t = 0), trois gradins
        double t = SmoothStep(0.6, 1.0, x);
        // (gradins irréguliers : leur nombre et leur position changent le long du bord, comme des
        // pans effondrés à des moments différents)
        double steps = 3 + 1.5 * GradientNoise.Sample(cos * 1.7, sin * 1.7, 3.3, _seed + 26);
        double phase = 0.5 * GradientNoise.Sample(cos * 4.5, sin * 4.5, 5.1, _seed + 27);
        double k = Math.Max(t * steps + phase, 0);
        double stair = Math.Clamp((Math.Floor(k) + SmoothStep(0.55, 1.0, k % 1) - phase) / steps, 0, 1);
        double profile = 0.4 * t + 0.6 * stair;
        double h = -depth * (1 - profile) + lip;
        // Ravines des parois (sillons dans le sens de la pente) et éboulis au pied
        double wall = t * (1 - t) * 4;
        if (rim / 12 > minWavelength)
        {
            double gully = 1 - Math.Abs(GradientNoise.Sample(cos * rim / 140, sin * rim / 140, x * 1.5, _seed + 22)
                                        + 0.4 * GradientNoise.Sample(cos * rim / 50, sin * rim / 50, x * 3, _seed + 25));
            h -= 0.15 * depth * wall * SmoothStep(0.55, 1.0, gully);
        }
        // Intérieur : rien n'y est lisse
        double inside = 1 - t;
        double detail = 0, amp = 0.15 * depth, wavelength = 0.35 * rim;
        for (int octave = 0; octave < 5 && wavelength > minWavelength; octave++)
        {
            // Blocs effondrés et plaques de lave figée : crêtes vives (bruit « en crêtes »)
            double n = 1 - Math.Abs(GradientNoise.Sample(u / wavelength, v / wavelength, 0.3 + octave, _seed + 23 + octave));
            detail += amp * Math.Clamp(wavelength / minWavelength - 1, 0, 1) * (n * n - 0.4);
            amp *= 0.5;
            wavelength *= 0.45;
        }
        h += (0.35 + 0.65 * inside) * detail;
        // Éboulis au pied des parois : cônes de débris qui débordent sur le fond
        double apron = Math.Exp(-(x - 0.64) * (x - 0.64) / (0.06 * 0.06));
        h += 0.12 * depth * apron * Math.Max(0, GradientNoise.Sample(cos * rim / 260, sin * rim / 260, 2.3, _seed + 30) + 0.3);
        // Fissures du fond : sillons étroits et sinueux (le fond s'affaisse et se fend)
        if (rim / 40 > minWavelength)
        {
            double crack = Math.Abs(GradientNoise.Sample(u / (0.3 * rim), v / (0.3 * rim), 4.1, _seed + 31));
            h -= 0.05 * depth * inside * (1 - SmoothStep(0.0, 0.04, crack));
        }
        // Puits central, la bouche active : parois presque verticales
        double pit = r / (0.22 * rim);
        if (pit < 1)
            h -= (landform.Active ? 0.7 : 0.35) * depth * (1 - SmoothStep(0.8, 1.0, pit));
        return h;
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }
}
