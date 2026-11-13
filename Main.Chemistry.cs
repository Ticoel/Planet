using System;
using System.Linq;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Chemistry;
using Planet.Simulation.Tectonics;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Chimie de la planète (étape 16b) dans le jeu : la simulation (<see cref="PlanetChemistry"/>)
/// suit la géologie et le climat. Horloge géologique : état d'équilibre pour le relief et le
/// climat moyen estimé du moment (les sols et l'océan se règlent en quelques milliers d'années,
/// bien plus vite qu'un pas de la tectonique). Horloge climatique : les réservoirs évoluent avec
/// le temps écoulé, la pluie et la chaleur du moment, et les éruptions. L'interface la montre :
/// une couche par élément, la fiche de la sonde, des courbes.
/// </summary>
public partial class Main
{
    private const double ChemistryRefreshSeconds = 1.0;

    private PlanetChemistry _chemistry;
    private double _chemistryTimer;
    private double _chemistryDay = double.NaN;
    private float[] _estimatedTemperatures;
    private float[] _volcanism;

    /// <summary>Oxygène de l'air (part en volume) : en traces avant la vie ; l'étape « vie » le fera monter.</summary>
    public double AtmosphericOxygen { get; set; } = PlanetChemistry.PrebioticOxygen;

    private static readonly Element[] ChemistryLayers = { Element.Carbon, Element.Nitrogen, Element.Phosphorus, Element.Sulfur, Element.Iron };

    private void ResetChemistry()
    {
        _chemistry = new PlanetChemistry(_grid);
        _chemistryTimer = 0;
        _chemistryDay = double.NaN;
    }

    /// <summary>Gaz carbonique de l'air (ppm) : celui que règle le thermostat des roches à l'âge de l'étoile.</summary>
    private double Co2Ppm() => _tectonics == null ? 280
        : PlanetChemistry.Co2FromForcing(_star.Co2CompensationWm2(_tectonics.TimeMyr / 1000.0));

    /// <summary>Volcanisme de chaque cellule : volcans actifs, plus les éruptions en cours (horloge climatique).</summary>
    private float[] Volcanism(bool climate)
    {
        _volcanism ??= new float[_grid.CellCount];
        Array.Clear(_volcanism);
        int hint = 0;
        foreach (var landform in _tectonics.Landforms)
            if (landform.Active && landform.Kind != LandformKind.ImpactCrater)
            {
                hint = _grid.NearestCell(landform.Position, hint);
                _volcanism[hint] += 1;
            }
        if (climate)
            foreach (var eruption in _activity.Eruptions)
            {
                hint = _grid.NearestCell(eruption.Position, hint);
                _volcanism[hint] += eruption.Vei * eruption.IntensityAt(_climate.TimeDays);
            }
        return _volcanism;
    }

    private void UpdateChemistry(double delta)
    {
        if (_tectonics == null)
            return;
        _chemistry ??= new PlanetChemistry(_grid);
        _chemistryTimer -= delta;
        if (_chemistryTimer > 0)
            return;
        _chemistryTimer = ChemistryRefreshSeconds;
        bool climate = _clock == ClockMode.Climate && _climate.HasTemperature;
        float[] temperature = climate ? _climate.SurfaceTemperature : _estimatedTemperatures;
        float[] precipitation = climate
            ? _climate.Statistics.RecentPrecipitation.Select(p => p * 365.25f).ToArray()
            : _tectonics.AnnualPrecipitation;
        if (temperature == null || precipitation == null)
            return;
        var input = new ChemistryInputs
        {
            ElevationKm = _tectonics.CellElevation,
            Continental = _tectonics.CellContinental,
            Boundary = _tectonics.CellBoundary,
            BoundaryRate = _tectonics.CellBoundaryRate,
            TemperatureC = temperature,
            PrecipitationMm = precipitation,
            Downstream = _terrain.Field?.Hydrology?.Downstream,
            Volcanism = Volcanism(climate),
            OxygenFraction = AtmosphericOxygen,
            Co2Ppm = Co2Ppm(),
        };
        // Horloge géologique : équilibre ; climatique : le temps écoulé depuis la dernière fois
        double dtYears = double.PositiveInfinity;
        if (climate)
        {
            double day = _climate.TimeDays;
            dtYears = double.IsNaN(_chemistryDay) ? 0 : Math.Max(0, day - _chemistryDay) / 365.25;
            _chemistryDay = day;
        }
        else
            _chemistryDay = double.NaN;
        if (dtYears > 0 || !climate || !_chemistry.HasData)
            _chemistry.Update(input, climate && _chemistry.HasData ? dtYears : double.PositiveInfinity);
        if (ChemistryElement(_displayMode) != null)
            _colorsDirty = true;
    }

    // --- Couches ---------------------------------------------------------------------------------

    private static Element? ChemistryElement(DisplayMode mode) => mode switch
    {
        DisplayMode.ChemCarbon => Element.Carbon,
        DisplayMode.ChemNitrogen => Element.Nitrogen,
        DisplayMode.ChemPhosphorus => Element.Phosphorus,
        DisplayMode.ChemSulfur => Element.Sulfur,
        DisplayMode.ChemIron => Element.Iron,
        _ => null,
    };

    private static DisplayMode ChemistryMode(Element element) => element switch
    {
        Element.Carbon => DisplayMode.ChemCarbon,
        Element.Nitrogen => DisplayMode.ChemNitrogen,
        Element.Phosphorus => DisplayMode.ChemPhosphorus,
        Element.Sulfur => DisplayMode.ChemSulfur,
        _ => DisplayMode.ChemIron,
    };

    /// <summary>Plages des couleurs (échelle logarithmique) : sols en mg/kg, mer en mg/L.</summary>
    private static (double soilMin, double soilMax, double seaMin, double seaMax) ChemistryRange(Element element) => element switch
    {
        Element.Carbon => (1, 300, 10, 100),
        Element.Nitrogen => (0.003, 1, 0.001, 0.5),
        Element.Phosphorus => (5, 300, 0.001, 0.1),
        Element.Sulfur => (0.3, 100, 30, 2000),
        _ => (10, 5000, 1e-5, 5),
    };

    // Palette « viridis » : violet sombre (peu) → bleu → vert → jaune (beaucoup)
    private static readonly Color[] ChemistryGradient =
    {
        new(0.27f, 0.00f, 0.33f), new(0.23f, 0.32f, 0.55f), new(0.13f, 0.57f, 0.55f),
        new(0.37f, 0.79f, 0.38f), new(0.99f, 0.91f, 0.14f),
    };

    private static Color ChemistryColor(double value, double min, double max)
    {
        double t = Math.Clamp((Math.Log10(Math.Max(value, 1e-12)) - Math.Log10(min)) / (Math.Log10(max) - Math.Log10(min)), 0, 1);
        double x = t * (ChemistryGradient.Length - 1);
        int i = Math.Min((int)x, ChemistryGradient.Length - 2);
        return ChemistryGradient[i].Lerp(ChemistryGradient[i + 1], (float)(x - i));
    }

    /// <summary>Couleurs d'un élément : réservoir disponible des sols sur terre, concentration dissoute en mer.</summary>
    private Color[] ChemistryColors(Element element)
    {
        var colors = new Color[_grid.CellCount];
        var relief = PlanetColorizer.Compute(_tectonics, _climate, DisplayMode.Elevation);
        var (soilMin, soilMax, seaMin, seaMax) = ChemistryRange(element);
        for (int c = 0; c < colors.Length; c++)
        {
            if (_chemistry == null || !_chemistry.HasData)
            {
                colors[c] = relief[c];
                continue;
            }
            bool sea = _tectonics.CellElevation[c] < 0;
            Color color = sea
                ? ChemistryColor(_chemistry.Water[(int)element][c], seaMin, seaMax).Darkened(0.15f)
                : ChemistryColor(_chemistry.SoilMgPerKg(element, c), soilMin, soilMax);
            colors[c] = color.Lerp(relief[c], 0.15f);
        }
        return colors;
    }

    private string PlanetChemistryName(DisplayMode mode) =>
        ChemistryElement(mode) is { } e ? $"chimie : {PlanetChemistry.NameOf(e)}" : "";

    private string ChemistryLegend(DisplayMode mode)
    {
        if (ChemistryElement(mode) is not { } e)
            return "";
        var (soilMin, soilMax, seaMin, seaMax) = ChemistryRange(e);
        string name = PlanetChemistry.NameOf(e);
        return $"Chimie, {name} : sur terre, {name} disponible dans le sol ({Amount(soilMin)} à {Amount(soilMax)} mg/kg) ; " +
               $"en mer, dissous ({Amount(seaMin)} à {Amount(seaMax)} mg/L). Violet : peu ; bleu, vert ; jaune : beaucoup (échelle logarithmique)";
    }

    /// <summary>Quantité lisible : 3 chiffres significatifs, virgule décimale.</summary>
    private static string Amount(double value)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        string text = value switch
        {
            0 => "0",
            >= 1000 => value.ToString("N0", invariant),
            >= 100 => value.ToString("0", invariant),
            >= 10 => value.ToString("0.#", invariant),
            >= 1 => value.ToString("0.##", invariant),
            >= 0.001 => value.ToString("0.###", invariant),
            _ => value.ToString("0.0e0", invariant),
        };
        return text.Replace(',', ' ').Replace('.', ',');
    }

    // --- Interface ---------------------------------------------------------------------------------

    private void RegisterChemistry()
    {
        var registry = _ui.Registry;
        foreach (var element in ChemistryLayers)
        {
            var mode = ChemistryMode(element);
            string name = PlanetChemistry.NameOf(element);
            registry.Add(new LayerEntry
            {
                Group = "Chimie", Label = char.ToUpper(name[0]) + name[1..],
                Help = element switch
                {
                    Element.Carbon => "Carbone : calcaires des plateaux, bicarbonate des fleuves (roches et gaz carbonique de l'air), carbone dissous de la mer",
                    Element.Nitrogen => "Azote : fixé par les éclairs (pas encore de vie pour le fixer), lessivé vite en nitrate",
                    Element.Phosphorus => "Phosphore : libéré lentement par l'altération des roches, retenu par le sol ; il manque souvent à la vie",
                    Element.Sulfur => "Soufre : volcans, gypse des bassins secs, sulfate de la mer",
                    _ => "Fer : abondant dans les basaltes ; dissous dans une mer sans oxygène, il précipite dès que l'oxygène apparaît",
                },
                IsActive = () => _displayMode == mode,
                Activate = () => SetDisplayMode(mode),
            });
        }

        string Line(Func<Element, double> value, string unit) =>
            string.Join("  ", ChemistryLayers.Select(e => $"{PlanetChemistry.SymbolOf(e)} {Amount(value(e))}")) + " " + unit;
        bool Ready() => _chemistry != null && _chemistry.HasData;
        bool Sea(int c) => _tectonics.CellElevation[c] < 0;

        registry.Add(new ProbeField
        {
            Label = "Roches",
            Read = c => !Ready() ? null
                : $"C {Percent(_chemistry.Rock[0][c])}  N {Amount(_chemistry.Rock[1][c])} ppm  P {Amount(_chemistry.Rock[2][c])} ppm  " +
                  $"S {Amount(_chemistry.Rock[3][c])} ppm  Fe {Percent(_chemistry.Rock[4][c])}  eau {French(_chemistry.RockWater[c])} %",
        });
        registry.Add(new ProbeField
        {
            Label = "Sol (disponible)",
            Read = c => !Ready() || Sea(c) ? null : Line(e => _chemistry.SoilMgPerKg(e, c), "mg/kg"),
        });
        registry.Add(new ProbeField
        {
            Label = "Eau (dissous)",
            Read = c =>
            {
                if (!Ready())
                    return null;
                var hydrology = _terrain.Field?.Hydrology;
                string where = Sea(c) ? "mer" : hydrology != null && hydrology.Lake[c] ? "lac"
                    : hydrology != null && hydrology.Discharge[c] > Simulation.Climate.HydrologyModel.RiverThreshold ? "fleuve" : "ruisseaux";
                return $"{where} : " + Line(e => _chemistry.Water[(int)e][c], "mg/L");
            },
        });
        registry.Add(new TimeSeries
        {
            Label = "Gaz carbonique", Unit = "ppm", Format = "0", Color = (0.75f, 0.75f, 0.75f), Shown = false,
            Sample = () => _tectonics == null ? null : Co2Ppm(),
        });
        registry.Add(new TimeSeries
        {
            Label = "Fer dissous (mer)", Unit = "mg/L", Format = "0.000", Color = (0.85f, 0.4f, 0.25f), Shown = false,
            Sample = () => Ready() ? _chemistry.OceanMean[(int)Element.Iron] : null,
        });
        registry.Add(new TimeSeries
        {
            Label = "Phosphore dissous (mer)", Unit = "mg/L", Format = "0.000", Color = (0.6f, 0.85f, 0.5f), Shown = false,
            Sample = () => Ready() ? _chemistry.OceanMean[(int)Element.Phosphorus] : null,
        });
    }

    private static string Percent(double ppm) => ppm >= 10000 ? $"{French(ppm / 10000)} %" : $"{Amount(ppm)} ppm";

    /// <summary>Ligne des détails (touche I) : l'air et la mer.</summary>
    private string ChemistryLine()
    {
        if (_chemistry == null || !_chemistry.HasData)
            return null;
        double co2 = Co2Ppm();
        string co2Text = co2 >= 10000 ? $"{French(co2 / 1e6 * 1.013)} bar" : $"{co2:0} ppm";
        string oxygen = AtmosphericOxygen < 1e-4 ? "traces (pas encore de vie)" : $"{French(AtmosphericOxygen * 100)} %";
        double fe = _chemistry.OceanMean[(int)Element.Iron];
        return $"Air : gaz carbonique {co2Text}, oxygène {oxygen}   Mer : fer dissous {Amount(fe)} mg/L ({(fe > 0.01 ? "mer ferrugineuse, sans oxygène" : "fer précipité")}), " +
               $"phosphore {Amount(_chemistry.OceanMean[(int)Element.Phosphorus])} mg/L, sulfate {Amount(_chemistry.OceanMean[(int)Element.Sulfur])} mg S/L";
    }
}
