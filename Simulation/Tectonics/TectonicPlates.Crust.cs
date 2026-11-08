using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>
/// Étape 2c : nature de la croûte, épaisseur et relief.
/// <list type="bullet">
/// <item>Croûte océanique : fine (~7 km) et dense. Elle naît chaude aux dorsales, à ~2,6 km
/// de profondeur, puis refroidit, se contracte et s'enfonce en vieillissant.</item>
/// <item>Croûte continentale : épaisse (~35 km) et légère. Elle flotte plus haut sur le manteau
/// et ne plonge jamais en subduction : en collision, elle s'empile et forme des montagnes.</item>
/// </list>
/// L'altitude découle de l'épaisseur par isostasie (principe d'Archimède) : comme un glaçon,
/// une croûte plus épaisse dépasse davantage.
/// </summary>
public sealed partial class TectonicPlates
{
    // Épaisseurs de référence (km)
    public const float OceanicThicknessKm = 7f;
    public const float ContinentalThicknessKm = 35f;
    private const float MaxThicknessKm = 80f;

    // Isostasie d'Airy : un km de croûte en plus soulève la surface de (ρm − ρc) / ρm km,
    // avec ρc = 2,8 et ρm = 3,3 g/cm³ (croûte continentale et manteau)
    private const float IsostasyFactor = (3.3f - 2.8f) / 3.3f;
    // Altitude d'un continent d'épaisseur normale (35 km)
    private const float ContinentalReferenceElevationKm = 0.4f;

    // Profondeur des océans selon l'âge (loi de Parsons et Sclater) : d = 2,6 + 0,33 √âge,
    // plafonnée vers 150 Ma (~6,6 km)
    private const float RidgeDepthKm = 2.6f;
    private const float AgeDepthCoefficient = 0.33f;
    private const float MaxAgeForDepthMyr = 150f;

    // Approfondissement des fosses océaniques là où une plaque plonge
    private const float TrenchDepthKm = 3f;

    // Épaississement aux frontières convergentes, côté chevauchant : km/Ma par cm/an de
    // convergence. À 7 cm/an, une chaîne passe de 35 à 70 km d'épaisseur en ~50 Ma (Andes)
    private const float OrogenyRateKmPerMyrPerCmYr = 0.1f;

    // Un arc volcanique océanique devient croûte continentale au-delà de cette épaisseur :
    // c'est ainsi que les continents grandissent au fil des âges
    private const float ArcToContinentThicknessKm = 20f;

    // Érosion non linéaire : dX/dt = −f X² / C, où X est l'excès d'épaisseur au-dessus de 35 km
    // et f le facteur climatique (précipitations). Plus un relief est haut, plus il s'use vite :
    // une chaîne de 70 km d'épaisseur (X = 35) perd la moitié de son excès en ~20 Ma, une vieille
    // chaîne de 40 km (X = 5) en ~140 Ma. C = 700 calibré pour une altitude moyenne des terres
    // proche de la Terre (~0,85 km) une fois l'érosion couplée au climat
    private const float ErosionConstantKmMyr = 700f;

    // Étalement latéral des reliefs continentaux (diffusion), en km²/Ma
    private const float DiffusivityKm2PerMyr = 600f;

    // Érosion pilotée par le climat (étape 7) : la vitesse d'érosion est multipliée par
    // P / 700 mm (borné entre 0,3 et 3) ; sous un climat de référence terrestre (~800 mm/an
    // sur les terres) on retrouve à peu près l'érosion d'origine
    private const float ReferencePrecipitationMm = 700f;
    private const float MinErosionFactor = 0.3f;
    private const float MaxErosionFactor = 3f;

    // Sédiments : une partie de la croûte érodée descend les pentes et se dépose dans les
    // plaines basses (jusqu'à 200 m d'altitude) puis sur les plateaux continentaux (jusqu'à
    // 150 m sous la mer) ; le reste part au large. Piège constaté : sans plafond, les dépôts
    // empilés transformaient toutes les plaines en plateaux
    private const float SedimentDepositFraction = 0.3f;
    private const float LowlandCeilingKm = 0.2f;
    private const float ShelfCeilingKm = -0.15f;
    private const int MaxSedimentPathCells = 64;

    // Répartition de l'épaississement autour d'une frontière convergente : la cellule de bord
    // reçoit tout, la première couronne 60 %, la deuxième 30 % (chaînes larges et plateaux)
    private const float OrogenyFirstRing = 0.6f;
    private const float OrogenySecondRing = 0.3f;

    // Structure des continents de départ : variations de socle (± km) et anciennes chaînes
    private const float BasementVariationKm = 6f;
    private const float AncientBeltThicknessKm = 18f;

    // En collision continentale, fraction de la croûte passée dessous qui s'ajoute à celle du dessus
    private const float CollisionAccretionFactor = 0.5f;

    // Écart d'âge en dessous duquel deux croûtes océaniques sont considérées comme équivalentes
    private const float SubductionAgeToleranceMyr = 1f;

    // Continents de départ
    private const float InitialContinentalFraction = 0.3f;
    private const float InitialContinentalAgeMyr = 1000f;
    private const float CoastalRampKm = 400f;

    /// <summary>Vrai si la cellule porte de la croûte continentale.</summary>
    public bool[] CellContinental { get; }

    /// <summary>Épaisseur de la croûte (km).</summary>
    public float[] CellThickness { get; }

    /// <summary>Altitude par rapport au niveau de la mer (km).</summary>
    public float[] CellElevation { get; }

    /// <summary>Vrai si la cellule est en train de plonger sous une autre plaque (fosse).</summary>
    public bool[] CellSubducting { get; }

    /// <summary>Précipitations annuelles de chaque cellule (mm/an), fournies par une estimation
    /// du climat ; null tant qu'aucune n'est disponible (érosion de référence).</summary>
    public float[] AnnualPrecipitation { get; set; }

    /// <summary>Croûte déposée par les sédiments depuis le début (km × cellules), pour le suivi.</summary>
    public double SedimentDeposited { get; private set; }

    /// <summary>Nombre de collisions continentales ayant soudé deux plaques.</summary>
    public int SutureCount { get; private set; }

    // Compteurs de diagnostic (cellules de croûte continentale avalées en collision,
    // cellules d'arc océanique devenues continentales)
    public int CollisionConsumedCells { get; private set; }
    public int ArcConvertedCells { get; private set; }

    private readonly bool[] _referenceContinental;
    private readonly float[] _referenceThickness;
    private readonly bool[] _nextContinental;
    private readonly float[] _nextThickness;

    // Cellules de référence déjà « avalées » par une collision depuis la dernière référence :
    // leur croûte ne doit être ajoutée qu'une fois, alors que le recouvrement se répète à chaque pas
    private readonly bool[] _consumed;

    /// <summary>Part de la surface couverte de croûte continentale.</summary>
    public float ContinentalFraction
    {
        get
        {
            int count = 0;
            foreach (bool continental in CellContinental)
                if (continental)
                    count++;
            return (float)count / CellContinental.Length;
        }
    }

    // --- Continents de départ ----------------------------------------------------------------

    /// <summary>
    /// Sème quelques continents qui grandissent au hasard jusqu'à couvrir ~30 % de la surface,
    /// indépendamment des plaques (comme sur Terre, une frontière peut traverser un continent).
    /// L'épaisseur croît de la côte (~20 km, plateau continental immergé) vers l'intérieur (~35 km).
    /// </summary>
    private void InitializeContinents()
    {
        int cellCount = Grid.CellCount;
        int target = (int)(InitialContinentalFraction * cellCount);
        int seedCount = 5 + _random.Next(4);

        var frontier = new List<int>();
        int assigned = 0;
        for (int i = 0; i < seedCount; i++)
        {
            int seed = _random.Next(cellCount);
            if (CellContinental[seed])
                continue;
            CellContinental[seed] = true;
            assigned++;
            frontier.AddRange(Grid.Neighbors[seed]);
        }

        while (assigned < target && frontier.Count > 0)
        {
            int index = _random.Next(frontier.Count);
            int cell = frontier[index];
            frontier[index] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (CellContinental[cell])
                continue;
            CellContinental[cell] = true;
            assigned++;
            foreach (int neighbor in Grid.Neighbors[cell])
                if (!CellContinental[neighbor])
                    frontier.Add(neighbor);
        }

        // Distance à la côte (en cellules) par parcours en largeur depuis l'océan
        var distance = new int[cellCount];
        Array.Fill(distance, int.MaxValue);
        var queue = new Queue<int>();
        for (int cell = 0; cell < cellCount; cell++)
            if (!CellContinental[cell])
            {
                distance[cell] = 0;
                queue.Enqueue(cell);
            }
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            foreach (int neighbor in Grid.Neighbors[cell])
                if (distance[neighbor] == int.MaxValue)
                {
                    distance[neighbor] = distance[cell] + 1;
                    queue.Enqueue(neighbor);
                }
        }

        // Un continent n'est pas plat : bassins et plateaux (bruit fractal), plus quelques
        // anciennes chaînes usées héritées d'un passé plus lointain (bruit en crêtes)
        float edgeKm = Grid.MeanEdgeAngle * PlanetConstants.RadiusKm;
        var noise = new SphereNoise(_random.Next());
        for (int cell = 0; cell < cellCount; cell++)
        {
            if (CellContinental[cell])
            {
                float ramp = Mathf.SmoothStep(0f, CoastalRampKm, distance[cell] * edgeKm);
                Vector3 p = Grid.Positions[cell];
                float basement = BasementVariationKm * noise.Fractal(p, 3f, 4);
                float belt = AncientBeltThicknessKm * Mathf.SmoothStep(0.72f, 0.95f, noise.Ridged(p, 2.5f, 3));
                float thickness = 20f + 15f * ramp + ramp * (basement + belt);
                CellThickness[cell] = Mathf.Clamp(thickness, 18f, MaxThicknessKm);
                CellCrustAge[cell] = InitialContinentalAgeMyr;
            }
            else
            {
                CellThickness[cell] = OceanicThicknessKm;
            }
        }

        Array.Copy(CellContinental, _referenceContinental, cellCount);
        Array.Copy(CellThickness, _referenceThickness, cellCount);
        Array.Copy(CellCrustAge, _referenceAge, cellCount);
    }

    // --- Règles de collision -----------------------------------------------------------------

    /// <summary>
    /// Quand deux croûtes de référence se disputent une cellule, dit si <paramref name="candidate"/>
    /// reste en surface plutôt que <paramref name="current"/> :
    /// la croûte continentale l'emporte toujours sur l'océanique (trop légère pour plonger) ;
    /// entre deux océaniques, la plus jeune (plus chaude, moins dense) reste dessus ;
    /// entre deux continentales, ou à âge égal, la plaque déjà en place garde la cellule.
    /// </summary>
    private bool Overrides(int candidate, int current, bool candidateIsIncumbent)
    {
        bool candidateContinental = _referenceContinental[candidate];
        bool currentContinental = _referenceContinental[current];
        if (candidateContinental != currentContinental)
            return candidateContinental;
        if (candidateContinental)
            return candidateIsIncumbent;

        float candidateAge = _referenceAge[candidate];
        float currentAge = _referenceAge[current];
        if (Mathf.Abs(candidateAge - currentAge) <= SubductionAgeToleranceMyr)
            return candidateIsIncumbent;
        return candidateAge < currentAge;
    }

    /// <summary>Collision continentale : la croûte du dessous ne disparaît pas dans le manteau,
    /// une partie s'ajoute à celle du dessus et l'épaissit.</summary>
    private void AccreteIfContinental(int winner, int loser)
    {
        if (!_referenceContinental[loser] || !_referenceContinental[winner] || _consumed[loser])
            return;
        _consumed[loser] = true;
        CollisionConsumedCells++;
        _referenceThickness[winner] = Mathf.Min(MaxThicknessKm,
            _referenceThickness[winner] + CollisionAccretionFactor * _referenceThickness[loser]);
    }

    private void SetNewOceanicCrust(int cell)
    {
        _nextAge[cell] = 0f;
        _nextContinental[cell] = false;
        _nextThickness[cell] = OceanicThicknessKm;
        _sourceCell[cell] = -1;
    }

    // --- Évolution de la croûte --------------------------------------------------------------

    /// <summary>Montagnes aux frontières convergentes, érosion et étalement des reliefs.</summary>
    private void UpdateCrust(float dtMyr)
    {
        int cellCount = Grid.CellCount;
        var ring1 = new List<int>(6);
        var ring2 = new HashSet<int>();

        // 1. Frontières convergentes : côté chevauchant, la croûte s'épaissit (arc volcanique,
        //    chaîne de montagnes) ; côté plongeant, une fosse se creuse
        for (int cell = 0; cell < cellCount; cell++)
        {
            CellSubducting[cell] = false;
            if (CellBoundary[cell] != BoundaryType.Convergent)
                continue;

            bool overriding = false;
            foreach (int neighbor in Grid.Neighbors[cell])
                if (CellPlate[neighbor] != CellPlate[cell] && OverridesCurrent(cell, neighbor))
                {
                    overriding = true;
                    break;
                }

            if (!overriding)
            {
                CellSubducting[cell] = true;
                continue;
            }

            float uplift = OrogenyRateKmPerMyrPerCmYr * CellBoundaryRate[cell] * dtMyr;
            Thicken(cell, uplift);
            ring1.Clear();
            ring2.Clear();
            foreach (int neighbor in Grid.Neighbors[cell])
                if (CellPlate[neighbor] == CellPlate[cell])
                    ring1.Add(neighbor);
            foreach (int first in ring1)
                foreach (int second in Grid.Neighbors[first])
                    if (second != cell && CellPlate[second] == CellPlate[cell] && !ring1.Contains(second))
                        ring2.Add(second);
            foreach (int neighbor in ring1)
                Thicken(neighbor, OrogenyFirstRing * uplift);
            foreach (int neighbor in ring2)
                Thicken(neighbor, OrogenySecondRing * uplift);
        }

        // 2. Érosion non linéaire, intégrée exactement sur le pas : X' = X / (1 + f·X·dt / C),
        //    f dépendant des précipitations ; une partie du matériau érodé se dépose en aval
        float[] precipitation = AnnualPrecipitation;
        for (int cell = 0; cell < cellCount; cell++)
            if (CellContinental[cell] && CellThickness[cell] > ContinentalThicknessKm)
            {
                float factor = precipitation == null
                    ? 1f
                    : Mathf.Clamp(precipitation[cell] / ReferencePrecipitationMm, MinErosionFactor, MaxErosionFactor);
                float excess = CellThickness[cell] - ContinentalThicknessKm;
                float eroded = excess / (1f + factor * excess * dtMyr / ErosionConstantKmMyr);
                float removed = excess - eroded;
                Thicken(cell, -removed);
                if (precipitation != null && removed > 0f)
                    DepositSediment(cell, SedimentDepositFraction * removed);
            }

        // 3. Étalement latéral entre cellules continentales voisines (diffusion)
        float edgeKm = Grid.MeanEdgeAngle * PlanetConstants.RadiusKm;
        float alpha = Mathf.Min(0.2f, DiffusivityKm2PerMyr * dtMyr / (edgeKm * edgeKm));
        var delta = new float[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            if (!CellContinental[cell])
                continue;
            float sum = 0f;
            int count = 0;
            foreach (int neighbor in Grid.Neighbors[cell])
                if (CellContinental[neighbor])
                {
                    sum += CellThickness[neighbor] - CellThickness[cell];
                    count++;
                }
            if (count > 0)
                delta[cell] = alpha * sum / Grid.Neighbors[cell].Length;
        }
        for (int cell = 0; cell < cellCount; cell++)
            if (delta[cell] != 0f)
                Thicken(cell, delta[cell]);
    }

    /// <summary>
    /// Transporte des sédiments depuis une cellule en suivant la plus forte pente. Ils comblent
    /// les plaines basses et les cuvettes jusqu'à 200 m d'altitude, puis les fonds marins côtiers
    /// jusqu'à 150 m de profondeur (plateaux continentaux, deltas) ; ce qui reste part au large.
    /// </summary>
    private void DepositSediment(int source, float thicknessKm)
    {
        int cell = source;
        float remaining = thicknessKm;
        for (int step = 0; step < MaxSedimentPathCells && remaining > 1e-6f; step++)
        {
            if (cell != source)
            {
                float elevation = CellElevation[cell];
                float ceiling = elevation > 0f ? LowlandCeilingKm : ShelfCeilingKm;
                float room = (ceiling - elevation) / IsostasyFactor;
                if (room > 0f)
                {
                    float deposit = MathF.Min(room, remaining);
                    Thicken(cell, deposit);
                    CellElevation[cell] += deposit * IsostasyFactor;
                    SedimentDeposited += deposit;
                    remaining -= deposit;
                }
                if (elevation <= 0f && remaining > 0f && room <= 0f)
                    return;   // fond marin déjà comblé : le reste part au large
            }

            int lowest = cell;
            foreach (int neighbor in Grid.Neighbors[cell])
                if (CellElevation[neighbor] < CellElevation[lowest])
                    lowest = neighbor;
            if (lowest == cell)
                return;
            cell = lowest;
        }
    }

    /// <summary>Même règle que <see cref="Overrides"/>, sur l'état courant.</summary>
    private bool OverridesCurrent(int cell, int other)
    {
        if (CellContinental[cell] != CellContinental[other])
            return CellContinental[cell];
        if (CellContinental[cell])
            return true;   // collision continentale : les deux côtés se soulèvent
        float age = CellCrustAge[cell], otherAge = CellCrustAge[other];
        if (Mathf.Abs(age - otherAge) <= SubductionAgeToleranceMyr)
            return CellPlate[cell] < CellPlate[other];
        return age < otherAge;
    }

    /// <summary>Modifie l'épaisseur d'une cellule, dans l'état courant et dans la référence
    /// (sinon le prochain pas, recalculé depuis la référence, effacerait la modification).</summary>
    private void Thicken(int cell, float deltaKm)
    {
        int source = _sourceCell[cell];
        if (source < 0)
            return;   // croûte neuve, recréée à chaque pas jusqu'à la prochaine référence

        float thickness = Mathf.Clamp(_referenceThickness[source] + deltaKm, OceanicThicknessKm, MaxThicknessKm);
        _referenceThickness[source] = thickness;
        CellThickness[cell] = thickness;

        if (!_referenceContinental[source] && thickness >= ArcToContinentThicknessKm)
        {
            _referenceContinental[source] = true;
            CellContinental[cell] = true;
            ArcConvertedCells++;
        }
    }

    // --- Nettoyage des côtes -----------------------------------------------------------------

    /// <summary>
    /// Le rééchantillonnage laisse des confettis sur les côtes : des lacs d'une cellule au milieu
    /// des continents et des îlots d'une cellule en pleine mer. On les supprime, sauf les îlots
    /// des frontières convergentes, qui sont de vrais arcs volcaniques en formation.
    /// Appelé à chaque nouvel état de référence, sur l'état courant.
    /// </summary>
    private void CleanUpCoastlines()
    {
        var toContinental = new List<int>();
        var toOceanic = new List<int>();

        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            int[] neighbors = Grid.Neighbors[cell];
            int continentalNeighbors = 0;
            foreach (int neighbor in neighbors)
                if (CellContinental[neighbor])
                    continentalNeighbors++;

            if (!CellContinental[cell] && continentalNeighbors >= neighbors.Length - 1)
                toContinental.Add(cell);
            else if (CellContinental[cell] && continentalNeighbors == 0
                     && CellBoundary[cell] != BoundaryType.Convergent)
                toOceanic.Add(cell);
        }

        // Lac comblé : croûte moyenne des continents voisins
        foreach (int cell in toContinental)
        {
            float thickness = 0f, age = 0f;
            int count = 0;
            foreach (int neighbor in Grid.Neighbors[cell])
                if (CellContinental[neighbor])
                {
                    thickness += CellThickness[neighbor];
                    age = Mathf.Max(age, CellCrustAge[neighbor]);
                    count++;
                }
            CellContinental[cell] = true;
            CellThickness[cell] = thickness / count;
            CellCrustAge[cell] = age;
        }

        // Îlot noyé : croûte océanique de l'âge moyen des fonds voisins
        foreach (int cell in toOceanic)
        {
            float age = 0f;
            int count = 0;
            foreach (int neighbor in Grid.Neighbors[cell])
                if (!CellContinental[neighbor])
                {
                    age += CellCrustAge[neighbor];
                    count++;
                }
            CellContinental[cell] = false;
            CellThickness[cell] = OceanicThicknessKm;
            CellCrustAge[cell] = count > 0 ? age / count : 0f;
        }
    }

    // --- Relief ------------------------------------------------------------------------------

    /// <summary>Calcule l'altitude de chaque cellule à partir de sa croûte (en parallèle).</summary>
    public void UpdateElevation() => ForEachCellParallel(UpdateCellElevation);

    private void UpdateCellElevation(int cell)
    {
        float elevation;
        if (CellContinental[cell])
        {
            elevation = ContinentalReferenceElevationKm
                        + (CellThickness[cell] - ContinentalThicknessKm) * IsostasyFactor;
        }
        else
        {
            float age = Mathf.Min(CellCrustAge[cell], MaxAgeForDepthMyr);
            elevation = -(RidgeDepthKm + AgeDepthCoefficient * Mathf.Sqrt(age));
            // Arc volcanique en formation : la croûte épaissie remonte
            elevation += (CellThickness[cell] - OceanicThicknessKm) * IsostasyFactor;
            if (CellSubducting[cell])
                elevation -= TrenchDepthKm;
        }
        CellElevation[cell] = elevation;
    }
}
