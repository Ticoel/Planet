using System;
using System.Linq;
using Planet.Simulation;
using Planet.Simulation.Chemistry;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;

namespace Planet.Tests;

/// <summary>Chimie de la planète (étape 16b).</summary>
public static class ChemistryTests
{
    private sealed record World(TectonicPlates Tectonics, FastClimateEstimate Climate, HydrologyResult Hydrology, float[] Volcanism);

    private static readonly Lazy<World> Evolved = new(() =>
    {
        var grid = Fixtures.Grid(5);
        var tectonics = TectonicPlates.Generate(grid, 12, 3);
        while (tectonics.TimeMyr < 60)
            tectonics.Step(tectonics.MaxTimeStep());
        var climate = FastClimateEstimator.Estimate(grid, tectonics.CellElevation, new OrbitalParameters());
        var runoff = climate.AnnualPrecipitation.Select(p => 0.5f * p).ToArray();
        var hydrology = HydrologyModel.Compute(grid, tectonics.CellElevation, runoff, new float[grid.CellCount]);
        var volcanism = new float[grid.CellCount];
        foreach (var l in tectonics.Landforms)
            if (l.Active && l.Kind != LandformKind.ImpactCrater)
                volcanism[grid.NearestCell(l.Position, 0)] += 1;
        return new World(tectonics, climate, hydrology, volcanism);
    });

    private static ChemistryInputs Inputs(World w, double oxygen, double co2 = 280, float temperatureShift = 0, float rainFactor = 1) => new()
    {
        ElevationKm = w.Tectonics.CellElevation,
        Continental = w.Tectonics.CellContinental,
        Boundary = w.Tectonics.CellBoundary,
        BoundaryRate = w.Tectonics.CellBoundaryRate,
        TemperatureC = w.Climate.MeanTemperature.Select(t => t + temperatureShift).ToArray(),
        PrecipitationMm = w.Climate.AnnualPrecipitation.Select(p => p * rainFactor).ToArray(),
        Downstream = w.Hydrology.Downstream,
        Volcanism = w.Volcanism,
        OxygenFraction = oxygen,
        Co2Ppm = co2,
    };

    private static double Median(float[] values, Func<int, bool> keep)
    {
        var list = Enumerable.Range(0, values.Length).Where(keep).Select(i => (double)values[i]).OrderBy(v => v).ToArray();
        return list.Length == 0 ? double.NaN : list[list.Length / 2];
    }

    private static string Report(PlanetChemistry chem, World w)
    {
        bool Land(int c) => w.Tectonics.CellElevation[c] > 0;
        bool River(int c) => Land(c) && w.Hydrology.Discharge[c] > HydrologyModel.RiverThreshold;
        return string.Join("\n", Enum.GetValues<Element>().Select(e =>
            $"     {PlanetChemistry.SymbolOf(e),-2} roche {Median(chem.Rock[(int)e], Land),8:G3} ppm   sol {Median(chem.Soil[(int)e], Land) * 1000 / 1300,8:G3} mg/kg   " +
            $"fleuves {Median(chem.Water[(int)e], River),8:G3} mg/L   mer {chem.OceanMean[(int)e],8:G3} mg/L"));
    }

    [Test]
    public static void EarthLikeChemistryHasRealisticMagnitudes()
    {
        var w = Evolved.Value;
        var chem = new PlanetChemistry(w.Tectonics.Grid);
        chem.Update(Inputs(w, PlanetChemistry.ModernOxygen), double.PositiveInfinity);
        Console.WriteLine("     Terre actuelle (oxygène 21 %) :\n" + Report(chem, w));
        bool River(int c) => w.Tectonics.CellElevation[c] > 0 && w.Hydrology.Discharge[c] > HydrologyModel.RiverThreshold;
        double Ocean(Element e) => chem.OceanMean[(int)e];
        double Rivers(Element e) => Median(chem.Water[(int)e], River);
        // Valeurs de référence : océan (Broecker et Peng 1982), fleuves (Meybeck 1982, Livingstone 1963)
        Assert.True(Ocean(Element.Carbon) is > 15 and < 40, $"carbone dissous de la mer : {Ocean(Element.Carbon):G3} mg/L (Terre ~27)");
        // (sans vie, pas de fixation biologique de l'azote : bien moins de nitrate que sur la Terre)
        Assert.True(Ocean(Element.Nitrogen) is > 0.002 and < 3, $"nitrate de la mer : {Ocean(Element.Nitrogen):G3} mg N/L (Terre ~0,4 avec la vie)");
        Assert.True(Ocean(Element.Phosphorus) is > 0.01 and < 0.4, $"phosphate de la mer : {Ocean(Element.Phosphorus):G3} mg P/L (Terre ~0,07)");
        Assert.True(Ocean(Element.Sulfur) is > 200 and < 3000, $"sulfate de la mer : {Ocean(Element.Sulfur):G3} mg S/L (Terre ~900)");
        Assert.True(Ocean(Element.Iron) < 1e-4, $"fer dissous d'une mer oxygénée : {Ocean(Element.Iron):G3} mg/L (Terre ~3·10⁻⁵)");
        Assert.True(Rivers(Element.Nitrogen) is > 0.005 and < 5, $"azote des fleuves : {Rivers(Element.Nitrogen):G3} mg/L (Terre ~0,2 à 1 avec la vie)");
        Assert.True(Rivers(Element.Iron) is > 0.005 and < 2, $"fer des fleuves : {Rivers(Element.Iron):G3} mg/L (Terre ~0,05 à 0,7)");
        Assert.True(Rivers(Element.Phosphorus) is > 0.003 and < 0.2, $"phosphore des fleuves : {Rivers(Element.Phosphorus):G3} mg/L (Terre ~0,02 à 0,05)");
        Assert.True(Rivers(Element.Sulfur) is > 0.5 and < 40, $"soufre des fleuves : {Rivers(Element.Sulfur):G3} mg/L (Terre ~4)");
        Assert.True(Rivers(Element.Carbon) is > 2 and < 60, $"carbone des fleuves : {Rivers(Element.Carbon):G3} mg/L (Terre ~12)");
        Assert.True(Rivers(Element.Phosphorus) < Rivers(Element.Nitrogen), "le phosphate, peu mobile, est plus rare dans les fleuves que le nitrate");
        foreach (var table in chem.Rock.Concat(chem.Soil).Concat(chem.Water))
            Assert.True(table.All(v => float.IsFinite(v) && v >= 0), "valeurs finies et positives");
    }

    [Test]
    public static void WithoutOxygenTheOceanIsFerruginous()
    {
        var w = Evolved.Value;
        var prebiotic = new PlanetChemistry(w.Tectonics.Grid);
        prebiotic.Update(Inputs(w, PlanetChemistry.PrebioticOxygen), double.PositiveInfinity);
        var modern = new PlanetChemistry(w.Tectonics.Grid);
        modern.Update(Inputs(w, PlanetChemistry.ModernOxygen), double.PositiveInfinity);
        Console.WriteLine("     Avant la vie (oxygène en traces) :\n" + Report(prebiotic, w));
        double fe = prebiotic.OceanMean[(int)Element.Iron];
        Assert.True(fe > 1000 * modern.OceanMean[(int)Element.Iron] && fe > 0.1, $"sans oxygène, le fer reste dissous : {fe:G3} mg/L");
        Assert.True(prebiotic.OceanMean[(int)Element.Sulfur] < modern.OceanMean[(int)Element.Sulfur], "sans oxygène, moins de sulfate (les sulfures ne s'oxydent pas)");
        Assert.True(prebiotic.OceanMean[(int)Element.Nitrogen] < modern.OceanMean[(int)Element.Nitrogen], "sans oxygène, les éclairs fixent moins d'azote");
    }

    [Test]
    public static void WeatheringFollowsWarmthAndRain()
    {
        double cold = PlanetChemistry.WeatheringRate(0, 300), warm = PlanetChemistry.WeatheringRate(25, 300);
        Assert.True(warm / cold is > 4 and < 30, $"10 °C de plus ≈ altération ×2,5 (Arrhenius) : rapport {warm / cold:0.0} pour 25 °C d'écart");
        Assert.True(PlanetChemistry.WeatheringRate(15, 1000) > 3 * PlanetChemistry.WeatheringRate(15, 300), "plus de pluie, plus d'altération");
        Assert.Near(PlanetChemistry.WeatheringRate(15, 300), 1e-4, 1e-9, "front d'altération de référence : 0,1 mm/an");

        var w = Evolved.Value;
        var normal = new PlanetChemistry(w.Tectonics.Grid);
        normal.Update(Inputs(w, PlanetChemistry.PrebioticOxygen), double.PositiveInfinity);
        var hot = new PlanetChemistry(w.Tectonics.Grid);
        hot.Update(Inputs(w, PlanetChemistry.PrebioticOxygen, temperatureShift: 10, rainFactor: 1.3f), double.PositiveInfinity);
        int p = (int)Element.Phosphorus;
        // (sous les tropiques, l'altération plafonne : elle est limitée par la roche fraîche disponible, West et al. 2005)
        Console.WriteLine($"     phosphore apporté à la mer : {normal.RiverInput[p] / 1e9:0.0} kt/an, climat +10 °C et +30 % de pluie : {hot.RiverInput[p] / 1e9:0.0} kt/an");
        Assert.True(hot.RiverInput[p] > 1.25 * normal.RiverInput[p], "climat chaud et humide : plus de phosphore apporté à la mer");
    }

    [Test]
    public static void TimeStepsConvergeToEquilibrium()
    {
        var w = Evolved.Value;
        var input = Inputs(w, PlanetChemistry.PrebioticOxygen);
        var target = new PlanetChemistry(w.Tectonics.Grid);
        target.Update(input, double.PositiveInfinity);
        // Départ d'un autre état (climat plus froid), puis pas de temps successifs sous le climat normal
        var chem = new PlanetChemistry(w.Tectonics.Grid);
        chem.Update(Inputs(w, PlanetChemistry.PrebioticOxygen, temperatureShift: -8), double.PositiveInfinity);
        int n = (int)Element.Nitrogen;
        double before = Math.Abs(chem.OceanMean[n] - target.OceanMean[n]);
        for (int i = 0; i < 40; i++)
            chem.Update(input, 500);   // 20 000 ans
        double after = Math.Abs(chem.OceanMean[n] - target.OceanMean[n]);
        Assert.True(after < 0.01 * before + 1e-12, "l'océan rejoint son équilibre (nitrate : séjour ~3 000 ans)");
        Assert.True(chem.TimeYears == 20000, "temps écoulé");
        int c0 = Enumerable.Range(0, w.Tectonics.Grid.CellCount).First(c => w.Tectonics.CellElevation[c] > 0.3f);
        Assert.Near(chem.Soil[n][c0], target.Soil[n][c0], 1e-3 * target.Soil[n][c0] + 1e-9, "le sol rejoint son équilibre");
    }

    [Test]
    public static void UpdateIsFastEnoughForTheGame()
    {
        // Le jeu met la chimie à jour une fois par seconde, sur le fil principal
        var w = Evolved.Value;
        var chem = new PlanetChemistry(w.Tectonics.Grid);
        var input = Inputs(w, PlanetChemistry.PrebioticOxygen);
        chem.Update(input, double.PositiveInfinity);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            chem.Update(input, 1.0 / 365.25);
        double ms = stopwatch.Elapsed.TotalMilliseconds / 10;
        Console.WriteLine($"     mise à jour de la chimie : {ms:0.0} ms ({w.Tectonics.Grid.CellCount} cellules)");
        Assert.True(ms < 30, $"mise à jour trop lente : {ms:0.0} ms");
    }

    [Test]
    public static void Co2FollowsTheGreenhouseNeed()
    {
        Assert.Near(PlanetChemistry.Co2FromForcing(0), 280, 1e-9, "sans compensation : 280 ppm");
        Assert.Near(PlanetChemistry.Co2FromForcing(5.35 * Math.Log(2)), 560, 1e-6, "+3,7 W/m² : doublement");
        double young = PlanetChemistry.Co2FromForcing(70);
        Assert.True(young is > 5e4 and < 1e6, $"jeune Soleil faible compensé par ~0,1 à 1 bar de CO₂ : {young:N0} ppm");
        Assert.True(PlanetChemistry.Co2FromForcing(-10) < 100, "étoile trop forte : moins de CO₂");
    }
}
