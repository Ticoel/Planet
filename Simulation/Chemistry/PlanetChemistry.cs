using System;
using Godot;
using Planet.Simulation.Tectonics;

namespace Planet.Simulation.Chemistry;

/// <summary>Éléments suivis par la chimie de la planète (étape 16b).</summary>
public enum Element
{
    Carbon,
    Nitrogen,
    Phosphorus,
    Sulfur,
    Iron,
}

/// <summary>Ce que la chimie reçoit du reste de la simulation (copies ou tableaux en lecture seule).</summary>
public sealed class ChemistryInputs
{
    public float[] ElevationKm { get; init; }
    public bool[] Continental { get; init; }
    public BoundaryType[] Boundary { get; init; }
    /// <summary>Vitesse relative aux frontières de plaques (cm/an).</summary>
    public float[] BoundaryRate { get; init; }
    /// <summary>Température de surface (°C).</summary>
    public float[] TemperatureC { get; init; }
    /// <summary>Précipitations (mm/an, en rythme annuel).</summary>
    public float[] PrecipitationMm { get; init; }
    /// <summary>Cellule aval de chaque cellule de terre (−1 : mer ou cuvette) ; null : pas de réseau.</summary>
    public int[] Downstream { get; init; }
    /// <summary>Volcanisme de chaque cellule (1 : un volcan actif ; plus pendant une éruption).</summary>
    public float[] Volcanism { get; init; }
    /// <summary>Part d'oxygène dans l'air (0,21 sur la Terre actuelle ; traces avant la vie).</summary>
    public double OxygenFraction { get; init; } = PlanetChemistry.PrebioticOxygen;
    /// <summary>Gaz carbonique de l'air (ppm).</summary>
    public double Co2Ppm { get; init; } = 280;
}

/// <summary>
/// Chimie de la planète (étape 16b) : où se trouvent le carbone, l'azote, le phosphore, le soufre
/// et le fer (plus l'eau des roches), et comment ils passent des roches aux sols, des sols aux
/// fleuves et des fleuves à la mer. Un modèle « à réservoirs » par cellule, comme les modèles
/// géochimiques globaux (Berner 2004, GEOCARB ; Walker 1977), mais réparti sur la grille.
/// <list type="number">
/// <item><b>Roches</b> : composition de la croûte selon sa nature. Croûte continentale
/// supérieure (Rudnick et Gao 2003 ; carbone : Wedepohl 1995) ; croûte océanique (basaltes de
/// dorsale, Gale et al. 2013 ; carbonates et eau de la croûte altérée, Alt et Teagle 1999) ;
/// roches d'arc volcanique (Kelemen et al. 2003). Plates-formes carbonatées sur les plateaux et
/// basses terres chaudes, évaporites (gypse) dans les bassins secs, soufre autour des volcans.</item>
/// <item><b>Altération</b> : la roche se dissout d'autant plus vite qu'il pleut et qu'il fait
/// chaud : w = w₀ (q / q₀) exp[Ea/R (1/T₀ − 1/T)] (loi d'Arrhenius, West, Galy et Bickle 2005 ;
/// Ea = 74 kJ/mol), avec un front d'altération qui avance de w₀ = 0,1 mm/an (production de sol,
/// Heimsath et al. 1997) pour q₀ = 300 mm/an de ruissellement à 15 °C. Une part de chaque
/// élément devient disponible dans le sol (le reste reste dans les argiles et les oxydes ; le
/// phosphore de l'apatite est surtout repris par les oxydes de fer). Carbonates et gypse se
/// dissolvent bien plus vite que les silicates ; et l'altération des silicates prend du gaz
/// carbonique à l'air : la moitié du carbone des fleuves en vient (Gaillardet et al. 1999).</item>
/// <item><b>Sols</b> : réservoir disponible (g/m² sur ~1 m) nourri par l'altération, par l'azote
/// fixé par les éclairs (~5 Tg N/an sur la Terre, Schumann et Huntrieser 2007 ; dix fois moins
/// dans un air sans oxygène, Navarro-González et al. 2001) et par le soufre des volcans ; vidé
/// par le lessivage (nitrates et sulfates partent en quelques années, le phosphate reste des
/// millénaires) et par l'érosion. Mise à jour exacte : S(t + Δt) = S∞ + (S − S∞) e^(−kΔt), stable
/// quel que soit le pas (des jours dans l'horloge climatique, l'équilibre dans l'horloge
/// géologique).</item>
/// <item><b>Fleuves</b> : ce que lessivent les sols descend le réseau hydrographique ;
/// concentration = flux dissous cumulé / débit cumulé (mg/L).</item>
/// <item><b>Océan</b> : un réservoir bien mélangé par élément (apports des fleuves, des sources
/// hydrothermales des dorsales, des éclairs ; temps de résidence d'après Broecker et Peng 1982 :
/// nitrate ~3 000 ans, phosphate ~20 000 ans, sulfate ~10 Ma), plus des panaches locaux près des
/// embouchures et des dorsales. Le carbone dissous suit le gaz carbonique de l'air.</item>
/// <item><b>Fer et oxygène</b> : sans oxygène (avant la vie), le fer dissous (Fe²⁺) est soluble :
/// océan « ferrugineux », jusqu'à ~50 µmol/L, limité par la sidérite (Holland 1984) ; les sulfures
/// ne s'oxydent pas, le soufre des roches reste piégé. Avec de l'oxygène, le fer précipite en
/// oxydes (moins d'une nanomole par litre dans l'océan actuel). L'étape « vie » pourra faire
/// monter l'oxygène et voir le fer quitter la mer (formations de fer rubané).</item>
/// </list>
/// Unités : roches en ppm (g/t), sols en g/m², eaux en mg/L (g/m³), temps en années.
/// </summary>
public sealed class PlanetChemistry
{
    public const int ElementCount = 5;
    /// <summary>Oxygène de l'air avant la vie (part en volume) : moins d'un cent-millième de l'actuel.</summary>
    public const double PrebioticOxygen = 1e-6;
    public const double ModernOxygen = 0.21;

    private const double RockDensityTonPerM3 = 2.7;
    private const double SoilMassKgPerM2 = 1300;      // ~1 m de sol
    private const double WeatheringRateM = 1e-4;      // front d'altération (m/an) à 15 °C, 300 mm/an
    private const double AtmosphericCarbonG = 2.5;    // CO₂ pris à l'air par l'altération (g C/m²/an à w₀, 280 ppm)
    private const double ReferenceRunoffMm = 300;
    private const double ActivationJPerMol = 74000;
    private const double GasConstant = 8.314;
    private const double ErosionRateM = 3e-5;         // érosion mécanique type (m/an)
    private const double LightningNitrogenGPerYear = 5e12;
    private const double VolcanicSulfurGPerYear = 1e13;
    private const double ReferenceVolcanoCount = 400;
    // Sources hydrothermales des dorsales (flux mondiaux, Elderfield et Schultz 1996)
    private const double VentIronGPerYear = 2.8e12;
    private const double VentSulfurGPerYear = 1e12;
    private const double VentCarbonGPerYear = 1.8e13;
    private const double ReferenceRidgeKm = 60000, ReferenceSpreadingCmYr = 5;
    private const double OceanMixingPerYear = 1.5;    // échange avec les cellules voisines
    private const double SideriteLimitMgL = 2.8;      // ~50 µmol/L de Fe²⁺
    private const double OxicIronMgL = 3e-5;          // ~0,5 nmol/L

    /// <summary>Composition de référence (ppm) : C, N, P, S, Fe.</summary>
    private static readonly double[] ContinentalPpm = { 1800, 83, 655, 621, 39200 };
    private static readonly double[] OceanicPpm = { 500, 5, 750, 1000, 81000 };
    private static readonly double[] ArcPpm = { 200, 20, 700, 300, 50000 };
    // Part libérée dans le sol par l'altération (le reste reste dans les argiles et les oxydes)
    private static readonly double[] ReleaseFraction = { 0.9, 0.5, 0.05, 0.3, 0.2 };
    // Dissolution plus rapide que celle des silicates : carbonates (C), gypse et pyrite (S)
    private static readonly double[] FastDissolution = { 5, 1, 1, 3, 1 };
    // Temps de séjour dans le sol au ruissellement de référence (ans)
    private static readonly double[] SoilResidenceYears = { 5, 3, 20000, 10, 300 };
    // Temps de séjour dans l'océan (ans)
    private static readonly double[] OceanResidenceYears = { 1e5, 3000, 20000, 1e7, 1e5 };

    public PlanetGrid Grid { get; }
    /// <summary>Roches : concentration de chaque élément (ppm), [élément][cellule].</summary>
    public float[][] Rock { get; }
    /// <summary>Eau des roches (minéraux hydratés), % en masse.</summary>
    public float[] RockWater { get; }
    /// <summary>Sols : réservoir disponible (g/m²) ; 0 sous la mer.</summary>
    public float[][] Soil { get; }
    /// <summary>Eaux de surface : fleuves sur terre, mer au large (mg/L).</summary>
    public float[][] Water { get; }
    /// <summary>Concentration moyenne de l'océan (mg/L).</summary>
    public double[] OceanMean { get; } = new double[ElementCount];
    /// <summary>Altération de la roche (m/an) de chaque cellule de terre.</summary>
    public float[] Weathering { get; }
    /// <summary>Apports des fleuves à la mer, toute la planète (g/an).</summary>
    public double[] RiverInput { get; } = new double[ElementCount];
    public double OxygenFraction { get; private set; } = PrebioticOxygen;
    public double Co2Ppm { get; private set; } = 280;
    public double TimeYears { get; private set; }
    public bool HasData { get; private set; }

    private readonly float[] _areaM2;
    private readonly bool[] _ocean;
    private readonly double[] _flux, _local;
    private readonly double[][] _mouths;
    private int[] _order;
    private int _orderCount;

    public PlanetChemistry(PlanetGrid grid)
    {
        Grid = grid;
        int n = grid.CellCount;
        Rock = NewTable(n);
        Soil = NewTable(n);
        Water = NewTable(n);
        RockWater = new float[n];
        Weathering = new float[n];
        _areaM2 = grid.ComputeCellAreas();
        double sphere = 4 * Math.PI * Math.Pow(PlanetConstants.RadiusKm * 1000, 2);
        double sum = 0;
        foreach (float a in _areaM2) sum += a;
        for (int c = 0; c < n; c++)
            _areaM2[c] = (float)(_areaM2[c] / sum * sphere);
        _ocean = new bool[n];
        _flux = new double[n];
        _local = new double[n];
        _mouths = new double[ElementCount][];
        for (int e = 0; e < ElementCount; e++)
            _mouths[e] = new double[n];
        _order = new int[n];
    }

    private static float[][] NewTable(int n)
    {
        var table = new float[ElementCount][];
        for (int e = 0; e < ElementCount; e++)
            table[e] = new float[n];
        return table;
    }

    /// <summary>Part « oxydante » du milieu : 0 sans oxygène, 1 dès ~1 % d'oxygène dans l'air.</summary>
    public static double Oxic(double oxygenFraction) =>
        Math.Clamp((Math.Log10(Math.Max(oxygenFraction, 1e-12)) + 5) / 3, 0, 1);

    /// <summary>Carbone inorganique dissous dans la mer (mg/L), d'après le gaz carbonique de l'air.</summary>
    public static double DissolvedCarbon(double co2Ppm, double temperatureC) =>
        24.0 * Math.Pow(Math.Max(co2Ppm, 1) / 280.0, 1.0 / 3.0) * Math.Clamp(1 + 0.015 * (15 - temperatureC), 0.6, 1.5);

    /// <summary>
    /// Gaz carbonique de l'air (ppm) qui donne un effet de serre supplémentaire F (W/m²) par
    /// rapport à 280 ppm : F = 5,35 ln(C/280) (Myhre et al. 1998) jusqu'à ~1 % ; au-delà la
    /// relation s'aplatit (bandes saturées) : ~0,2 bar pour compenser le jeune Soleil faible
    /// (Kasting 1993 ; Feulner 2012). Négatif : moins de 280 ppm.
    /// </summary>
    public static double Co2FromForcing(double forcingWm2)
    {
        const double knee = 19;
        if (forcingWm2 <= knee)
            return 280 * Math.Exp(forcingWm2 / 5.35);
        return 280 * Math.Exp(knee / 5.35) * Math.Exp((forcingWm2 - knee) / 18.5);
    }

    /// <summary>
    /// Avance la chimie de <paramref name="dtYears"/> années (infini : équilibre, pour l'horloge
    /// géologique).
    /// </summary>
    public void Update(ChemistryInputs input, double dtYears)
    {
        int n = Grid.CellCount;
        OxygenFraction = input.OxygenFraction;
        Co2Ppm = input.Co2Ppm;
        double oxic = Oxic(OxygenFraction);
        bool equilibrium = double.IsInfinity(dtYears) || !HasData;
        for (int c = 0; c < n; c++)
            _ocean[c] = input.ElevationKm[c] < 0;

        ComputeRocks(input);
        SortDownstream(input);

        // Azote des éclairs : réparti selon la pluie (les orages) et la chaleur
        double lightningWeight = 0;
        for (int c = 0; c < n; c++)
            lightningWeight += LightningWeight(input, c) * _areaM2[c];
        double lightning = LightningNitrogenGPerYear * (0.1 + 0.9 * oxic) / Math.Max(lightningWeight, 1);

        // Sources hydrothermales : le long des dorsales, selon la vitesse d'expansion
        double spacingKm = Math.Sqrt(4 * Math.PI * PlanetConstants.RadiusKm * PlanetConstants.RadiusKm / n);
        double ridgeReference = ReferenceRidgeKm / spacingKm * ReferenceSpreadingCmYr;

        // --- Sols ---------------------------------------------------------------------------------
        Array.Clear(RiverInput);
        for (int e = 0; e < ElementCount; e++)
        {
            Array.Clear(_flux);
            double residenceScale = e == (int)Element.Iron ? (oxic > 0.5 ? 300 : 1) : 1;   // fer oxydé : immobile
            double release = ReleaseFraction[e];
            if (e == (int)Element.Sulfur) release = 0.3 + 0.6 * oxic;       // les sulfures s'oxydent à l'air
            if (e == (int)Element.Iron) release = 0.2 * (1 - 0.95 * oxic);   // Fe²⁺ soluble sans oxygène
            for (int c = 0; c < n; c++)
            {
                if (_ocean[c])
                {
                    Soil[e][c] = 0;
                    continue;
                }
                double t = input.TemperatureC[c];
                double runoff = Runoff(input, c);
                double weathering = WeatheringRate(t, runoff);
                Weathering[c] = (float)weathering;
                double gain = Rock[e][c] * RockDensityTonPerM3 * weathering * release * FastDissolution[e];   // g/m²/an
                if (e == (int)Element.Carbon)
                    gain += AtmosphericCarbonG * weathering / WeatheringRateM * Math.Pow(Co2Ppm / 280.0, 0.3);
                if (e == (int)Element.Nitrogen)
                    gain += lightning * LightningWeight(input, c);
                if (e == (int)Element.Sulfur && input.Volcanism != null)
                    gain += VolcanicSulfur(input, c);
                // Pertes : lessivage (selon le ruissellement) et érosion
                double leach = runoff / ReferenceRunoffMm / (SoilResidenceYears[e] * residenceScale);
                double erosion = ErosionRateM * Math.Clamp(Math.Max(0, input.ElevationKm[c]) / 1.0, 0.2, 4)
                                 * Math.Clamp(input.PrecipitationMm[c] / 700.0, 0.3, 3);
                if (t < -15)
                {
                    leach *= 0.05;
                    erosion *= 0.2;
                }
                double k = leach + erosion;
                double equilibriumValue = gain / Math.Max(k, 1e-9);
                double s = equilibrium ? equilibriumValue : equilibriumValue + (Soil[e][c] - equilibriumValue) * Math.Exp(-k * dtYears);
                Soil[e][c] = (float)s;
                // Ce qui part dans les eaux (dissous), g/an pour la cellule
                _flux[c] = leach * s * _areaM2[c];
            }

            // --- Fleuves -------------------------------------------------------------------------
            RouteRivers(input, e);
        }

        // --- Océan --------------------------------------------------------------------------------
        double oceanVolume = 0, oceanTemperature = 0, oceanArea = 0;
        for (int c = 0; c < n; c++)
            if (_ocean[c])
            {
                oceanVolume += OceanDepth(input, c) * _areaM2[c];
                oceanTemperature += input.TemperatureC[c] * _areaM2[c];
                oceanArea += _areaM2[c];
            }
        oceanTemperature /= Math.Max(oceanArea, 1);
        for (int e = 0; e < ElementCount; e++)
        {
            Array.Clear(_local);
            double total = 0;
            for (int c = 0; c < n; c++)
            {
                if (!_ocean[c])
                    continue;
                double q = _mouths[e][c];   // apports des fleuves à cette embouchure (g/an)
                if (e == (int)Element.Nitrogen)
                    q += lightning * LightningWeight(input, c) * _areaM2[c];
                if (e == (int)Element.Sulfur && input.Volcanism != null)
                    q += VolcanicSulfur(input, c) * _areaM2[c];
                if (input.Boundary[c] == BoundaryType.Divergent)
                {
                    double vent = input.BoundaryRate[c] / ridgeReference;
                    q += e switch
                    {
                        (int)Element.Iron => VentIronGPerYear * vent,
                        (int)Element.Sulfur => VentSulfurGPerYear * vent,
                        (int)Element.Carbon => VentCarbonGPerYear * vent,
                        _ => 0,
                    };
                }
                _local[c] = q;
                total += q;
            }
            double tau = OceanResidenceYears[e];
            if (e == (int)Element.Phosphorus)
                tau *= 0.3 + 0.7 * oxic;   // océan ferrugineux : le phosphate est piégé par le fer
            double meanEquilibrium = oceanVolume > 0 ? total * tau / oceanVolume : 0;
            OceanMean[e] = equilibrium ? meanEquilibrium : meanEquilibrium + (OceanMean[e] - meanEquilibrium) * Math.Exp(-dtYears / tau);
            double cap = e == (int)Element.Iron ? (oxic > 0.5 ? OxicIronMgL : SideriteLimitMgL * (1 - oxic) + OxicIronMgL) : double.MaxValue;
            if (e == (int)Element.Carbon)
                OceanMean[e] = DissolvedCarbon(Co2Ppm, oceanTemperature);
            OceanMean[e] = Math.Min(OceanMean[e], cap);

            // Panaches : excès local près des sources, qui se mélange avec les voisines
            double removal = 1 / tau + OceanMixingPerYear;
            for (int c = 0; c < n; c++)
                _local[c] = _ocean[c] ? _local[c] / (OceanDepth(input, c) * _areaM2[c]) / removal : 0;
            for (int pass = 0; pass < 3; pass++)
                Smooth(_local);
            for (int c = 0; c < n; c++)
            {
                if (!_ocean[c])
                    continue;
                double mean = e == (int)Element.Carbon ? DissolvedCarbon(Co2Ppm, input.TemperatureC[c]) : OceanMean[e];
                Water[e][c] = (float)Math.Min(cap, mean + _local[c]);
            }
        }
        TimeYears += double.IsInfinity(dtYears) ? 0 : dtYears;
        HasData = true;
    }

    /// <summary>Profondeur de la mer (m), au moins 10 m.</summary>
    private static double OceanDepth(ChemistryInputs input, int c) => Math.Max(10, -input.ElevationKm[c] * 1000);

    /// <summary>Ruissellement (mm/an) : la moitié des précipitations (comme le réseau hydrographique).</summary>
    private static double Runoff(ChemistryInputs input, int c) => Math.Max(0, 0.5 * input.PrecipitationMm[c]);

    /// <summary>Altération de la roche (m/an) : loi d'Arrhenius et ruissellement.</summary>
    public static double WeatheringRate(double temperatureC, double runoffMm)
    {
        double t = Math.Max(temperatureC, -40) + 273.15;
        double arrhenius = Math.Exp(ActivationJPerMol / GasConstant * (1 / 288.15 - 1 / t));
        return Math.Min(20 * WeatheringRateM, WeatheringRateM * runoffMm / ReferenceRunoffMm * arrhenius);
    }

    private static double LightningWeight(ChemistryInputs input, int c) =>
        Math.Max(0, input.PrecipitationMm[c]) * Math.Exp(Math.Clamp((input.TemperatureC[c] - 15) / 10, -3, 2));

    /// <summary>Soufre des volcans retombé sur la cellule (g/m²/an) : le sien et celui des voisines.</summary>
    private double VolcanicSulfur(ChemistryInputs input, int c)
    {
        double v = input.Volcanism[c];
        foreach (int nb in Grid.Neighbors[c])
            v += 0.3 * input.Volcanism[nb];
        return v * VolcanicSulfurGPerYear / ReferenceVolcanoCount / _areaM2[c] * 0.5;
    }

    /// <summary>Lissage d'une grandeur entre cellules de mer voisines (mélange).</summary>
    private void Smooth(double[] values)
    {
        int n = Grid.CellCount;
        var copy = (double[])values.Clone();
        for (int c = 0; c < n; c++)
        {
            if (!_ocean[c])
                continue;
            double sum = copy[c], weight = 1;
            foreach (int nb in Grid.Neighbors[c])
                if (_ocean[nb])
                {
                    sum += copy[nb];
                    weight++;
                }
            values[c] = sum / weight;
        }
    }

    /// <summary>
    /// Composition des roches de chaque cellule : croûte continentale, océanique ou d'arc,
    /// plates-formes carbonatées, évaporites, soufre volcanique, et une variété propre à chaque
    /// région (± 25 %, les roches ne sont pas partout les mêmes).
    /// </summary>
    private void ComputeRocks(ChemistryInputs input)
    {
        int n = Grid.CellCount;
        for (int c = 0; c < n; c++)
        {
            double elevation = input.ElevationKm[c];
            bool continental = input.Continental[c];
            bool arc = input.Boundary[c] == BoundaryType.Convergent && (input.Volcanism?[c] ?? 0) > 0;
            double[] baseline = continental ? ContinentalPpm : OceanicPpm;
            for (int e = 0; e < ElementCount; e++)
            {
                double ppm = arc ? 0.5 * (baseline[e] + ArcPpm[e]) : baseline[e];
                double t = input.TemperatureC[c], p = input.PrecipitationMm[c];
                if (continental)
                {
                    if (e == (int)Element.Carbon)
                        // Calcaires des plateaux et basses terres chaudes (mers peu profondes)
                        ppm *= 1 + 4 * Band(elevation, -0.25, 0.35) * Math.Clamp((t - 10) / 12, 0, 1);
                    if (e == (int)Element.Sulfur)
                        // Gypse des bassins secs
                        ppm *= 1 + 5 * Band(elevation, -0.05, 0.8) * Math.Clamp((350 - p) / 250, 0, 1);
                    if (elevation > 2)
                        ppm *= e == (int)Element.Iron ? 1.2 : e == (int)Element.Carbon ? 0.5 : 1;
                }
                if (e == (int)Element.Sulfur && input.Volcanism != null)
                    ppm *= 1 + 2 * Math.Min(1, input.Volcanism[c]);
                ppm *= 1 + 0.25 * Hash(c, e);
                Rock[e][c] = (float)ppm;
            }
            RockWater[c] = (float)((continental ? 1.0 : 2.0) * (1 + 0.2 * Hash(c, 7)));
        }
    }

    /// <summary>1 entre a et b, 0 en dehors, avec des bords adoucis.</summary>
    private static double Band(double x, double a, double b)
    {
        double w = 0.15 * (b - a);
        return Math.Clamp((x - a + w) / (2 * w), 0, 1) * Math.Clamp((b - x + w) / (2 * w), 0, 1);
    }

    /// <summary>Nombre pseudo-aléatoire déterministe dans [−1, 1] pour une cellule.</summary>
    private static double Hash(int cell, int salt)
    {
        uint h = (uint)(cell * 374761393 + salt * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return h / (double)uint.MaxValue * 2 - 1;
    }

    /// <summary>
    /// Ordre de l'amont vers l'aval (tri topologique du réseau : une cellule passe après toutes
    /// celles qui s'écoulent vers elle ; l'altitude seule ne suffit pas dans les cuvettes remplies).
    /// </summary>
    private void SortDownstream(ChemistryInputs input)
    {
        int n = Grid.CellCount;
        var inflow = new int[n];
        for (int c = 0; c < n; c++)
        {
            int down = input.Downstream?[c] ?? -1;
            if (!_ocean[c] && down >= 0 && !_ocean[down])
                inflow[down]++;
        }
        int head = 0, tail = 0;
        for (int c = 0; c < n; c++)
            if (!_ocean[c] && inflow[c] == 0)
                _order[tail++] = c;
        while (head < tail)
        {
            int c = _order[head++];
            int down = input.Downstream?[c] ?? -1;
            if (down >= 0 && !_ocean[down] && --inflow[down] == 0)
                _order[tail++] = down;
        }
        // (une boucle dans le réseau, en principe impossible, laisserait des cellules de côté)
        _orderCount = tail;
    }

    /// <summary>
    /// Ce que lessivent les sols descend le réseau hydrographique, de l'amont vers l'aval : la
    /// concentration d'un fleuve est le flux dissous cumulé divisé par le débit cumulé. Arrivé à
    /// la mer, le flux est rangé dans la cellule de mer de l'embouchure.
    /// </summary>
    private void RouteRivers(ChemistryInputs input, int e)
    {
        int n = Grid.CellCount;
        var flux = _flux;
        var discharge = new double[n];
        for (int c = 0; c < n; c++)
            discharge[c] = _ocean[c] ? 0 : Runoff(input, c) / 1000.0 * _areaM2[c];   // m³/an
        var mouths = _mouths[e];
        Array.Clear(mouths);
        var elevation = input.ElevationKm;
        for (int k = 0; k < _orderCount; k++)
        {
            int c = _order[k];
            int down = input.Downstream?[c] ?? -1;
            Water[e][c] = discharge[c] > 0 ? (float)(flux[c] / discharge[c]) : 0;
            if (down >= 0 && !_ocean[down])
            {
                flux[down] += flux[c];
                discharge[down] += discharge[c];
                continue;
            }
            // Embouchure : la cellule de mer aval, ou la plus basse des voisines en mer
            int sea = down >= 0 ? down : -1;
            if (sea < 0)
                foreach (int nb in Grid.Neighbors[c])
                    if (_ocean[nb] && (sea < 0 || elevation[nb] < elevation[sea]))
                        sea = nb;
            if (sea >= 0)
            {
                mouths[sea] += flux[c];
                RiverInput[e] += flux[c];
            }
            // (sinon : cuvette fermée, le flux reste dans son lac salé)
        }
    }

    /// <summary>Nom de l'élément (« carbone »…) et symbole.</summary>
    public static string NameOf(Element element) => element switch
    {
        Element.Carbon => "carbone",
        Element.Nitrogen => "azote",
        Element.Phosphorus => "phosphore",
        Element.Sulfur => "soufre",
        _ => "fer",
    };

    public static string SymbolOf(Element element) => element switch
    {
        Element.Carbon => "C",
        Element.Nitrogen => "N",
        Element.Phosphorus => "P",
        Element.Sulfur => "S",
        _ => "Fe",
    };

    /// <summary>Concentration disponible dans le sol en mg/kg (le réservoir est en g/m² sur ~1 m).</summary>
    public float SoilMgPerKg(Element element, int cell) => (float)(Soil[(int)element][cell] * 1000 / SoilMassKgPerM2);
}
