using System;
using Planet.Rendering;
using Planet.Simulation.Climate;

namespace Planet;

/// <summary>
/// Eau de pluie sur le sol, autour de la caméra (<see cref="SurfacePonding"/>) : le sol se
/// mouille sous la pluie, et quand il pleut plus vite que le sol n'absorbe (sol déjà gorgé
/// d'eau, gelé, ou forte averse), l'eau stagne en flaques avant de s'infiltrer, puis tout sèche.
/// </summary>
public partial class Main
{
    private readonly SurfacePonding _ponding = new();
    private double _pondingDay = double.NaN;

    private void UpdateRain(in SkyFrame frame)
    {
        bool climate = _clock == ClockMode.Climate && _climate.HasTemperature;
        if (!climate || !frame.Weather.HasValue)
        {
            _ponding.Reset();
            _pondingDay = double.NaN;
            _terrain.SetShaderParameter("puddle_mm", 0f);
            _terrain.SetShaderParameter("ground_wet", 0f);
            _terrain.SetShaderParameter("rain_now", 0f);
            _terrain.SetShaderParameter("mud_cracks", 0f);
            return;
        }
        LocalWeather weather = frame.Weather.Value;
        double day = _climate.TimeDays;
        double hours = double.IsNaN(_pondingDay) ? 0 : Math.Max(0, day - _pondingDay) * 24;
        _pondingDay = day;
        int cell = _grid.NearestCell(_terrain.Explorer.Position.Normalized().ToVector3(), Math.Max(_weatherHint, 0));
        double saturation = Math.Clamp(_climate.SoilWater[cell] / 150.0, 0, 1);
        _ponding.Step(hours, weather.PrecipitationMmPerDay, saturation, weather.TemperatureC);
        _terrain.SetShaderParameter("puddle_mm", (float)_ponding.DepthMm);
        _terrain.SetShaderParameter("ground_wet", (float)_ponding.Wetness);
        _terrain.SetShaderParameter("mud_cracks", (float)_ponding.Cracks);
        float showers = weather.TemperatureC > 0.5f ? (float)Math.Min(1.0, SurfacePonding.ShowerIntensity(weather.PrecipitationMmPerDay) / 5.0) : 0f;
        _terrain.SetShaderParameter("rain_now", showers);
    }

    /// <summary>Ligne des détails : la pluie au sol.</summary>
    private string RainLine() => _clock != ClockMode.Climate || (_ponding.Wetness < 0.01 && _ponding.Cracks < 0.05) ? null
        : $"Sol : mouillé à {_ponding.Wetness * 100:0} %, eau en surface {French(_ponding.DepthMm)} mm" +
          (_ponding.DepthMm > 0.05 ? " (flaques : il pleut plus vite que le sol n'absorbe)" : "") +
          (_ponding.Cracks > 0.05 ? $", boue séchée fendue à {_ponding.Cracks * 100:0} %" : "");
}
