using System.Linq;
using Godot;
using Planet.Rendering;
using Planet.Simulation.Climate;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Eau souterraine et sources d'eau douce (étape 17) dans le jeu (<see cref="Groundwater"/>) :
/// recalculées avec le relief (les vasques des sources sont creusées dans le relief détaillé,
/// pleines de vraie eau), lisibles à la sonde, et dans une couche.
/// </summary>
public partial class Main
{
    private Groundwater _groundwater;

    /// <summary>Nappe et sources du relief et du climat du moment.</summary>
    private Groundwater UpdateGroundwater()
    {
        int n = _grid.CellCount;
        bool climate = _clock == ClockMode.Climate && _climate.HasTemperature && _climate.Statistics.CompletedYears > 0;
        float[] rain = climate ? _climate.Statistics.AnnualPrecipitation : _tectonics.AnnualPrecipitation ?? new float[n];
        float[] temperature = climate ? _climate.Statistics.MeanTemperature : _estimatedTemperatures ?? Enumerable.Repeat(15f, n).ToArray();
        float[] carbon = _chemistry != null && _chemistry.HasData ? _chemistry.Rock[0] : null;
        bool[] river = _terrain.Field?.Hydrology?.River;
        _groundwater = new Groundwater(_grid, _tectonics.CellElevation, rain, temperature, carbon, river, Seed);
        if (_displayMode == DisplayMode.Groundwater)
            _colorsDirty = true;
        return _groundwater;
    }

    private void RegisterGroundwater()
    {
        _ui.Registry.Add(new LayerEntry
        {
            Group = "Planète", Label = "Eau souterraine",
            Help = "Profondeur de la nappe sous la surface, sources d'eau douce et résurgences des calcaires karstiques",
            IsActive = () => _displayMode == DisplayMode.Groundwater,
            Activate = () => SetDisplayMode(DisplayMode.Groundwater),
        });
        _ui.Registry.Add(new ProbeField { Label = "Eau souterraine", Read = c => _groundwater?.Describe(c) });
    }

    private static readonly (float depth, Color color)[] TableGradient =
    {
        (0f, new Color(0.15f, 0.45f, 0.95f)), (5f, new Color(0.2f, 0.75f, 0.85f)), (20f, new Color(0.45f, 0.8f, 0.4f)),
        (60f, new Color(0.9f, 0.8f, 0.3f)), (150f, new Color(0.55f, 0.35f, 0.2f)),
    };

    private Color[] GroundwaterColors()
    {
        var colors = PlanetColorizer.Compute(_tectonics, _climate, DisplayMode.Elevation);
        if (_groundwater == null)
            return colors;
        for (int c = 0; c < colors.Length; c++)
        {
            if (_tectonics.CellElevation[c] <= 0)
            {
                colors[c] = new Color(0.08f, 0.1f, 0.18f);
                continue;
            }
            float d = _groundwater.TableDepthM[c];
            int k = 0;
            while (k < TableGradient.Length - 2 && d > TableGradient[k + 1].depth) k++;
            float t = Mathf.Clamp((d - TableGradient[k].depth) / (TableGradient[k + 1].depth - TableGradient[k].depth), 0f, 1f);
            colors[c] = TableGradient[k].color.Lerp(TableGradient[k + 1].color, t);
            if (_groundwater.Karst[c])
                colors[c] = colors[c].Lerp(Colors.White, 0.25f);
        }
        foreach (var spring in _groundwater.Springs)
            colors[spring.Cell] = spring.Kind == SpringKind.Karst ? Colors.White : colors[spring.Cell].Lerp(new Color(0.3f, 1f, 1f), 0.5f);
        return colors;
    }
}
