using System;
using System.Collections.Generic;

namespace Planet.Simulation.Terrain;

/// <summary>Morceau retenu pour l'affichage, avec sa zone de morphing (distances à la caméra, m).</summary>
public readonly record struct SelectedChunk(ChunkKey Key, float MorphStart, float MorphEnd);

/// <summary>
/// Choix des morceaux à afficher (CDLOD : « Continuous Distance-Dependent Level of Detail »,
/// Strugar 2009, adapté à une sphère cubique). On descend dans le quadtree depuis les 6 faces ;
/// un morceau est subdivisé tant que la caméra est plus proche que <see cref="LodFactor"/> fois
/// sa taille. Les tailles doublant d'un niveau au suivant, deux morceaux voisins n'ont jamais
/// plus d'un niveau d'écart, et les sommets du plus fin sont entièrement « morphés » sur la
/// frontière commune : pas de fente.
/// <para>On n'affiche ni ce qui est hors du champ de vision, ni ce qui est caché derrière
/// l'horizon (la mer, à plat au rayon de la planète, cache tout ce qui est au-delà).</para>
/// <para>Un morceau dont les enfants ne sont pas encore calculés reste affiché à leur place :
/// les enfants manquants sont demandés aux threads de calcul, les plus proches d'abord.</para>
/// </summary>
public sealed class TerrainQuadtree
{
    /// <summary>Niveau le plus fin : morceaux de ~38 m, un sommet tous les ~1,2 m.</summary>
    public int MaxLevel { get; set; } = 18;

    /// <summary>Distance de subdivision, en tailles de morceau. Plus grand = plus de détail au loin.</summary>
    public double LodFactor { get; set; } = 2.5;

    // Altitudes extrêmes possibles (fonds marins compris), tant qu'un morceau n'est pas calculé (m)
    private const double UnknownMinHeight = -11000;
    private const double UnknownMaxHeight = 9000;
    // Part de la plage de distance d'un niveau consacrée au morphing
    private const double MorphFraction = 0.3;

    /// <summary>Plage de distance où un morceau de ce niveau est remplacé par ses enfants (m).</summary>
    public double SubdivisionDistance(int level) => LodFactor * CubeSphere.ChunkSizeMeters(level);

    /// <summary>Sphère englobante prudente d'un morceau pas encore calculé.</summary>
    public static (Double3 center, double radius) EstimatedBounds(ChunkKey key,
        double minHeight = UnknownMinHeight, double maxHeight = UnknownMaxHeight)
    {
        double radius = TerrainHeightField.RadiusMeters;
        Double3 centerDirection = CubeSphere.CenterDirection(key);
        Double3 center = centerDirection * (radius + (minHeight + maxHeight) / 2);
        double boundsRadius = 0;
        // Coins et milieux des bords, aux deux altitudes extrêmes
        for (int a = 0; a <= 2; a++)
            for (int b = 0; b <= 2; b++)
            {
                Double3 direction = CubeSphere.Direction(key.Face, key.MinS + a * key.FaceSize / 2, key.MinT + b * key.FaceSize / 2);
                boundsRadius = Math.Max(boundsRadius, (direction * (radius + minHeight)).DistanceTo(center));
                boundsRadius = Math.Max(boundsRadius, (direction * (radius + maxHeight)).DistanceTo(center));
            }
        return (center, boundsRadius * 1.02);
    }

    /// <summary>
    /// Sélectionne les morceaux à afficher.
    /// </summary>
    /// <param name="camera">Position de la caméra (m, repère de la planète).</param>
    /// <param name="bounds">Sphère englobante d'un morceau calculé, ou null s'il ne l'est pas encore.</param>
    /// <param name="inFrustum">Vrai si la sphère (centre relatif à la caméra, rayon) est dans le champ.</param>
    /// <param name="request">Demande le calcul d'un morceau (priorité : plus petit = plus urgent).</param>
    public List<SelectedChunk> Select(Double3 camera,
                                      Func<ChunkKey, (Double3 center, double radius)?> bounds,
                                      Func<Double3, double, bool> inFrustum,
                                      Action<ChunkKey, double> request)
    {
        var selected = new List<SelectedChunk>();
        double radius = TerrainHeightField.RadiusMeters;
        double cameraDistance = Math.Max(camera.Length(), radius + 0.01);
        double horizon = Math.Sqrt(cameraDistance * cameraDistance - radius * radius);

        void Visit(ChunkKey key, (Double3 center, double radius) box)
        {
            double distanceToCenter = camera.DistanceTo(box.center);
            // Derrière l'horizon : la sphère du niveau de la mer cache tout le morceau
            double farthest = box.center.Length() + box.radius;
            double beyond = Math.Sqrt(Math.Max(0, farthest * farthest - radius * radius));
            if (distanceToCenter - box.radius > horizon + beyond)
                return;
            if (!inFrustum(box.center - camera, box.radius))
                return;

            double distance = Math.Max(0, distanceToCenter - box.radius);
            if (key.Level < MaxLevel && distance < SubdivisionDistance(key.Level))
            {
                var children = new (Double3, double)?[4];
                bool allReady = true;
                for (int c = 0; c < 4; c++)
                {
                    ChunkKey child = key.Child(c);
                    children[c] = bounds(child);
                    if (children[c] == null)
                    {
                        allReady = false;
                        // Priorité : les plus grossiers et les plus proches d'abord
                        request(child, child.Level * 1e9 + distance);
                    }
                }
                if (allReady)
                {
                    for (int c = 0; c < 4; c++)
                        Visit(key.Child(c), children[c]!.Value);
                    return;
                }
            }
            // Zone de morphing : jusqu'à la distance où le parent reprend la main
            double morphEnd = key.Level == 0 ? double.MaxValue / 4 : SubdivisionDistance(key.Level - 1);
            double morphStart = morphEnd * (1 - MorphFraction);
            selected.Add(new SelectedChunk(key, (float)morphStart, (float)Math.Min(morphEnd, 1e30)));
        }

        for (int face = 0; face < CubeSphere.FaceCount; face++)
        {
            var root = new ChunkKey(face, 0, 0, 0);
            var box = bounds(root);
            if (box == null)
            {
                request(root, -1);
                continue;
            }
            Visit(root, box.Value);
        }
        return selected;
    }
}
