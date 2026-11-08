using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>
/// Évolution du découpage et du mouvement des plaques au fil du temps géologique :
/// <list type="bullet">
/// <item>changements de cap : chaque plaque se choisit périodiquement un nouveau mouvement
/// et s'en rapproche progressivement ;</item>
/// <item>fusion : une plaque devenue minuscule est absorbée par sa voisine principale ;</item>
/// <item>fracturation (rifting) : une plaque géante, ou un nombre de plaques trop faible,
/// provoque la rupture d'une plaque en deux morceaux qui s'écartent.</item>
/// </list>
/// Ensemble, ces mécanismes entretiennent un cycle : rassemblement, rupture, dispersion.
/// </summary>
public sealed partial class TectonicPlates
{
    // Durée entre deux changements de cap d'une plaque (Ma)
    private const float MinMotionPeriodMyr = 40f;
    private const float MaxMotionPeriodMyr = 120f;

    // Temps caractéristique pour qu'une plaque rejoigne son nouveau mouvement (Ma)
    private const float MotionResponseMyr = 25f;

    // Une plaque plus petite que cette fraction de la surface est absorbée par sa voisine
    private const float MinPlateFraction = 0.004f;

    // Une plaque plus grande que cette fraction de la surface finit par se fracturer
    private const float MaxPlateFraction = 0.25f;

    // Délai minimal entre deux fracturations (Ma), pour que chaque rupture reste visible ;
    // plus court quand il manque des plaques
    private const float RiftCooldownMyr = 8f;
    private const float UrgentRiftCooldownMyr = 2f;

    // Vitesse d'écartement des deux morceaux d'une plaque fracturée (°/Ma)
    private const float MinRiftSpeedDegPerMyr = 0.3f;
    private const float MaxRiftSpeedDegPerMyr = 0.8f;

    // Le nombre de plaques peut monter jusqu'à ce multiple de la cible
    private const int MaxPlateFactor = 2;

    // Un morceau détaché d'une plaque plus petit que cette fraction de la surface est rendu
    // à la plaque qui l'entoure (au-delà, on le garde : c'est peut-être un vrai fragment)
    private const float MaxFragmentFraction = 0.01f;

    // Longueur du front de collision continentale (km) à partir de laquelle deux plaques se soudent
    private const float SutureFrontKm = 600f;

    // Délai minimal entre deux sutures (Ma) : une collision continentale met des dizaines de
    // millions d'années à souder deux plaques
    private const float SutureCooldownMyr = 20f;

    // Temps caractéristique du freinage de deux plaques dont les continents se percutent (Ma)
    private const float CollisionBrakingMyr = 20f;

    /// <summary>Nombre de plaques autour duquel la simulation se maintient.</summary>
    public int TargetPlateCount { get; }

    /// <summary>Nombre de plaques encore présentes.</summary>
    public int AlivePlateCount
    {
        get
        {
            int count = 0;
            foreach (var plate in Plates)
                if (plate.IsAlive)
                    count++;
            return count;
        }
    }

    /// <summary>Nombre de fracturations et de fusions depuis le début.</summary>
    public int RiftCount { get; private set; }
    public int MergeCount { get; private set; }

    private readonly Random _random;
    private double _lastRiftMyr = double.NegativeInfinity;
    private double _lastSutureMyr = double.NegativeInfinity;

    private void InitializeMotionPlan(Plate plate)
    {
        plate.TargetAngularVelocity = plate.AngularVelocity;
        plate.NextMotionChangeMyr = TimeMyr + Mathf.Lerp(0f, MaxMotionPeriodMyr, _random.NextSingle());
    }

    /// <summary>Appelé à chaque nouvel état de référence, avec la durée écoulée depuis le précédent.</summary>
    private void UpdatePlateDynamics(float elapsedMyr)
    {
        UpdateMotions(elapsedMyr);
        CleanUpCoastlines();
        CleanUpFragments();
        SmoothPlateBoundaries();
        MergeTinyPlates();
        HandleContinentalCollisions(elapsedMyr);
        TryRift();

        foreach (var plate in Plates)
            plate.CellCount = 0;
        foreach (int p in CellPlate)
            Plates[p].CellCount++;
    }

    // --- Changements de cap ------------------------------------------------------------------

    private void UpdateMotions(float elapsedMyr)
    {
        // Rapprochement exponentiel vers la cible : au bout de MotionResponseMyr, ~63 % du chemin
        float blend = 1f - Mathf.Exp(-elapsedMyr / MotionResponseMyr);
        foreach (var plate in Plates)
        {
            if (!plate.IsAlive)
                continue;

            if (TimeMyr >= plate.NextMotionChangeMyr)
            {
                float speedDeg = Mathf.Lerp(MinAngularSpeedDegPerMyr, MaxAngularSpeedDegPerMyr, _random.NextSingle());
                plate.TargetAngularVelocity = RandomUnitVector(_random) * Mathf.DegToRad(speedDeg);
                plate.NextMotionChangeMyr = TimeMyr + Mathf.Lerp(MinMotionPeriodMyr, MaxMotionPeriodMyr, _random.NextSingle());
            }

            plate.SetAngularVelocity(plate.AngularVelocity.Lerp(plate.TargetAngularVelocity, blend));
        }
    }

    // --- Nettoyage --------------------------------------------------------------------------

    /// <summary>
    /// Le rééchantillonnage laisse parfois des îlots : quelques cellules d'une plaque détachées
    /// du reste, au milieu d'une autre plaque. Une plaque rigide est d'un seul tenant, donc on
    /// ne garde que son plus grand morceau ; les petits morceaux détachés sont rendus à la plaque
    /// qui les entoure.
    /// </summary>
    private void CleanUpFragments()
    {
        int maxFragmentCells = Math.Max(1, (int)(MaxFragmentFraction * Grid.CellCount));
        var component = new int[Grid.CellCount];
        Array.Fill(component, -1);
        var components = new List<List<int>>();
        var componentPlate = new List<int>();
        var queue = new Queue<int>();

        // Repérage des morceaux connexes de chaque plaque (parcours en largeur)
        for (int start = 0; start < Grid.CellCount; start++)
        {
            if (component[start] != -1)
                continue;
            int plate = CellPlate[start];
            var cells = new List<int>();
            int id = components.Count;
            component[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                cells.Add(cell);
                foreach (int neighbor in Grid.Neighbors[cell])
                    if (component[neighbor] == -1 && CellPlate[neighbor] == plate)
                    {
                        component[neighbor] = id;
                        queue.Enqueue(neighbor);
                    }
            }
            components.Add(cells);
            componentPlate.Add(plate);
        }

        // Plus grand morceau de chaque plaque
        var mainComponent = new int[Plates.Length];
        Array.Fill(mainComponent, -1);
        for (int c = 0; c < components.Count; c++)
        {
            int plate = componentPlate[c];
            if (mainComponent[plate] == -1 || components[c].Count > components[mainComponent[plate]].Count)
                mainComponent[plate] = c;
        }

        for (int c = 0; c < components.Count; c++)
        {
            int plate = componentPlate[c];
            if (c == mainComponent[plate] || components[c].Count > maxFragmentCells)
                continue;

            // Plaque majoritaire autour du morceau
            var sharedEdges = new Dictionary<int, int>();
            foreach (int cell in components[c])
                foreach (int neighbor in Grid.Neighbors[cell])
                {
                    int other = CellPlate[neighbor];
                    if (other != plate)
                        sharedEdges[other] = sharedEdges.GetValueOrDefault(other) + 1;
                }

            int target = -1, best = -1;
            foreach (var (other, edges) in sharedEdges)
                if (edges > best)
                {
                    best = edges;
                    target = other;
                }
            if (target < 0)
                continue;

            foreach (int cell in components[c])
                CellPlate[cell] = target;
        }
    }

    /// <summary>Une passe de filtre majoritaire : une cellule entourée en majorité par une autre
    /// plaque la rejoint. Supprime les dents et les filaments d'une cellule de large.</summary>
    private void SmoothPlateBoundaries()
    {
        var result = (int[])CellPlate.Clone();
        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            int[] neighbors = Grid.Neighbors[cell];
            foreach (int n in neighbors)
            {
                int candidate = CellPlate[n];
                if (candidate == CellPlate[cell])
                    continue;

                int votes = 0;
                foreach (int m in neighbors)
                    if (CellPlate[m] == candidate)
                        votes++;

                if (votes * 2 > neighbors.Length)
                {
                    result[cell] = candidate;
                    break;
                }
            }
        }
        Array.Copy(result, CellPlate, CellPlate.Length);
    }

    // --- Fusion ------------------------------------------------------------------------------

    private void MergeTinyPlates()
    {
        int minCells = Math.Max(1, (int)(MinPlateFraction * Grid.CellCount));
        var cellsByPlate = CellsByPlate();

        for (int p = 0; p < Plates.Length; p++)
        {
            var cells = cellsByPlate[p];
            if (cells.Count == 0 || cells.Count >= minCells)
                continue;

            // Voisine avec qui la frontière commune est la plus longue
            var sharedEdges = new Dictionary<int, int>();
            foreach (int cell in cells)
                foreach (int neighbor in Grid.Neighbors[cell])
                {
                    int other = CellPlate[neighbor];
                    if (other != p)
                        sharedEdges[other] = sharedEdges.GetValueOrDefault(other) + 1;
                }
            if (sharedEdges.Count == 0)
                continue;

            int target = -1, best = -1;
            foreach (var (other, edges) in sharedEdges)
                if (edges > best)
                {
                    best = edges;
                    target = other;
                }

            foreach (int cell in cells)
                CellPlate[cell] = target;
            cellsByPlate[target].AddRange(cells);
            cells.Clear();
            MergeCount++;
        }
    }

    // --- Suture des continents --------------------------------------------------------------

    /// <summary>
    /// Quand deux continents portés par deux plaques différentes se percutent, aucun ne peut
    /// plonger. La collision freine les deux plaques : leurs mouvements se rapprochent de leur
    /// moyenne (l'Inde a fortement ralenti en heurtant l'Asie). Dès que la ligne de collision
    /// est assez longue, les deux plaques se soudent et prennent un mouvement commun, moyenne
    /// de leurs mouvements pondérée par leur surface. C'est ainsi que se forment les supercontinents.
    /// </summary>
    private void HandleContinentalCollisions(float elapsedMyr)
    {
        float edgeKm = Grid.MeanEdgeAngle * PlanetConstants.RadiusKm;
        int threshold = Math.Max(3, (int)(SutureFrontKm / edgeKm));
        var collisions = new Dictionary<(int, int), int>();

        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            if (!CellContinental[cell] || CellBoundary[cell] != BoundaryType.Convergent)
                continue;
            int plate = CellPlate[cell];
            foreach (int neighbor in Grid.Neighbors[cell])
            {
                int other = CellPlate[neighbor];
                if (other == plate || !CellContinental[neighbor])
                    continue;
                var key = plate < other ? (plate, other) : (other, plate);
                collisions[key] = collisions.GetValueOrDefault(key) + 1;
                break;
            }
        }

        if (collisions.Count == 0)
            return;
        var cellsByPlate = CellsByPlate();

        // Freinage de toutes les paires en collision
        float braking = 1f - Mathf.Exp(-elapsedMyr / CollisionBrakingMyr);
        foreach (var ((a, b), _) in collisions)
        {
            float areaA = cellsByPlate[a].Count, areaB = cellsByPlate[b].Count;
            if (areaA == 0 || areaB == 0)
                continue;
            Vector3 mean = (Plates[a].AngularVelocity * areaA + Plates[b].AngularVelocity * areaB) / (areaA + areaB);
            Plates[a].SetAngularVelocity(Plates[a].AngularVelocity.Lerp(mean, braking));
            Plates[b].SetAngularVelocity(Plates[b].AngularVelocity.Lerp(mean, braking));
        }

        if (TimeMyr - _lastSutureMyr < SutureCooldownMyr)
            return;

        (int a, int b) best = (-1, -1);
        int bestCount = 0;
        foreach (var (pair, count) in collisions)
            if (count > bestCount)
            {
                bestCount = count;
                best = pair;
            }
        if (bestCount < threshold)
            return;

        int keep = cellsByPlate[best.a].Count >= cellsByPlate[best.b].Count ? best.a : best.b;
        int absorb = keep == best.a ? best.b : best.a;
        float keepArea = cellsByPlate[keep].Count, absorbArea = cellsByPlate[absorb].Count;

        Plate kept = Plates[keep];
        Vector3 combined = (kept.AngularVelocity * keepArea + Plates[absorb].AngularVelocity * absorbArea)
                           / (keepArea + absorbArea);
        kept.SetAngularVelocity(combined);
        kept.TargetAngularVelocity = combined;

        foreach (int cell in cellsByPlate[absorb])
            CellPlate[cell] = keep;
        SutureCount++;
        _lastSutureMyr = TimeMyr;
    }

    // --- Fracturation ------------------------------------------------------------------------

    private void TryRift()
    {
        if (TimeMyr - _lastRiftMyr < UrgentRiftCooldownMyr)
            return;

        var cellsByPlate = CellsByPlate();
        int alive = 0, largest = -1;
        for (int p = 0; p < Plates.Length; p++)
        {
            if (cellsByPlate[p].Count == 0)
                continue;
            alive++;
            if (largest < 0 || cellsByPlate[p].Count > cellsByPlate[largest].Count)
                largest = p;
        }
        if (largest < 0)
            return;

        bool tooFewPlates = alive < TargetPlateCount;
        bool giantPlate = cellsByPlate[largest].Count > MaxPlateFraction * Grid.CellCount;
        if (!tooFewPlates && !giantPlate)
            return;
        bool urgent = alive < TargetPlateCount * 3 / 4;
        if (!urgent && TimeMyr - _lastRiftMyr < RiftCooldownMyr)
            return;
        if (alive >= TargetPlateCount * MaxPlateFactor)
            return;

        SplitPlate(largest, cellsByPlate[largest]);
        _lastRiftMyr = TimeMyr;
        RiftCount++;
    }

    /// <summary>
    /// Coupe une plaque en deux le long d'une ligne de rift irrégulière, puis donne aux deux
    /// morceaux des mouvements qui les écartent l'un de l'autre.
    /// </summary>
    private void SplitPlate(int plateId, List<int> cells)
    {
        var positions = Grid.Positions;

        // Deux pôles du rift éloignés l'un de l'autre, pris dans la plaque
        Vector3 a = positions[cells[_random.Next(cells.Count)]];
        Vector3 b = a;
        float farthest = 2f;
        for (int i = 0; i < 32; i++)
        {
            Vector3 candidate = positions[cells[_random.Next(cells.Count)]];
            float dot = candidate.Dot(a);
            if (dot < farthest)
            {
                farthest = dot;
                b = candidate;
            }
        }

        // Ligne de rift : médiatrice entre a et b, déformée par une ondulation pour ne pas
        // obtenir une coupure parfaitement droite
        Vector3 phase = RandomUnitVector(_random) * 10f;
        float wobble = 0.08f + 0.08f * _random.NextSingle();
        var newCells = new List<int>();
        Vector3 centroidA = Vector3.Zero, centroidB = Vector3.Zero;
        foreach (int cell in cells)
        {
            Vector3 p = positions[cell];
            float noise = Mathf.Sin(7f * p.X + phase.X) * Mathf.Sin(7f * p.Y + phase.Y) * Mathf.Sin(7f * p.Z + phase.Z);
            if (p.Dot(b) - p.Dot(a) + wobble * noise > 0f)
            {
                newCells.Add(cell);
                centroidB += p;
            }
            else
            {
                centroidA += p;
            }
        }
        if (newCells.Count == 0 || newCells.Count == cells.Count)
            return;

        int newId = AllocatePlate(newCells[0]);
        foreach (int cell in newCells)
            CellPlate[cell] = newId;

        // Écartement : une rotation d'axe cA × cB, positive, pousse les points de A vers B.
        // Le morceau B reçoit +axe (il s'éloigne de A), le morceau A reçoit -axe
        centroidA = centroidA.Normalized();
        centroidB = centroidB.Normalized();
        Vector3 axis = centroidA.Cross(centroidB);
        if (axis.LengthSquared() < 1e-10f)
            axis = RandomUnitVector(_random).Cross(centroidA);
        axis = axis.Normalized();
        float riftSpeed = Mathf.DegToRad(Mathf.Lerp(MinRiftSpeedDegPerMyr, MaxRiftSpeedDegPerMyr, _random.NextSingle()));

        Plate parent = Plates[plateId];
        Plate child = Plates[newId];
        Vector3 baseMotion = parent.AngularVelocity;
        parent.SetAngularVelocity(baseMotion - axis * riftSpeed * 0.5f);
        child.SetAngularVelocity(baseMotion + axis * riftSpeed * 0.5f);
        foreach (var plate in new[] { parent, child })
        {
            plate.TargetAngularVelocity = plate.AngularVelocity;
            plate.NextMotionChangeMyr = TimeMyr + Mathf.Lerp(MinMotionPeriodMyr, MaxMotionPeriodMyr, _random.NextSingle());
        }
    }

    /// <summary>Réutilise l'emplacement d'une plaque disparue, ou en ajoute un.</summary>
    private int AllocatePlate(int seedCell)
    {
        for (int p = 0; p < Plates.Length; p++)
            if (!Plates[p].IsAlive && !PlateHasCells(p))
            {
                Plates[p] = new Plate(p, seedCell);
                return p;
            }

        var plates = new Plate[Plates.Length + 1];
        Array.Copy(Plates, plates, Plates.Length);
        plates[^1] = new Plate(plates.Length - 1, seedCell);
        Plates = plates;
        return plates.Length - 1;
    }

    private bool PlateHasCells(int plateId)
    {
        foreach (int p in CellPlate)
            if (p == plateId)
                return true;
        return false;
    }

    private List<int>[] CellsByPlate()
    {
        var lists = new List<int>[Plates.Length];
        for (int p = 0; p < lists.Length; p++)
            lists[p] = new List<int>();
        for (int cell = 0; cell < CellPlate.Length; cell++)
            lists[CellPlate[cell]].Add(cell);
        return lists;
    }
}
