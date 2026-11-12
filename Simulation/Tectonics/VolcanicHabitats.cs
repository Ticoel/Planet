using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>Type de milieu hydrothermal.</summary>
public enum HabitatKind
{
    /// <summary>Fumerolles : évents de gaz brûlants (vapeur, soufre), sol acide tapissé de soufre.</summary>
    Fumarole,
    /// <summary>Source chaude : bassin d'eau chaude riche en minéraux, bordé de tapis microbiens.</summary>
    HotSpring,
    /// <summary>Mare de boue : peu d'eau, très acide, l'argile bouillonne.</summary>
    MudPot,
    /// <summary>Geyser : source chaude dont le conduit étroit fait jaillir l'eau par intermittence.</summary>
    Geyser,
    /// <summary>Évents côtiers : gaz et eau chaude sous quelques dizaines de mètres de mer.</summary>
    ShallowVent,
    /// <summary>Fumeur noir : source sous-marine à ~350 °C, acide, chargée de sulfures métalliques.</summary>
    BlackSmoker,
    /// <summary>Source alcaline (type « Lost City ») : tiède, très basique, riche en hydrogène.</summary>
    AlkalineVent,
}

/// <summary>Un milieu hydrothermal : un lieu précis, avec les conditions qu'y trouverait la vie.</summary>
public sealed class Habitat
{
    public HabitatKind Kind { get; init; }
    /// <summary>Position (sphère unité, repère de la planète).</summary>
    public Vector3 Position { get; init; }
    /// <summary>Cellule de la grille qui le contient.</summary>
    public int Cell { get; init; }
    /// <summary>Température du fluide à la sortie (°C).</summary>
    public float TemperatureC { get; init; }
    public float Ph { get; init; }
    /// <summary>Rayon du bassin, du champ de fumerolles ou du site (m).</summary>
    public float RadiusM { get; init; }
    /// <summary>Altitude approximative (m ; négative : profondeur sous la mer).</summary>
    public float AltitudeM { get; init; }
    /// <summary>Geyser : durée entre deux éruptions (minutes) et hauteur du jet (m).</summary>
    public float PeriodMinutes { get; init; }
    public float JetHeightM { get; init; }
    /// <summary>Nombre propre au milieu (0 à 1), pour varier son aspect.</summary>
    public float Variation { get; init; }

    public bool Submarine => Kind is HabitatKind.ShallowVent or HabitatKind.BlackSmoker or HabitatKind.AlkalineVent;

    /// <summary>Geyser : intensité du jet (0 : repos, 1 : pleine éruption) à un instant (minutes).</summary>
    public float GeyserActivity(double minutes)
    {
        if (Kind != HabitatKind.Geyser || PeriodMinutes <= 0)
            return 0f;
        double phase = minutes / PeriodMinutes + Variation;
        phase -= Math.Floor(phase);
        // Éruption de ~4 minutes : montée rapide, puis le jet retombe
        double duration = Math.Min(0.5, VolcanicHabitats.GeyserEruptionMinutes / PeriodMinutes);
        if (phase >= duration)
            return 0f;
        double t = phase / duration;
        return (float)(Math.Min(1.0, t * 8.0) * Math.Pow(1 - t, 0.6));
    }
}

/// <summary>
/// Milieux hydrothermaux de la planète (étape 14a), là où la chaleur du sous-sol rencontre l'eau.
/// Ce sont des oasis chimiques : eau liquide, chaleur, minéraux et gradients de pH, donc des
/// berceaux possibles de la vie (sources alcalines : Martin et Russell 2007 ; mares chaudes des
/// terres émergées : Mulkidjanian et al. 2012 ; Damer et Deamer 2020).
/// <list type="bullet">
/// <item><b>Sur les volcans émergés</b> : fumerolles près du sommet (gaz à 100-450 °C, pH 1-2,5) ;
/// plus bas, là où l'eau de pluie s'infiltre et ressort réchauffée, sources chaudes (40 °C à
/// l'ébullition ; neutres à alcalines le plus souvent, parfois acides), geysers (rares : il faut
/// beaucoup d'eau et un conduit étroit) et mares de boue acides là où l'eau manque. L'eau bout à
/// plus basse température en altitude (~1 °C de moins tous les 300 m).</item>
/// <item><b>Sous la mer</b> : fumeurs noirs à ~350 °C le long des dorsales (plaques qui
/// s'écartent) et sur les volcans sous-marins ; à l'écart de l'axe, sources alcalines tièdes ;
/// évents côtiers à faible profondeur.</item>
/// </list>
/// Les milieux d'un volcan dépendent d'une graine propre à ce volcan : ils restent les mêmes tant
/// que le volcan vit, et le suivent quand sa plaque le déplace. Un volcan éteint depuis peu garde
/// quelques sources tièdes.
/// </summary>
public sealed class VolcanicHabitats
{
    public const double GeyserEruptionMinutes = 4.0;
    // Pluie (mm/an) en dessous de laquelle il n'y a pas assez d'eau pour une source, un geyser
    private const float SpringRainMm = 400f, MudRainMm = 150f;
    private const float DeepVentDepthM = 200f;

    public IReadOnlyList<Habitat> All => _all;
    private readonly List<Habitat> _all = new();
    private readonly int[][] _byCell;
    private readonly int[] _counts = new int[Enum.GetValues<HabitatKind>().Length];

    public int Count(HabitatKind kind) => _counts[(int)kind];
    public int LandCount => _all.Count(h => !h.Submarine);
    public int SubmarineCount => _all.Count(h => h.Submarine);

    /// <summary>Milieux d'une cellule (indices dans <see cref="All"/>), ou tableau vide.</summary>
    public ReadOnlySpan<int> InCell(int cell) => _byCell[cell] ?? ReadOnlySpan<int>.Empty;

    /// <summary>Température d'ébullition de l'eau à une altitude (°C).</summary>
    public static float BoilingPoint(float altitudeM) => 100f - Math.Max(0f, altitudeM) / 300f;

    public static string NameOf(HabitatKind kind, bool plural = false) => kind switch
    {
        HabitatKind.Fumarole => "fumerolles",
        HabitatKind.HotSpring => plural ? "sources chaudes" : "source chaude",
        HabitatKind.MudPot => plural ? "mares de boue" : "mare de boue",
        HabitatKind.Geyser => plural ? "geysers" : "geyser",
        HabitatKind.ShallowVent => plural ? "évents côtiers" : "évent côtier",
        HabitatKind.BlackSmoker => plural ? "fumeurs noirs" : "fumeur noir",
        _ => plural ? "sources alcalines" : "source alcaline",
    };

    /// <param name="annualRainMm">Pluie annuelle par cellule (mm/an), ou null (planète supposée arrosée).</param>
    /// <param name="abundance">Abondance par rapport à la Terre (1 = normale, 0 = aucun milieu).</param>
    public VolcanicHabitats(TectonicPlates tectonics, float[] annualRainMm, double abundance = 1.0)
    {
        var grid = tectonics.Grid;
        _byCell = new int[grid.CellCount][];
        if (abundance > 0)
        {
            foreach (var landform in tectonics.Landforms)
                if (landform.Kind != LandformKind.ImpactCrater && (landform.Active || landform.Freshness > 0.6f))
                    AddVolcano(tectonics, landform, annualRainMm, abundance);
            AddRidges(tectonics, abundance);
        }
        var lists = new List<int>[grid.CellCount];
        for (int i = 0; i < _all.Count; i++)
        {
            (lists[_all[i].Cell] ??= new()).Add(i);
            _counts[(int)_all[i].Kind]++;
        }
        for (int c = 0; c < lists.Length; c++)
            _byCell[c] = lists[c]?.ToArray();
    }

    private static int SeedOf(Landform landform) =>
        (int)(BitConverter.DoubleToInt64Bits(landform.BirthMyr) ^ (long)(landform.RadiusM * 613) ^ (long)landform.Kind * 104729);

    private void AddVolcano(TectonicPlates tectonics, Landform volcano, float[] annualRainMm, double abundance)
    {
        var random = new Random(SeedOf(volcano));
        var grid = tectonics.Grid;
        bool shield = volcano.Kind == LandformKind.ShieldVolcano;
        Vector3 up = volcano.Position.Normalized();
        Vector3 east = (MathF.Abs(up.Y) < 0.99f ? Vector3.Up : Vector3.Right).Cross(up).Normalized();
        Vector3 north = up.Cross(east);
        float baseAltitude = tectonics.CellElevation[volcano.Cell] * 1000f;
        float rain = annualRainMm != null ? annualRainMm[volcano.Cell] : 800f;

        // Tirages toujours dans le même ordre : les milieux d'un volcan ne changent pas d'une
        // génération à l'autre (seule leur nature suit le climat et le niveau de la mer)
        int sites = volcano.Active ? 4 + random.Next(5) : 1 + random.Next(3);
        // (abondance : on garde les premiers tirages, on en ajoute au-delà de 1)
        int kept = (int)Math.Round(sites * abundance);
        for (int k = 0; k < Math.Max(sites, kept); k++)
        {
            double kindDraw = random.NextDouble(), radial = random.NextDouble(), angle = random.NextDouble() * 2 * Math.PI;
            double heat = random.NextDouble(), acidity = random.NextDouble(), size = random.NextDouble();
            float variation = (float)random.NextDouble();
            double period = random.NextDouble(), jet = random.NextDouble();
            if (k >= kept)
                continue;

            // Le premier milieu d'un volcan actif : les fumerolles du cratère
            bool summit = volcano.Active && k == 0;
            double q = summit ? 0.012 : 0.25 + 0.8 * radial;
            float profile = q >= 1 ? 0f : shield ? MathF.Pow(1f - (float)(q * q), 1.5f) : MathF.Pow(1f - (float)q, 1.6f);
            float altitude = baseAltitude + volcano.PeakHeightM * volcano.Growth * volcano.Freshness * profile;
            float distance = (float)(q * volcano.RadiusM / (PlanetConstants.RadiusKm * 1000.0));
            Vector3 position = (up + (east * (float)Math.Cos(angle) + north * (float)Math.Sin(angle)) * distance).Normalized();
            int cell = grid.NearestCell(position, volcano.Cell);
            // (au pied du volcan, l'altitude est celle de la cellule où l'on arrive)
            if (q >= 1)
                altitude = tectonics.CellElevation[cell] * 1000f;

            HabitatKind kind;
            float temperature, ph, radius;
            if (altitude < 0)
            {
                bool deep = altitude < -DeepVentDepthM;
                kind = deep ? HabitatKind.BlackSmoker : HabitatKind.ShallowVent;
                temperature = deep ? 300f + 100f * (float)heat : 60f + 80f * (float)heat;
                ph = deep ? 2.8f + 1.4f * (float)acidity : 5f + 1.5f * (float)acidity;
                radius = 20f + 60f * (float)size;
            }
            else
            {
                float boiling = BoilingPoint(altitude);
                if (summit || rain < MudRainMm || !volcano.Active && kindDraw > 0.85)
                    kind = summit || volcano.Active ? HabitatKind.Fumarole : HabitatKind.HotSpring;
                else if (rain < SpringRainMm)
                    kind = kindDraw < 0.6 ? HabitatKind.MudPot : HabitatKind.Fumarole;
                else
                    kind = kindDraw < 0.65 ? HabitatKind.HotSpring
                        : kindDraw < 0.77 && volcano.Active ? HabitatKind.Geyser
                        : kindDraw < 0.9 ? HabitatKind.MudPot : HabitatKind.Fumarole;
                // Volcan éteint : il ne reste que des sources tièdes
                if (!volcano.Active)
                    kind = rain >= MudRainMm ? HabitatKind.HotSpring : HabitatKind.Fumarole;
                switch (kind)
                {
                    case HabitatKind.Fumarole:
                        temperature = volcano.Active ? 95f + 355f * (float)heat : 60f + 40f * (float)heat;
                        ph = 1f + 1.5f * (float)acidity;
                        radius = 40f + 100f * (float)size;
                        break;
                    case HabitatKind.MudPot:
                        temperature = Math.Min(boiling, 75f + 25f * (float)heat);
                        ph = 2f + 2f * (float)acidity;
                        radius = 8f + 12f * (float)size;
                        break;
                    case HabitatKind.Geyser:
                        temperature = boiling - 1f;
                        ph = 8f + 1.5f * (float)acidity;
                        radius = 6f + 6f * (float)size;
                        break;
                    default:
                        temperature = Math.Min(boiling - 2f, (volcano.Active ? 40f : 28f) + (volcano.Active ? 55f : 30f) * (float)heat);
                        // Le plus souvent neutre à alcaline (eaux chlorurées) ; une sur cinq, acide (eaux sulfatées)
                        ph = acidity < 0.2 ? 2.5f + 10f * (float)acidity : 6.5f + 3.1f * (float)(acidity - 0.2);
                        radius = 12f + 33f * (float)size;
                        break;
                }
            }
            _all.Add(new Habitat
            {
                Kind = kind, Position = position, Cell = cell, TemperatureC = temperature, Ph = ph, RadiusM = radius,
                AltitudeM = altitude, Variation = variation,
                PeriodMinutes = kind == HabitatKind.Geyser ? 25f + 155f * (float)period : 0f,
                JetHeightM = kind == HabitatKind.Geyser ? 15f + 55f * (float)jet : 0f,
            });
        }
    }

    /// <summary>Dorsales : fumeurs noirs sur l'axe, sources alcalines sur les flancs.</summary>
    private void AddRidges(TectonicPlates tectonics, double abundance)
    {
        var grid = tectonics.Grid;
        for (int c = 0; c < grid.CellCount; c++)
        {
            float elevation = tectonics.CellElevation[c];
            if (elevation >= 0)
                continue;
            // Graine par cellule : la répartition ne clignote pas d'un calcul à l'autre
            var random = new Random(c * 7919 + 17);
            double draw = random.NextDouble(), heat = random.NextDouble(), acidity = random.NextDouble();
            float variation = (float)random.NextDouble();
            Vector3 offset = new((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
            Vector3 position = (grid.Positions[c] + offset * (float)(0.35 * grid.MeanEdgeAngle)).Normalized();
            bool axis = tectonics.CellBoundary[c] == BoundaryType.Divergent;
            bool flank = !axis && grid.Neighbors[c].Any(n => tectonics.CellBoundary[n] == BoundaryType.Divergent);
            if (axis && draw < 0.35 * abundance)
                _all.Add(new Habitat
                {
                    Kind = elevation * 1000f < -DeepVentDepthM ? HabitatKind.BlackSmoker : HabitatKind.ShallowVent,
                    Position = position, Cell = c, AltitudeM = elevation * 1000f, Variation = variation,
                    TemperatureC = 300f + 100f * (float)heat, Ph = 2.8f + 1.4f * (float)acidity, RadiusM = 30f + 70f * (float)heat,
                });
            else if (flank && draw < 0.08 * abundance)
                _all.Add(new Habitat
                {
                    Kind = HabitatKind.AlkalineVent, Position = position, Cell = c, AltitudeM = elevation * 1000f, Variation = variation,
                    TemperatureC = 40f + 50f * (float)heat, Ph = 9f + 2f * (float)acidity, RadiusM = 100f + 200f * (float)heat,
                });
        }
    }

    /// <summary>Fiche des milieux d'une cellule (pour la sonde), ou null s'il n'y en a pas.</summary>
    public string Describe(int cell)
    {
        var indices = InCell(cell);
        if (indices.Length == 0)
            return null;
        var parts = new List<string>();
        foreach (HabitatKind kind in Enum.GetValues<HabitatKind>())
        {
            int count = 0;
            float minT = float.MaxValue, maxT = float.MinValue, minPh = float.MaxValue, maxPh = float.MinValue;
            foreach (int i in indices)
            {
                var h = _all[i];
                if (h.Kind != kind)
                    continue;
                count++;
                minT = Math.Min(minT, h.TemperatureC); maxT = Math.Max(maxT, h.TemperatureC);
                minPh = Math.Min(minPh, h.Ph); maxPh = Math.Max(maxPh, h.Ph);
            }
            if (count == 0)
                continue;
            string name = kind == HabitatKind.Fumarole ? "fumerolles" : $"{count} {NameOf(kind, count > 1)}";
            string temperature = maxT - minT < 3 ? $"{minT:0} °C" : $"{minT:0} à {maxT:0} °C";
            string ph = maxPh - minPh < 0.4f ? $"pH {Fr(minPh)}" : $"pH {Fr(minPh)} à {Fr(maxPh)}";
            parts.Add($"{name} ({temperature}, {ph})");
        }
        return string.Join(" ; ", parts);
    }

    private static string Fr(float value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
}
