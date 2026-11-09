using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>Sorte de relief ponctuel, plus petit que les cellules de la simulation.</summary>
public enum LandformKind : byte
{
    /// <summary>Volcan bouclier de point chaud (pentes douces, caldeira), type Hawaï.</summary>
    ShieldVolcano,
    /// <summary>Volcan d'arc (stratovolcan conique, cratère), au-dessus d'une subduction.</summary>
    Stratovolcano,
    /// <summary>Cratère d'impact de météorite.</summary>
    ImpactCrater,
}

/// <summary>
/// Volcan ou cratère porté par une plaque (il voyage avec elle) : position, taille, âge.
/// <para>Hauteur actuelle = <see cref="PeakHeightM"/> × <see cref="Freshness"/> : les volcans
/// éteints s'érodent et s'enfoncent, les cratères s'effacent.</para>
/// </summary>
public sealed class Landform
{
    public LandformKind Kind { get; init; }
    /// <summary>Position (sphère unité, repère de la planète).</summary>
    public Vector3 Position { get; set; }
    /// <summary>Plaque qui le porte.</summary>
    public int Plate { get; set; }
    /// <summary>Rayon à la base (m) ; cratère : rayon du bord.</summary>
    public float RadiusM { get; init; }
    /// <summary>Volcan : hauteur au-dessus du terrain à son maximum (m) ; cratère : profondeur (m).</summary>
    public float PeakHeightM { get; set; }
    /// <summary>Instant de naissance (Ma, horloge géologique).</summary>
    public double BirthMyr { get; init; }
    /// <summary>Volcan actif (il peut entrer en éruption dans l'horloge climatique).</summary>
    public bool Active { get; set; }
    /// <summary>Fraîcheur (0 à 1) : 1 neuf, puis décroissance par l'érosion.</summary>
    public float Freshness { get; set; } = 1f;
    /// <summary>Croissance (0 à 1) d'un volcan actif, qui s'édifie coulée après coulée.</summary>
    public float Growth { get; set; }
    /// <summary>Cellule la plus proche (point de départ de la recherche au pas suivant : le relief
    /// bouge de moins d'une cellule par pas, la recherche est alors immédiate).</summary>
    public int Cell { get; set; }

    /// <summary>Copie figée (le relief détaillé est calculé en tâche de fond).</summary>
    public Landform Snapshot() => (Landform)MemberwiseClone();
}

/// <summary>
/// Étape 13 : reliefs ponctuels de la planète active.
/// <list type="bullet">
/// <item><b>Points chauds</b> : panaches fixes du manteau (comme sous Hawaï ou l'Islande). La plaque
/// glisse au-dessus ; un nouveau volcan naît quand l'ancien s'est assez éloigné : chaînes d'îles
/// de plus en plus vieilles, érodées puis englouties (monts sous-marins), dans le sens du
/// mouvement de la plaque. Le panache bombe aussi la croûte (léger épaississement).</item>
/// <item><b>Arcs volcaniques</b> : à ~150-250 km de la fosse, côté chevauchant d'une subduction
/// (Andes, Japon). Ils s'éteignent quand la subduction cesse.</item>
/// <item><b>Cratères d'impact</b> : taux actuel ~1,8 cratère de plus de 20 km par million
/// d'années sur une planète comme la Terre (Grieve et Shoemaker 1994), loi de taille
/// N(&gt;D) ∝ D^−1,8 ; bombardement bien plus intense dans la jeunesse de la planète (chronologie
/// lunaire de Neukum). Ils s'effacent par l'érosion et disparaissent en subduction.</item>
/// </list>
/// </summary>
public sealed partial class TectonicPlates
{
    // Points chauds
    private const int HotspotCount = 12;
    private const float HotspotSwellKmPerMyr = 0.6f;          // épaississement sous le panache
    // Volcans boucliers : du petit bouclier islandais (Skjaldbreiður : ~10 km de large, 600 m de
    // haut) au géant hawaïen (Mauna Loa : ~120 km de large, 4 km au-dessus des fonds) ; pente
    // moyenne ~4° (hauteur ≈ 7 % du rayon)
    private const float ShieldMinRadiusM = 12000f;
    private const float ShieldMaxRadiusM = 60000f;
    private const float ShieldAspect = 0.07f;
    private const float HotspotSpacingM = 84000f;             // distance entre volcans d'une chaîne
    private const float ShieldGrowthMyr = 1.5f;               // temps pour s'édifier

    // Arcs volcaniques
    private static readonly float CosArcSpacing = MathF.Cos(400f / PlanetConstants.RadiusKm);
    private const float StratoRadiusM = 15000f;
    private const float StratoHeightM = 2500f;
    private const float StratoGrowthMyr = 0.5f;

    // Érosion et affaissement des volcans éteints, effacement des cratères (temps caractéristiques)
    private const float VolcanoErosionMyr = 15f;
    private const float CraterErosionLandMyr = 60f;
    private const float CraterBurialOceanMyr = 100f;
    private const float MinFreshness = 0.03f;

    // Cratères : diamètre minimal suivi (plus petits : invisibles à l'échelle des cellules), maximal
    private const float MinCraterDiameterKm = 5f;
    private const float MaxCraterDiameterKm = 300f;
    private const float CraterSizeExponent = 1.8f;
    private const float CratersAbove20KmPerMyr = 1.8f;
    private const int MaxLandforms = 3000;
    private const double MaxImpactsPerStep = 30;
    // Âge de la planète (Ga) pour la chronologie des impacts : l'horloge géologique part de sa formation
    private const double PlanetAgeGa = 4.54;

    private readonly List<Landform> _landforms = new();
    private Vector3[] _hotspots = Array.Empty<Vector3>();
    private Random _landformRandom = new(1);

    /// <summary>Volcans et cratères actuels (à ne pas modifier).</summary>
    public IReadOnlyList<Landform> Landforms => _landforms;

    /// <summary>Panaches du manteau, fixes (sphère unité).</summary>
    public IReadOnlyList<Vector3> Hotspots => _hotspots;

    /// <summary>Nombre total de cratères creusés depuis le début.</summary>
    public int ImpactCount { get; private set; }

    /// <summary>Copies des reliefs ponctuels encore visibles, pour le relief détaillé.</summary>
    public Landform[] SnapshotLandforms()
    {
        var copies = new Landform[_landforms.Count];
        for (int i = 0; i < copies.Length; i++)
            copies[i] = _landforms[i].Snapshot();
        return copies;
    }

    /// <summary>Points chauds tirés avec leur propre générateur (le reste de la planète ne change pas).</summary>
    private void InitializeLandforms(int seed)
    {
        _landformRandom = new Random(seed * 7919 + 17);
        _hotspots = new Vector3[HotspotCount];
        for (int i = 0; i < HotspotCount; i++)
            _hotspots[i] = RandomUnitVector(_landformRandom);
    }

    /// <summary>Taux d'impacts relatif à aujourd'hui selon l'âge de la planète (Neukum et al. 2001) :
    /// dN/dT ∝ 6,93 × 5,44·10⁻¹⁴ e^(6,93 T) + 8,38·10⁻⁴, T en milliards d'années avant le présent.</summary>
    public static double ImpactRateFactor(double planetTimeMyr)
    {
        double t = Math.Max(0.0, PlanetAgeGa - planetTimeMyr / 1000.0);
        double now = 6.93 * 5.44e-14 + 8.38e-4;
        return (6.93 * 5.44e-14 * Math.Exp(6.93 * t) + 8.38e-4) / now;
    }

    /// <summary>Diamètre d'un cratère tiré selon N(&gt;D) ∝ D^−1,8 (u uniforme entre 0 et 1).</summary>
    public static float CraterDiameterKm(double u) =>
        (float)Math.Min(MaxCraterDiameterKm, MinCraterDiameterKm * Math.Pow(Math.Max(u, 1e-12), -1.0 / CraterSizeExponent));

    /// <summary>Profondeur d'un cratère frais (m) selon son diamètre (Pike 1977 : simple puis complexe).</summary>
    public static float CraterDepthM(float diameterKm) =>
        1000f * (diameterKm < 15f ? 0.196f * MathF.Pow(diameterKm, 1.01f) : 1.044f * MathF.Pow(diameterKm, 0.301f));

    /// <summary>Volcans et cratères pour un pas de dtMyr (appelé à la fin de chaque pas géologique).</summary>
    private void UpdateLandforms(float dtMyr)
    {
        MoveLandforms(dtMyr);
        UpdateHotspots(dtMyr);
        UpdateArcs(dtMyr);
        AddImpacts(dtMyr);
        AgeLandforms(dtMyr);
    }

    /// <summary>Chaque relief tourne avec sa plaque ; il disparaît si sa croûte a plongé.</summary>
    private void MoveLandforms(float dtMyr)
    {
        // Une rotation par plaque pour tout le pas
        if (_stepRotations.Length < Plates.Length)
            _stepRotations = new Quaternion[Plates.Length];
        for (int p = 0; p < Plates.Length; p++)
            _stepRotations[p] = Plates[p].AngularSpeed > 0f
                ? new Quaternion(Plates[p].EulerPole, Plates[p].AngularSpeed * dtMyr)
                : Quaternion.Identity;
        int write = 0;
        for (int i = 0; i < _landforms.Count; i++)
        {
            var landform = _landforms[i];
            landform.Position = _stepRotations[landform.Plate] * landform.Position;
            landform.Cell = Grid.NearestCell(landform.Position, landform.Cell);
            if (CellPlate[landform.Cell] == landform.Plate)
                _landforms[write++] = landform;   // sinon : sa croûte a plongé, il disparaît
        }
        _landforms.RemoveRange(write, _landforms.Count - write);
    }

    private Quaternion[] _stepRotations = Array.Empty<Quaternion>();

    private void UpdateHotspots(float dtMyr)
    {
        float cosSpacing = MathF.Cos(HotspotSpacingM / (PlanetConstants.RadiusKm * 1000f));
        // Volcan emporté loin de tout panache par sa plaque : il s'éteint
        foreach (var landform in _landforms)
            if (landform.Kind == LandformKind.ShieldVolcano && landform.Active)
            {
                bool fed = false;
                foreach (var hotspot in _hotspots)
                    if (landform.Position.Dot(hotspot) > cosSpacing)
                        fed = true;
                landform.Active = fed;
            }

        int hint = 0;
        foreach (var hotspot in _hotspots)
        {
            hint = Grid.NearestCell(hotspot, hint);
            Thicken(hint, HotspotSwellKmPerMyr * dtMyr);
            // Un seul volcan actif par panache : le plus jeune ; un nouveau naît quand il est parti
            Landform active = null;
            foreach (var landform in _landforms)
                if (landform.Kind == LandformKind.ShieldVolcano && landform.Active && landform.Position.Dot(hotspot) > cosSpacing
                    && (active == null || landform.BirthMyr > active.BirthMyr))
                    active = landform;
            foreach (var landform in _landforms)
                if (landform.Kind == LandformKind.ShieldVolcano && landform.Active && landform != active
                    && landform.Position.Dot(hotspot) > cosSpacing)
                    landform.Active = false;
            if (active == null)
            {
                // Taille tirée au hasard sur une échelle logarithmique (beaucoup de petits, peu de géants)
                float radius = ShieldMinRadiusM * MathF.Pow(ShieldMaxRadiusM / ShieldMinRadiusM, _landformRandom.NextSingle());
                AddLandform(new Landform
                {
                    Kind = LandformKind.ShieldVolcano, Position = hotspot, Plate = CellPlate[hint], Cell = hint,
                    RadiusM = radius,
                    PeakHeightM = radius * ShieldAspect * (0.8f + 0.4f * _landformRandom.NextSingle()),
                    BirthMyr = TimeMyr, Active = true,
                });
            }
        }
    }

    /// <summary>Volcans d'arc : un tous les ~400 km le long des frontières où une plaque plonge,
    /// sur la plaque chevauchante, une cellule en retrait de la frontière.</summary>
    private void UpdateArcs(float dtMyr)
    {
        // Les volcans d'arc restent actifs tant qu'une frontière convergente chevauchante est proche
        foreach (var landform in _landforms)
            if (landform.Kind == LandformKind.Stratovolcano && landform.Active)
            {
                int cell = landform.Cell;
                landform.Active = NearOverridingBoundary(cell);
            }

        // Cellules déjà occupées par un volcan d'arc actif (à moins de ~400 km) : marquées une
        // fois pour toutes, au lieu de comparer chaque frontière à tous les volcans
        _arcOccupied ??= new bool[Grid.CellCount];
        Array.Clear(_arcOccupied);
        foreach (var landform in _landforms)
            if (landform.Kind == LandformKind.Stratovolcano && landform.Active)
                MarkWithin(landform.Position, CosArcSpacing);

        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            if (CellBoundary[cell] != BoundaryType.Convergent || CellSubducting[cell])
                continue;
            // Une cellule vers l'intérieur de la plaque chevauchante
            int inland = -1;
            foreach (int n in Grid.Neighbors[cell])
                if (CellPlate[n] == CellPlate[cell] && CellBoundary[n] != BoundaryType.Convergent)
                {
                    inland = n;
                    break;
                }
            if (inland < 0)
                continue;
            Vector3 position = Grid.Positions[inland];
            bool crowded = _arcOccupied[inland];
            // Pas à chaque pas : en moyenne un nouveau volcan par site libre tous les ~2 Ma
            if (crowded || _landformRandom.NextSingle() > dtMyr / 2f)
                continue;
            // Position un peu décalée au hasard dans la cellule (pas d'alignement sur la grille)
            Vector3 jitter = RandomUnitVector(_landformRandom) * (0.3f * Grid.MeanEdgeAngle);
            MarkWithin(position, CosArcSpacing);
            AddLandform(new Landform
            {
                Kind = LandformKind.Stratovolcano,
                Position = (position + jitter - position * jitter.Dot(position)).Normalized(),
                Plate = CellPlate[inland], Cell = inland,
                RadiusM = StratoRadiusM * (0.7f + 0.6f * _landformRandom.NextSingle()),
                PeakHeightM = StratoHeightM * (0.6f + 0.8f * _landformRandom.NextSingle()),
                BirthMyr = TimeMyr, Active = true,
            });
        }
    }

    private bool[] _arcOccupied;

    /// <summary>Marque les cellules à moins d'un angle donné (cosinus) d'un point.</summary>
    private void MarkWithin(Vector3 position, float cosAngle)
    {
        int start = Grid.NearestCell(position, 0);
        var stack = new Stack<int>();
        stack.Push(start);
        _arcOccupied[start] = true;
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            foreach (int n in Grid.Neighbors[c])
                if (!_arcOccupied[n] && Grid.Positions[n].Dot(position) > cosAngle)
                {
                    _arcOccupied[n] = true;
                    stack.Push(n);
                }
        }
    }

    private bool NearOverridingBoundary(int cell)
    {
        if (CellBoundary[cell] == BoundaryType.Convergent && !CellSubducting[cell])
            return true;
        foreach (int n in Grid.Neighbors[cell])
            if (CellPlate[n] == CellPlate[cell] && CellBoundary[n] == BoundaryType.Convergent && !CellSubducting[n])
                return true;
        return false;
    }

    /// <summary>Impacts du pas : nombre tiré selon le taux du moment (loi de Poisson).</summary>
    private void AddImpacts(float dtMyr)
    {
        double ratePerMyr = CratersAbove20KmPerMyr * Math.Pow(20.0 / MinCraterDiameterKm, CraterSizeExponent)
                            * ImpactRateFactor(TimeMyr);
        // Bombardement des débuts (des centaines de milliers d'impacts par Ma) : la surface est
        // saturée, les petits cratères effacés aussitôt par les suivants. On ne garde alors que
        // les plus grands : diamètre minimal relevé pour ne pas dépasser ~30 cratères par pas
        double expected = ratePerMyr * dtMyr;
        double minDiameterScale = expected > MaxImpactsPerStep ? Math.Pow(expected / MaxImpactsPerStep, 1.0 / CraterSizeExponent) : 1.0;
        int count = Poisson(Math.Min(expected, MaxImpactsPerStep));
        for (int k = 0; k < count; k++)
        {
            Vector3 position = RandomUnitVector(_landformRandom);
            int cell = Grid.NearestCell(position, 0);
            float diameter = (float)Math.Min(MaxCraterDiameterKm, CraterDiameterKm(_landformRandom.NextDouble()) * minDiameterScale);
            AddLandform(new Landform
            {
                Kind = LandformKind.ImpactCrater, Position = position, Plate = CellPlate[cell], Cell = cell,
                RadiusM = diameter * 500f, PeakHeightM = CraterDepthM(diameter), BirthMyr = TimeMyr, Growth = 1f,
            });
            ImpactCount++;
        }
    }

    private int Poisson(double mean)
    {
        if (mean > 50)   // approximation normale (bombardement intense des débuts)
            return Math.Max(0, (int)Math.Round(mean + Math.Sqrt(mean) * NormalSample()));
        double limit = Math.Exp(-mean), product = _landformRandom.NextDouble();
        int k = 0;
        while (product > limit)
        {
            k++;
            product *= _landformRandom.NextDouble();
        }
        return k;
    }

    private double NormalSample()
    {
        double u1 = Math.Max(1e-12, _landformRandom.NextDouble()), u2 = _landformRandom.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Croissance des volcans actifs, érosion des volcans éteints, effacement des cratères.</summary>
    private void AgeLandforms(float dtMyr)
    {
        int hint = 0;
        for (int i = _landforms.Count - 1; i >= 0; i--)
        {
            var landform = _landforms[i];
            if (landform.Kind == LandformKind.ImpactCrater)
            {
                hint = landform.Cell;
                float time = CellElevation[hint] > 0f ? CraterErosionLandMyr : CraterBurialOceanMyr;
                if (CellElevation[hint] > 0f && AnnualPrecipitation != null)
                    time /= Mathf.Clamp(AnnualPrecipitation[hint] / ReferencePrecipitationMm, MinErosionFactor, MaxErosionFactor);
                landform.Freshness *= MathF.Exp(-dtMyr / time);
            }
            else if (landform.Active)
            {
                float growth = landform.Kind == LandformKind.ShieldVolcano ? ShieldGrowthMyr : StratoGrowthMyr;
                landform.Growth = MathF.Min(1f, landform.Growth + dtMyr / growth);
            }
            else
            {
                landform.Freshness *= MathF.Exp(-dtMyr / VolcanoErosionMyr);
            }
            if (landform.Freshness < MinFreshness)
                _landforms.RemoveAt(i);   // (rare : quelques reliefs par pas)
        }
    }

    /// <summary>Ajoute un relief ; au-delà du plafond, on retire d'un coup les 10 % de cratères et
    /// de volcans éteints les moins visibles (et pas un par un : trop lent pendant le bombardement).</summary>
    private void AddLandform(Landform landform)
    {
        _landforms.Add(landform);
        if (_landforms.Count <= MaxLandforms)
            return;
        var candidates = new List<(float score, Landform landform)>();
        foreach (var l in _landforms)
            if (!l.Active)
                candidates.Add((l.Freshness * l.RadiusM, l));
        candidates.Sort((a, b) => a.score.CompareTo(b.score));
        var removed = new HashSet<Landform>();
        for (int i = 0; i < Math.Min(candidates.Count, MaxLandforms / 10); i++)
            removed.Add(candidates[i].landform);
        _landforms.RemoveAll(removed.Contains);
    }
}
