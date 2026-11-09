using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>Nature d'une frontière entre deux plaques, selon leur mouvement relatif.</summary>
public enum BoundaryType : byte
{
    None,
    /// <summary>Les plaques se rapprochent : subduction, collision, montagnes.</summary>
    Convergent,
    /// <summary>Les plaques s'écartent : dorsale, rift, nouvelle croûte.</summary>
    Divergent,
    /// <summary>Les plaques coulissent l'une contre l'autre : faille transformante.</summary>
    Transform,
}

/// <summary>
/// Découpage de la planète en plaques tectoniques et calcul de leurs frontières.
/// Étape 2a : génération des plaques. Étape 2b : mouvement et classification des frontières.
/// La dérive est dans ce fichier ; fracturations, fusions et changements de cap dans
/// TectonicPlates.Dynamics.cs.
/// </summary>
public sealed partial class TectonicPlates
{
    // Vitesses angulaires tirées au hasard : 0,2 à 1,2 °/Ma, soit environ 2 à 13 cm/an à
    // l'équateur de la plaque, l'ordre de grandeur des plaques terrestres
    private const float MinAngularSpeedDegPerMyr = 0.3f;
    private const float MaxAngularSpeedDegPerMyr = 1.5f;

    // Taille des paquets de cellules confiés à chaque cœur du processeur
    private const int ParallelChunkSize = 2048;

    // Nombre de candidats testés pour placer chaque germe (échantillonnage « meilleur candidat »)
    private const int SeedCandidates = 12;

    // Passes de lissage des frontières après la croissance (adoucit le contour fractal)
    private const int SmoothingPasses = 2;

    // Une frontière n'est transformante que si le mouvement relatif est presque parallèle à
    // elle : composante normale < 0,5 × composante tangentielle, soit moins de ~27° d'écart.
    // Avec un seuil à 45°, la moitié des frontières seraient transformantes, bien plus que sur Terre
    private const float TransformRatio = 0.5f;

    // Âge attribué à toute la croûte au départ : on la considère « ancienne », pour que la
    // croûte neuve des dorsales ressorte nettement
    public const float InitialCrustAgeMyr = 200f;

    // Déplacement maximal par pas de temps, en fraction de la distance entre cellules voisines.
    // Au-delà, une plaque pourrait « sauter » une cellule et le rééchantillonnage se tromperait
    private const float MaxDisplacementPerStep = 0.5f;

    // Déplacement (en cellules) au bout duquel on prend un nouvel état de référence.
    // Entre deux références, les déplacements s'additionnent exactement ; à chaque nouvelle
    // référence, les positions sont arrondies à la cellule près
    private const float RebaseDisplacement = 3f;

    // Un vide est une vraie ouverture si une plaque voisine s'en éloigne avec un angle de moins
    // de ~70° (cosinus 0,35) entre sa vitesse et la direction opposée au vide
    private const float OpeningDirectionThreshold = 0.35f;

    public PlanetGrid Grid { get; }
    /// <summary>Toutes les plaques, y compris celles disparues (CellCount = 0), dont les
    /// emplacements sont réutilisés lors des fracturations.</summary>
    public Plate[] Plates { get; private set; }

    /// <summary>Plaque de chaque cellule.</summary>
    public int[] CellPlate { get; }

    /// <summary>Type de frontière portée par chaque cellule (None à l'intérieur d'une plaque).</summary>
    public BoundaryType[] CellBoundary { get; }

    /// <summary>Vitesse relative à la frontière, en cm/an (0 à l'intérieur d'une plaque).</summary>
    public float[] CellBoundaryRate { get; }

    /// <summary>Âge de la croûte de chaque cellule, en millions d'années.</summary>
    public float[] CellCrustAge { get; }

    /// <summary>Temps géologique écoulé depuis la génération, en millions d'années.</summary>
    public double TimeMyr { get; private set; }

    // État de référence : plaques et croûte figées à un instant donné. Chaque pas recalcule
    // l'état courant à partir de cette référence et du temps écoulé depuis
    private readonly int[] _referencePlate;
    private readonly float[] _referenceAge;
    private float _sinceReferenceMyr;

    // Tampons réutilisés à chaque pas pour éviter les allocations
    private readonly int[] _nextPlate;
    private readonly float[] _nextAge;
    private Quaternion[] _inverseRotations;

    // Cellule de référence d'où provient la croûte de chaque cellule courante (-1 : croûte
    // neuve). Les modifications de la croûte (montagnes, érosion) sont écrites dans la
    // référence via cette correspondance, sinon le pas suivant les effacerait
    private readonly int[] _sourceCell;

    // Collision continentale relevée pendant le rééchantillonnage parallèle (une par cellule),
    // appliquée ensuite dans l'ordre des cellules pour que le résultat ne dépende pas de
    // l'ordre d'exécution des cœurs
    private readonly int[] _accretionWinner;
    private readonly int[] _accretionLoser;

    private TectonicPlates(PlanetGrid grid, Plate[] plates, int[] cellPlate, int targetPlateCount, Random random)
    {
        Grid = grid;
        TargetPlateCount = targetPlateCount;
        _random = random;
        Plates = plates;
        CellPlate = cellPlate;
        CellBoundary = new BoundaryType[grid.CellCount];
        CellBoundaryRate = new float[grid.CellCount];
        CellCrustAge = new float[grid.CellCount];
        Array.Fill(CellCrustAge, InitialCrustAgeMyr);

        _referencePlate = (int[])cellPlate.Clone();
        _referenceAge = (float[])CellCrustAge.Clone();
        _nextPlate = new int[grid.CellCount];
        _nextAge = new float[grid.CellCount];
        _inverseRotations = new Quaternion[plates.Length];
        _accretionWinner = new int[grid.CellCount];
        _accretionLoser = new int[grid.CellCount];
        _sourceCell = new int[grid.CellCount];
        for (int cell = 0; cell < grid.CellCount; cell++)
            _sourceCell[cell] = cell;

        CellContinental = new bool[grid.CellCount];
        CellThickness = new float[grid.CellCount];
        CellElevation = new float[grid.CellCount];
        CellSubducting = new bool[grid.CellCount];
        _referenceContinental = new bool[grid.CellCount];
        _referenceThickness = new float[grid.CellCount];
        _nextContinental = new bool[grid.CellCount];
        _nextThickness = new float[grid.CellCount];
        _consumed = new bool[grid.CellCount];
    }

    public static TectonicPlates Generate(PlanetGrid grid, int plateCount, int seed)
    {
        if (plateCount < 2 || plateCount > grid.CellCount)
            throw new ArgumentOutOfRangeException(nameof(plateCount));

        var random = new Random(seed);
        var seeds = PickSpreadSeeds(grid, plateCount, random);
        var plates = new Plate[plateCount];
        for (int i = 0; i < plateCount; i++)
            plates[i] = new Plate(i, seeds[i]);

        var cellPlate = GrowPlates(grid, plates, random);

        foreach (var plate in plates)
        {
            plate.EulerPole = RandomUnitVector(random);
            float speedDeg = Mathf.Lerp(MinAngularSpeedDegPerMyr, MaxAngularSpeedDegPerMyr, random.NextSingle());
            plate.AngularSpeed = Mathf.DegToRad(speedDeg);
        }

        var result = new TectonicPlates(grid, plates, cellPlate, plateCount, random);
        foreach (var plate in plates)
            result.InitializeMotionPlan(plate);
        result.InitializeContinents();
        result.InitializeLandforms(seed);
        result.ClassifyBoundaries();
        result.UpdateElevation();
        return result;
    }

    /// <summary>Vitesse de la cellule (portée par sa plaque), en rayons par Ma.</summary>
    public Vector3 VelocityAt(int cell) => Plates[CellPlate[cell]].VelocityAt(Grid.Positions[cell]);

    /// <summary>Vitesse de la cellule en cm/an.</summary>
    public float SpeedCmPerYear(int cell) => VelocityAt(cell).Length() * PlanetConstants.CmPerYearPerRadiusPerMyr;

    // --- Dérive (horloge géologique) ---------------------------------------------------------

    /// <summary>Pas de temps maximal (Ma) garantissant qu'aucune plaque ne se déplace de plus
    /// d'une demi-cellule par pas.</summary>
    public float MaxTimeStep()
    {
        float maxAngularSpeed = 0f;
        foreach (var plate in Plates)
            maxAngularSpeed = Mathf.Max(maxAngularSpeed, Mathf.Abs(plate.AngularSpeed));
        return maxAngularSpeed <= 0f
            ? float.PositiveInfinity
            : MaxDisplacementPerStep * Grid.MeanEdgeAngle / maxAngularSpeed;
    }

    /// <summary>
    /// Avance la tectonique de <paramref name="dtMyr"/> millions d'années.
    /// Les cellules de la grille restent fixes ; ce sont les plaques qui glissent dessus.
    /// Pour chaque cellule, on remonte le temps jusqu'à l'état de référence : on applique à sa
    /// position la rotation inverse de chaque plaque candidate (pour toute la durée écoulée
    /// depuis la référence), et on regarde si le point d'origine appartenait à cette plaque.
    /// Remonter à la référence plutôt qu'au pas précédent est essentiel : un pas déplace les
    /// plaques de moins d'une demi-cellule, et l'arrondi à la cellule la plus proche effacerait
    /// le mouvement à chaque fois.
    /// <list type="bullet">
    /// <item>Une seule plaque recouvre la cellule : elle en hérite (avec sa croûte d'origine).</item>
    /// <item>Plusieurs plaques (convergence) : voir <see cref="Overrides"/> ; la croûte
    /// continentale ne plonge jamais, elle s'empile (collision).</item>
    /// <item>Aucune plaque (divergence) : un vide s'ouvre, comblé par de la croûte océanique
    /// neuve d'âge 0 rattachée à la plaque voisine majoritaire.</item>
    /// </list>
    /// </summary>
    public void Step(float dtMyr)
    {
        if (dtMyr <= 0f)
            return;

        _sinceReferenceMyr += dtMyr;
        if (_inverseRotations.Length < Plates.Length)
            _inverseRotations = new Quaternion[Plates.Length];
        float maxAngularSpeed = 0f;
        for (int p = 0; p < Plates.Length; p++)
        {
            var plate = Plates[p];
            maxAngularSpeed = Mathf.Max(maxAngularSpeed, Mathf.Abs(plate.AngularSpeed));
            _inverseRotations[p] = plate.AngularSpeed == 0f
                ? Quaternion.Identity
                : new Quaternion(plate.EulerPole, -plate.AngularSpeed * _sinceReferenceMyr);
        }

        Array.Fill(_nextPlate, -1);
        Array.Fill(_accretionWinner, -1);

        // Chaque cellule est indépendante : le calcul est réparti sur tous les cœurs
        ForEachCellParallel(ResampleCell);

        for (int cell = 0; cell < Grid.CellCount; cell++)
            if (_accretionWinner[cell] >= 0)
                AccreteIfContinental(_accretionWinner[cell], _accretionLoser[cell]);

        FillGaps();

        Array.Copy(_nextPlate, CellPlate, CellPlate.Length);
        Array.Copy(_nextAge, CellCrustAge, CellCrustAge.Length);
        Array.Copy(_nextContinental, CellContinental, CellContinental.Length);
        Array.Copy(_nextThickness, CellThickness, CellThickness.Length);

        foreach (var plate in Plates)
            plate.CellCount = 0;
        foreach (int p in CellPlate)
            Plates[p].CellCount++;

        // Nouvelle référence quand les plaques ont assez bougé : la croûte neuve créée depuis
        // la dernière référence est alors intégrée aux plaques et se met à voyager avec elles
        if (maxAngularSpeed * _sinceReferenceMyr >= RebaseDisplacement * Grid.MeanEdgeAngle)
            Rebase();

        TimeMyr += dtMyr;
        ClassifyBoundaries();
        UpdateCrust(dtMyr);
        UpdateLandforms(dtMyr);
        UpdateElevation();
    }

    /// <summary>Exécute une action pour chaque cellule, par paquets répartis sur les cœurs.</summary>
    private void ForEachCellParallel(Action<int> action)
    {
        Parallel.ForEach(Partitioner.Create(0, Grid.CellCount, ParallelChunkSize), range =>
        {
            for (int cell = range.Item1; cell < range.Item2; cell++)
                action(cell);
        });
    }

    /// <summary>Rééchantillonnage d'une cellule (voir <see cref="Step"/>). N'écrit que dans les
    /// cases de cette cellule : peut tourner en parallèle.</summary>
    private void ResampleCell(int cell)
    {
        var positions = Grid.Positions;
        Span<int> candidates = stackalloc int[8];

        // Plaques candidates : celle de la cellule et celles de ses voisines dans l'état
        // courant (un pas déplace de moins d'une demi-cellule, une plaque plus lointaine
        // ne peut pas arriver)
        int candidateCount = 0;
        AddCandidate(candidates, ref candidateCount, CellPlate[cell]);
        foreach (int neighbor in Grid.Neighbors[cell])
            AddCandidate(candidates, ref candidateCount, CellPlate[neighbor]);

        int chosenPlate = -1;
        int sourceCell = -1;
        for (int k = 0; k < candidateCount; k++)
        {
            int p = candidates[k];
            Vector3 origin = _inverseRotations[p] * positions[cell];
            int nearest = Grid.NearestCell(origin, cell);
            if (_referencePlate[nearest] != p)
                continue;

            if (chosenPlate == -1)
            {
                chosenPlate = p;
                sourceCell = nearest;
                continue;
            }

            // Recouvrement (convergence) : une seule croûte reste en surface
            int winner, loser;
            if (Overrides(nearest, sourceCell, p == CellPlate[cell]))
            {
                winner = nearest;
                loser = sourceCell;
                chosenPlate = p;
                sourceCell = nearest;
            }
            else
            {
                winner = sourceCell;
                loser = nearest;
            }
            if (_accretionWinner[cell] < 0 && _referenceContinental[winner] && _referenceContinental[loser])
            {
                _accretionWinner[cell] = winner;
                _accretionLoser[cell] = loser;
            }
        }

        if (chosenPlate >= 0)
        {
            _nextPlate[cell] = chosenPlate;
            _nextAge[cell] = _referenceAge[sourceCell] + _sinceReferenceMyr;
            _nextContinental[cell] = _referenceContinental[sourceCell];
            _nextThickness[cell] = _referenceThickness[sourceCell];
            _sourceCell[cell] = sourceCell;
        }
        else
        {
            _sourceCell[cell] = -1;
        }
    }

    private void Rebase()
    {
        // Les vitesses et le découpage des plaques ne changent qu'au moment d'une nouvelle
        // référence : entre deux références, la rotation de chaque plaque doit rester constante
        UpdatePlateDynamics(_sinceReferenceMyr);
        Array.Copy(CellPlate, _referencePlate, CellPlate.Length);
        Array.Copy(CellCrustAge, _referenceAge, CellCrustAge.Length);
        Array.Copy(CellContinental, _referenceContinental, CellContinental.Length);
        Array.Copy(CellThickness, _referenceThickness, CellThickness.Length);
        Array.Clear(_consumed);
        for (int cell = 0; cell < _sourceCell.Length; cell++)
            _sourceCell[cell] = cell;
        _sinceReferenceMyr = 0f;
    }

    private static void AddCandidate(Span<int> candidates, ref int count, int plate)
    {
        for (int i = 0; i < count; i++)
            if (candidates[i] == plate)
                return;
        if (count < candidates.Length)
            candidates[count++] = plate;
    }

    /// <summary>Comble les cellules qu'aucune plaque ne recouvre, en les rattachant à la plaque
    /// la plus représentée parmi les voisines : croûte océanique neuve en cas d'ouverture
    /// (dorsale, rift), copie de la croûte voisine pour un simple trou d'arrondi.</summary>
    private void FillGaps()
    {
        var gaps = new List<int>();
        for (int cell = 0; cell < Grid.CellCount; cell++)
            if (_nextPlate[cell] == -1)
                gaps.Add(cell);

        Span<int> votes = stackalloc int[8];
        Span<int> votePlates = stackalloc int[8];

        while (gaps.Count > 0)
        {
            var remaining = new List<int>();
            var resolved = new List<(int cell, int plate, int donor)>();

            foreach (int cell in gaps)
            {
                int distinct = 0;
                foreach (int neighbor in Grid.Neighbors[cell])
                {
                    int p = _nextPlate[neighbor];
                    if (p == -1)
                        continue;
                    int index = votePlates[..distinct].IndexOf(p);
                    if (index < 0)
                    {
                        votePlates[distinct] = p;
                        votes[distinct] = 1;
                        distinct++;
                    }
                    else
                    {
                        votes[index]++;
                    }
                }

                if (distinct == 0)
                {
                    remaining.Add(cell);
                    continue;
                }

                int best = 0;
                for (int i = 1; i < distinct; i++)
                    if (votes[i] > votes[best])
                        best = i;
                int plate = votePlates[best];

                // Vraie ouverture (une plaque voisine s'éloigne) : croûte océanique neuve.
                // Sinon, c'est un simple trou d'arrondi du rééchantillonnage : on recopie la
                // croûte d'une voisine, sans quoi les continents seraient grignotés sur leurs bords
                bool opening = false;
                for (int i = 0; i < distinct && !opening; i++)
                    opening = IsRetreatingFrom(cell, votePlates[i]);
                resolved.Add((cell, plate, opening ? -1 : PickDonor(cell, plate)));
            }

            // Application après coup, pour que l'ordre de parcours n'influence pas le résultat
            foreach (var (cell, plate, donor) in resolved)
            {
                _nextPlate[cell] = plate;
                if (donor < 0)
                {
                    SetNewOceanicCrust(cell);
                }
                else
                {
                    _nextAge[cell] = _nextAge[donor];
                    _nextContinental[cell] = _nextContinental[donor];
                    _nextThickness[cell] = _nextThickness[donor];
                    _sourceCell[cell] = -1;
                }
            }

            if (resolved.Count == 0)
            {
                // Sécurité (ne devrait pas arriver) : on garde l'ancienne plaque
                foreach (int cell in remaining)
                {
                    _nextPlate[cell] = CellPlate[cell];
                    SetNewOceanicCrust(cell);
                }
                break;
            }
            gaps = remaining;
        }
    }

    /// <summary>Vrai si la plaque, présente parmi les voisines de la cellule vide, s'éloigne
    /// d'elle (sa vitesse pointe à l'opposé de la cellule) : le vide est une vraie ouverture.</summary>
    private bool IsRetreatingFrom(int cell, int plate)
    {
        Vector3 position = Grid.Positions[cell];
        Vector3 centroid = Vector3.Zero;
        int count = 0;
        foreach (int neighbor in Grid.Neighbors[cell])
            if (_nextPlate[neighbor] == plate)
            {
                centroid += Grid.Positions[neighbor];
                count++;
            }
        if (count == 0)
            return false;

        // Direction de la cellule vers la plaque, dans le plan tangent
        Vector3 towardPlate = centroid / count - position;
        towardPlate -= position * towardPlate.Dot(position);
        if (towardPlate.LengthSquared() < 1e-14f)
            return false;   // trou entouré par la plaque : pas une ouverture

        Vector3 velocity = Plates[plate].VelocityAt(position);
        float speed = velocity.Length();
        return speed > 0f && velocity.Dot(towardPlate.Normalized()) > OpeningDirectionThreshold * speed;
    }

    /// <summary>Voisine de la même plaque dont on recopie la croûte pour boucher un trou
    /// (la plus épaisse, pour ne pas amincir les continents).</summary>
    private int PickDonor(int cell, int plate)
    {
        int donor = -1;
        foreach (int neighbor in Grid.Neighbors[cell])
            if (_nextPlate[neighbor] == plate && (donor < 0 || _nextThickness[neighbor] > _nextThickness[donor]))
                donor = neighbor;
        return donor;
    }

    /// <summary>
    /// Pour chaque arête séparant deux plaques, on compare la vitesse des deux plaques au même
    /// point. La composante normale à la frontière dit si elles se rapprochent ou s'écartent,
    /// la composante tangentielle si elles coulissent. Chaque cellule de bord garde la
    /// frontière la plus active parmi ses arêtes.
    /// </summary>
    public void ClassifyBoundaries()
    {
        ForEachCellParallel(ClassifyCell);
    }

    /// <summary>Classe les frontières d'une cellule ; n'écrit que dans ses propres cases.</summary>
    private void ClassifyCell(int a)
    {
        var positions = Grid.Positions;
        CellBoundary[a] = BoundaryType.None;
        CellBoundaryRate[a] = 0f;
        Plate plateA = Plates[CellPlate[a]];
        Vector3 pa = positions[a];

        foreach (int b in Grid.Neighbors[a])
        {
            if (CellPlate[b] == CellPlate[a])
                continue;

            Plate plateB = Plates[CellPlate[b]];
            Vector3 pb = positions[b];
            Vector3 midpoint = (pa + pb).Normalized();

            // Orientation de la frontière, lissée sur le voisinage (voir BoundaryNormal) puis
            // ramenée dans le plan tangent à la sphère
            Vector3 normal = BoundaryNormal(a, CellPlate[b]);
            normal = (normal - midpoint * normal.Dot(midpoint)).Normalized();

            // Mouvement de b vu depuis a
            Vector3 relative = plateB.VelocityAt(midpoint) - plateA.VelocityAt(midpoint);
            float normalRate = relative.Dot(normal);                 // > 0 : b s'éloigne de a
            float tangentialRate = (relative - normal * normalRate).Length();
            float rate = relative.Length() * PlanetConstants.CmPerYearPerRadiusPerMyr;

            if (rate <= CellBoundaryRate[a])
                continue;

            CellBoundaryRate[a] = rate;
            if (Mathf.Abs(normalRate) < TransformRatio * tangentialRate)
                CellBoundary[a] = BoundaryType.Transform;
            else
                CellBoundary[a] = normalRate < 0f ? BoundaryType.Convergent : BoundaryType.Divergent;
        }
    }

    /// <summary>
    /// Direction de la frontière entre la plaque de la cellule et une plaque voisine, vue depuis
    /// la cellule. Une arête isolée a une orientation presque aléatoire (le contour est en
    /// escalier) : on prend donc la différence entre le barycentre des cellules de l'autre plaque
    /// et celui des cellules de sa propre plaque, sur deux couronnes de voisines.
    /// </summary>
    private Vector3 BoundaryNormal(int cell, int otherPlate)
    {
        int ownPlate = CellPlate[cell];
        Vector3 ownSum = Grid.Positions[cell];
        Vector3 otherSum = Vector3.Zero;
        int ownCount = 1, otherCount = 0;

        foreach (int n1 in Grid.Neighbors[cell])
        {
            Accumulate(n1);
            foreach (int n2 in Grid.Neighbors[n1])
                if (n2 != cell)
                    Accumulate(n2);
        }

        return otherSum / otherCount - ownSum / ownCount;

        // Les cellules de la deuxième couronne sont comptées plusieurs fois, ce qui donne
        // naturellement plus de poids aux plus proches
        void Accumulate(int c)
        {
            if (CellPlate[c] == ownPlate) { ownSum += Grid.Positions[c]; ownCount++; }
            else if (CellPlate[c] == otherPlate) { otherSum += Grid.Positions[c]; otherCount++; }
        }
    }

    // --- Génération --------------------------------------------------------------------------

    /// <summary>Choisit des germes bien répartis : pour chaque germe on tire plusieurs candidats
    /// et on garde le plus éloigné des germes existants (évite les plaques minuscules).</summary>
    private static int[] PickSpreadSeeds(PlanetGrid grid, int count, Random random)
    {
        var seeds = new int[count];
        seeds[0] = random.Next(grid.CellCount);

        for (int i = 1; i < count; i++)
        {
            int best = -1;
            float bestDistance = -1f;
            for (int c = 0; c < SeedCandidates; c++)
            {
                int candidate = random.Next(grid.CellCount);
                // Distance au germe le plus proche ; sur la sphère unité, le produit scalaire
                // le plus grand correspond au point le plus proche
                float closestDot = -1f;
                for (int s = 0; s < i; s++)
                    closestDot = Mathf.Max(closestDot, grid.Positions[candidate].Dot(grid.Positions[seeds[s]]));
                float distance = 1f - closestDot;
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            seeds[i] = best;
        }
        return seeds;
    }

    /// <summary>
    /// Croissance aléatoire des plaques depuis leurs germes : à chaque itération on choisit une
    /// plaque (pondérée par un « appétit » propre, pour obtenir des tailles variées) et une
    /// cellule au hasard sur son front, qu'elle annexe. Les frontières obtenues sont irrégulières,
    /// et chaque plaque reste d'un seul tenant.
    /// </summary>
    private static int[] GrowPlates(PlanetGrid grid, Plate[] plates, Random random)
    {
        int cellCount = grid.CellCount;
        var cellPlate = new int[cellCount];
        Array.Fill(cellPlate, -1);

        var frontiers = new List<int>[plates.Length];
        var appetite = new float[plates.Length];
        int assigned = 0;

        for (int p = 0; p < plates.Length; p++)
        {
            // Appétit entre 0,15 et 1 (au carré) : quelques grandes plaques, beaucoup de petites
            float r = Mathf.Lerp(0.4f, 1f, random.NextSingle());
            appetite[p] = r * r;
            frontiers[p] = new List<int>();

            int seedCell = plates[p].SeedCell;
            cellPlate[seedCell] = p;
            plates[p].CellCount = 1;
            assigned++;
            frontiers[p].AddRange(grid.Neighbors[seedCell]);
        }

        while (assigned < cellCount)
        {
            int p = PickPlate(frontiers, appetite, random);
            if (p < 0)
                throw new InvalidOperationException("Plate growth stalled before covering the planet.");

            // Retrait d'une cellule au hasard du front (échange avec la dernière : O(1))
            var frontier = frontiers[p];
            int index = random.Next(frontier.Count);
            int cell = frontier[index];
            frontier[index] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);

            if (cellPlate[cell] != -1)
                continue;   // déjà prise par une autre plaque entre-temps

            cellPlate[cell] = p;
            plates[p].CellCount++;
            assigned++;

            foreach (int neighbor in grid.Neighbors[cell])
                if (cellPlate[neighbor] == -1)
                    frontier.Add(neighbor);
        }

        for (int pass = 0; pass < SmoothingPasses; pass++)
            cellPlate = SmoothBoundaries(grid, plates, cellPlate);

        // Recomptage après lissage
        foreach (var plate in plates)
            plate.CellCount = 0;
        foreach (int p in cellPlate)
            plates[p].CellCount++;

        return cellPlate;
    }

    /// <summary>Filtre majoritaire : une cellule dont plus de la moitié des voisines appartient
    /// à une autre plaque rejoint cette plaque. Supprime les dents et les isthmes d'une cellule.
    /// Les germes ne bougent jamais.</summary>
    private static int[] SmoothBoundaries(PlanetGrid grid, Plate[] plates, int[] cellPlate)
    {
        var result = (int[])cellPlate.Clone();
        var isSeed = new HashSet<int>();
        foreach (var plate in plates)
            isSeed.Add(plate.SeedCell);

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            if (isSeed.Contains(cell))
                continue;

            int[] neighbors = grid.Neighbors[cell];
            foreach (int n in neighbors)
            {
                int candidate = cellPlate[n];
                if (candidate == cellPlate[cell])
                    continue;

                int votes = 0;
                foreach (int m in neighbors)
                    if (cellPlate[m] == candidate)
                        votes++;

                if (votes * 2 > neighbors.Length)
                {
                    result[cell] = candidate;
                    break;
                }
            }
        }
        return result;
    }

    private static int PickPlate(List<int>[] frontiers, float[] appetite, Random random)
    {
        float total = 0f;
        for (int p = 0; p < frontiers.Length; p++)
            if (frontiers[p].Count > 0)
                total += appetite[p];
        if (total <= 0f)
            return -1;

        float pick = random.NextSingle() * total;
        int last = -1;
        for (int p = 0; p < frontiers.Length; p++)
        {
            if (frontiers[p].Count == 0)
                continue;
            last = p;
            pick -= appetite[p];
            if (pick <= 0f)
                return p;
        }
        return last;
    }

    /// <summary>Direction uniformément répartie sur la sphère.</summary>
    private static Vector3 RandomUnitVector(Random random)
    {
        float z = random.NextSingle() * 2f - 1f;
        float angle = random.NextSingle() * Mathf.Tau;
        float r = Mathf.Sqrt(1f - z * z);
        return new Vector3(r * Mathf.Cos(angle), r * Mathf.Sin(angle), z);
    }
}
