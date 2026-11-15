using System.Linq;
using System;
using System.Collections.Generic;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;

namespace Planet;

/// <summary>
/// Une Terre vivante (étape 17), dans l'horloge climatique : crues des fleuves (et nouveaux bras
/// creusés par les plus fortes), tsunamis après les grands séismes sous la mer, cyclones
/// tropicaux, tornades sous les orages, icebergs vêlés par les glaciers, dunes qui avancent.
/// (Lahars, avalanches de neige et recul des falaises : <see cref="Main"/>.Crust ; aurores,
/// étoiles filantes, brouillards, arcs-en-ciel et tempêtes de sable : <see cref="SkyRenderer"/>.)
/// </summary>
public partial class Main
{
    /// <summary>Rayon (m) autour de la caméra où la crue et le tsunami du moment sont montrés.</summary>
    private const float EventReachM = 60000;
    /// <summary>Vitesse d'avancée des dunes (m/an) : 5 à 30 m/an pour les barkhanes (Bagnold 1941).</summary>
    private const double DuneSpeedMPerYear = 15;

    private readonly FloodModel _flood = new();
    private readonly TsunamiModel _tsunami = new();
    private readonly CycloneModel _cyclones = new();
    private readonly Random _eventRandom = new(29);
    private double _eventsDay = double.NaN;
    private double _floodTimer, _avulsionCooldown;
    private int _avulsionCount, _tsunamiCount, _cycloneCount, _tornadoCount, _calvedCount;
    private TornadoRenderer _tornadoRenderer;
    private IcebergRenderer _icebergRenderer;

    // Tornade en cours (au plus une, près de la caméra)
    private bool _tornado;
    private Double3 _tornadoPosition, _tornadoVelocity;
    private double _tornadoAge, _tornadoLife, _tornadoWidth, _tornadoTimer;

    // Icebergs proches de la caméra
    private readonly List<(Double3 direction, float size, float heading)> _icebergs = new();
    private double _icebergTimer;
    private int[] _calvingCells = Array.Empty<int>();
    private int _calvingVersion = -1;
    private int _glacierCells;

    private void SetupEvents()
    {
        _tornadoRenderer = new TornadoRenderer { Name = "Tornado" };
        AddChild(_tornadoRenderer);
        _icebergRenderer = new IcebergRenderer { Name = "Icebergs" };
        AddChild(_icebergRenderer);
    }

    private void ResetEvents()
    {
        _flood.Reset();
        _tsunami.Clear();
        _cyclones.Clear();
        if (_sky != null)
            UpdateCycloneClouds();
        _tornado = false;
        _icebergs.Clear();
        _eventsDay = double.NaN;
        _calvingVersion = -1;
        _glacierCells = 0;
        _recentSlides.Clear();
        _recentQuakes.Clear();
        _eventIndex = -1;
        _avulsionCount = _tsunamiCount = _cycloneCount = _tornadoCount = _calvedCount = 0;
        _tornadoRenderer?.HideTornado();
    }

    private void UpdateEvents(double delta)
    {
        bool climate = _clock == ClockMode.Climate && _climate.HasTemperature && _terrain.Field != null;
        if (!climate)
        {
            _eventsDay = double.NaN;
            _terrain.SetShaderParameter("flood_rise", 0f);
            _terrain.SetShaderParameter("tsunami_rise", 0f);
            _tornadoRenderer.HideTornado();
            _icebergRenderer.UpdateIcebergs(_terrain.Explorer.Position, Array.Empty<(Double3, float, float)>(), 0);
            return;
        }
        double day = _climate.TimeDays;
        double days = double.IsNaN(_eventsDay) ? 0 : Math.Max(0, day - _eventsDay);
        _eventsDay = day;
        Double3 camera = _terrain.Explorer.Position;
        Double3 up = camera.Normalized();
        Vector3 cameraDirection = up.ToVector3();
        int cell = _grid.NearestCell(cameraDirection, Math.Max(_weatherHint, 0));
        // Temps des événements locaux : le temps réel, un peu accéléré si l'horloge va vite
        double rate = !_running ? 0 : Math.Clamp(ClimateSpeeds[_climateSpeed] * 86400.0, 1.0, 3.0);

        RememberQuakes();
        UpdateCyclones(days, cameraDirection);
        UpdateCycloneClouds();
        UpdateFlood(delta, days, cell);
        UpdateTsunamis(day, cameraDirection);
        _terrain.SetShaderParameter("flood_rise", (float)_flood.RiseM);
        _terrain.SetShaderParameter("tsunami_rise", (float)_tsunami.SeaRise(up, day));
        _terrain.SetShaderParameter("event_up", cameraDirection);
        _terrain.SetShaderParameter("event_reach", EventReachM);
        // Dunes : elles avancent avec le vent dominant (motif périodique : décalage borné)
        _terrain.SetShaderParameter("dune_shift", (float)(day / 365.25 * DuneSpeedMPerYear % 2048.0));
        UpdateTornado(delta, delta * rate, cell);
        UpdateIcebergs(delta, days);
    }

    // --- Cyclones -----------------------------------------------------------------------------------

    private void UpdateCyclones(double days, Vector3 cameraDirection)
    {
        _cyclones.Step(days, _grid, _climate.SurfaceTemperature, c => _climate.Model.IsOcean(c), _eventRandom);
        foreach (var born in _cyclones.Born)
        {
            _cycloneCount++;
            double km = born.Position.ToVector3().AngleTo(cameraDirection) * PlanetConstants.RadiusKm;
            if (km < 3000)
                ShowNotice($"Un cyclone tropical se forme sur la mer chaude, à {km:N0} km");
        }
    }

    /// <summary>Nuages des cyclones (spirale, œil), dessinés par le shader des nuages et des ombres du sol.</summary>
    private void UpdateCycloneClouds()
    {
        var cyclones = new Vector4[4];
        int k = 0;
        foreach (var cy in _cyclones.Cyclones.OrderByDescending(c => c.MaxWind))
        {
            if (k == cyclones.Length)
                break;
            Vector3 eye = cy.Position.ToVector3();
            cyclones[k++] = new Vector4(eye.X, eye.Y, eye.Z, (float)Math.Clamp(cy.MaxWind / 60, 0.3, 1));
        }
        _sky.SetCyclones(cyclones);
        _terrain.SetShaderParameter("cyclones", cyclones);
    }

    // --- Crues ---------------------------------------------------------------------------------------

    private void UpdateFlood(double delta, double days, int cell)
    {
        var hydrology = _terrain.Field.Hydrology;
        if (hydrology == null)
            return;
        _floodTimer -= delta;
        if (_floodTimer <= 0)
        {
            _floodTimer = 1;
            _flood.UpdateNetwork(_grid, hydrology.Downstream, _tectonics.CellElevation,
                                 _climate.Statistics.RecentPrecipitation, _climate.Statistics.AnnualPrecipitation);
        }
        _flood.Follow(cell, days);
        // Les plus fortes crues rompent parfois une berge : le fleuve s'ouvre un nouveau bras
        _avulsionCooldown -= delta;
        if (_running && _flood.RiseM > 2.5 && _avulsionCooldown <= 0 && _terrain.Explorer.AltitudeAboveGround < 8000)
        {
            _avulsionCooldown = 20;
            if (_eventRandom.NextDouble() < 0.35)
                TryAvulsion();
        }
    }

    /// <summary>
    /// Avulsion : en crue, le fleuve rompt sa berge et s'ouvre un nouveau bras, qui descend la
    /// plaine par la plus grande pente (inscrit dans la croûte : un chenal de ~3,5 m de profondeur
    /// et ~50 m de large, sur 1 à 3 km ; l'eau de la plaine d'inondation s'y voit).
    /// </summary>
    private void TryAvulsion()
    {
        var field = _terrain.Field;
        var height = ThreadSafeHeight(field, 4.0);
        Double3 center = ViewTarget();
        var (ax, ay) = LavaFlow.TangentFrame(center);
        double r = TerrainHeightField.RadiusMeters;
        int hint = -1;
        Double3? breach = null;
        for (int k = 0; k < 150 && breach == null; k++)
        {
            double rho = 4000 * Math.Sqrt(_eventRandom.NextDouble()), phi = _eventRandom.NextDouble() * 2 * Math.PI;
            Double3 d = (center + ax * (rho * Math.Cos(phi) / r) + ay * (rho * Math.Sin(phi) / r)).Normalized();
            if (field.Sample(d, 4, ref hint).River > 0.9)
                breach = d;
        }
        if (breach is not { } start)
            return;
        // Le nouveau bras part de côté, puis suit la pente de la plaine
        var patch = new CrustPatch(start, 160, 20, (float)_climate.TimeDays, "Avulsion");
        var (_, downhill, _) = SlopeStability.LocalSlope(height, start, 300);
        Double3 side = start.Cross(downhill).Normalized() * (_eventRandom.NextDouble() < 0.5 ? -1 : 1);
        var (px, py) = patch.Project((start + side * (120 / r)).Normalized());
        double dx = side.Dot(patch.AxisX), dy = side.Dot(patch.AxisY);
        var delta = new float[patch.Size * patch.Size];
        var debris = new float[patch.Size * patch.Size];
        double length = 1000 + 2000 * _eventRandom.NextDouble(), limit = patch.HalfExtentM - 10 * patch.CellM;
        for (double travelled = 0; travelled < length && Math.Abs(px) < limit && Math.Abs(py) < limit; travelled += 20)
        {
            var (slope, down, _) = SlopeStability.LocalSlope(height, patch.DirectionAt(px, py), 60);
            double gx = down.Dot(patch.AxisX), gy = down.Dot(patch.AxisY);
            double wobble = (_eventRandom.NextDouble() - 0.5) * 0.5;
            dx = 0.6 * dx + 0.4 * gx - wobble * dy;
            dy = 0.6 * dy + 0.4 * gy + wobble * dx;
            double norm = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
            dx /= norm;
            dy /= norm;
            px += dx * 20;
            py += dy * 20;
            int ci = (int)((px + patch.HalfExtentM) / patch.CellM), cj = (int)((py + patch.HalfExtentM) / patch.CellM);
            for (int j = cj - 3; j <= cj + 3; j++)
                for (int i = ci - 3; i <= ci + 3; i++)
                {
                    if (i < 6 || j < 6 || i >= patch.Size - 6 || j >= patch.Size - 6)
                        continue;
                    double ex = (i + 0.5) * patch.CellM - patch.HalfExtentM - px, ey = (j + 0.5) * patch.CellM - patch.HalfExtentM - py;
                    double q = (ex * ex + ey * ey) / (30.0 * 30.0);
                    if (q >= 1)
                        continue;
                    int k = j * patch.Size + i;
                    float cut = (float)(-3.5 * (1 - q));
                    if (cut < delta[k])
                    {
                        delta[k] = cut;
                        debris[k] = -0.6f;
                    }
                }
            _ = slope;
        }
        patch.Publish(delta, debris);
        _crust.Add(patch);
        _terrain.InvalidateRegion(patch.Center, patch.ReachM);
        _avulsionCount++;
        ShowNotice("Crue : le fleuve rompt sa berge et s'ouvre un nouveau bras dans la plaine");
    }

    // --- Tsunamis --------------------------------------------------------------------------------------

    private void UpdateTsunamis(double day, Vector3 cameraDirection)
    {
        foreach (var quake in _activity.RecentEarthquakes)
        {
            if (quake.Magnitude < TsunamiModel.MinMagnitude)
                continue;
            int quakeCell = _grid.NearestCell(quake.Position, 0);
            if (_tectonics.CellElevation[quakeCell] >= 0)
                continue;   // séisme sous les terres : pas de tsunami
            _tsunami.Add(Double3.From(quake.Position), quake.Magnitude, day);
            _tsunamiCount++;
            double km = quake.Position.AngleTo(cameraDirection) * PlanetConstants.RadiusKm;
            double amplitude = TsunamiModel.CoastalAmplitude(quake.Magnitude, km);
            if (amplitude > 0.3)
                ShowNotice($"Alerte au tsunami : séisme de magnitude {French(quake.Magnitude)} sous la mer à {km:N0} km ; " +
                           $"vague de ~{French(amplitude)} m attendue sur les côtes d'ici {TsunamiModel.ArrivalSeconds(km) / 60:0} min (la mer se retire d'abord)");
        }
        _tsunami.Forget(day);
    }

    // --- Tornades --------------------------------------------------------------------------------------

    /// <summary>
    /// Tornades : sous un orage violent, l'après-midi, sur terre aux moyennes latitudes (air
    /// chaud et humide au sol, cisaillement du vent) ; colonne de 50 à 400 m de large qui avance
    /// à 10-20 m/s et dure de quelques minutes à une demi-heure.
    /// </summary>
    private void UpdateTornado(double realDelta, double eventSeconds, int cell)
    {
        var explorer = _terrain.Explorer;
        if (!_tornado)
        {
            _tornadoTimer -= realDelta;
            if (_tornadoTimer > 0 || !_running || explorer.AltitudeAboveGround > 6000)
            {
                _tornadoRenderer.HideTornado();
                return;
            }
            _tornadoTimer = 15;
            var weather = LocalWeatherAtCamera();
            float latitude = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(explorer.Position.Normalized().ToVector3().Y, -1f, 1f)));
            bool storm = _tectonics.CellElevation[cell] > 0 && weather.TemperatureC > 18 && weather.PrecipitationMmPerDay > 6
                         && weather.RelativeHumidity > 0.65f && weather.SolarHour is > 12 and < 21
                         && Math.Abs(latitude) is > 20 and < 55;
            if (!storm || _eventRandom.NextDouble() > 0.25)
            {
                _tornadoRenderer.HideTornado();
                return;
            }
            Double3 target = ViewTarget();
            var (ax, ay) = LavaFlow.TangentFrame(target);
            double angle = _eventRandom.NextDouble() * 2 * Math.PI, distance = 3000 + 5000 * _eventRandom.NextDouble();
            _tornadoPosition = (target + (ax * Math.Cos(angle) + ay * Math.Sin(angle)) * (distance / TerrainHeightField.RadiusMeters)).Normalized();
            Vector3 wind = _climate.Winds.Wind[cell];
            _tornadoVelocity = Double3.From(wind.LengthSquared() > 1 ? wind.Normalized() * 14f : new Vector3(10, 0, 0));
            _tornadoAge = 0;
            _tornadoLife = 300 + 1200 * _eventRandom.NextDouble();
            _tornadoWidth = 60 + 300 * _eventRandom.NextDouble();
            _tornado = true;
            _tornadoCount++;
            ShowNotice($"Tornade sous l'orage, à {Distance(distance)}");
        }
        _tornadoAge += eventSeconds;
        if (_tornadoAge > _tornadoLife)
        {
            _tornado = false;
            _tornadoRenderer.HideTornado();
            return;
        }
        Double3 velocity = _tornadoVelocity - _tornadoPosition * _tornadoVelocity.Dot(_tornadoPosition);
        _tornadoPosition = (_tornadoPosition + velocity * (eventSeconds / TerrainHeightField.RadiusMeters)).Normalized();
        int hint = -1;
        double ground = Math.Max(0, _terrain.Field.Sample(_tornadoPosition, 10, ref hint).Surface);
        Double3 foot = _tornadoPosition * (TerrainHeightField.RadiusMeters + ground);
        float life = (float)(_tornadoAge / _tornadoLife);
        float intensity = Mathf.SmoothStep(0f, 0.08f, life) * (1f - Mathf.SmoothStep(0.85f, 1f, life));
        Color light = _sky.Sun.LightColor * Mathf.Clamp(_sky.Sun.LightEnergy, 0.2f, 1f);
        _tornadoRenderer.Show((foot - _terrain.Explorer.Position).ToVector3(), _tornadoPosition.ToVector3(),
                              (float)_tornadoWidth, 1300f, intensity * 0.85f, light);
        // Poussière et débris aspirés au pied de la tornade
        if (_eventRandom.NextDouble() < realDelta * 8)
            _debris.AddPuff(foot + _tornadoPosition * 5, (float)(_tornadoWidth * 0.6), _tornadoPosition.ToVector3() * 8f);
    }

    /// <summary>Vent de la tornade au point de la caméra (m/s, repère de la planète).</summary>
    private Vector3 TornadoWindAt(Double3 position)
    {
        if (!_tornado)
            return Vector3.Zero;
        double km = Math.Acos(Math.Clamp(position.Normalized().Dot(_tornadoPosition), -1, 1)) * PlanetConstants.RadiusKm;
        double rm = _tornadoWidth / 2000;
        double speed = 70 * (km < rm ? km / rm : Math.Pow(rm / Math.Max(km, 1e-6), 1.0));
        if (speed < 1)
            return Vector3.Zero;
        Double3 outward = position.Normalized() - _tornadoPosition;
        Double3 around = position.Normalized().Cross(outward.Normalized());
        return (around * speed).ToVector3();
    }

    // --- Icebergs --------------------------------------------------------------------------------------

    /// <summary>
    /// Icebergs : les glaciers qui atteignent la mer vêlent des blocs de glace (chaque front en
    /// lâche quelques-uns par mois), qui dérivent à ~2 % du vent et fondent (quelques mètres par
    /// jour dans une eau à quelques degrés).
    /// </summary>
    private void UpdateIcebergs(double realDelta, double days)
    {
        var hydrology = _climate.Hydrology;
        if (hydrology != null && _calvingVersion != _climate.HydrologyVersion)
        {
            _calvingVersion = _climate.HydrologyVersion;
            // Glaciers : recalculés chaque année d'après la neige qui ne fond plus ; ils avancent
            // dans les années froides et neigeuses, reculent dans les années chaudes
            int glaciers = hydrology.GlacierCellCount;
            if (_glacierCells > 0 && Math.Abs(glaciers - _glacierCells) >= Math.Max(2, _glacierCells / 20))
                ShowNotice(glaciers > _glacierCells
                    ? $"Les glaciers avancent : {glaciers} cellules couvertes (contre {_glacierCells} l'an dernier)"
                    : $"Les glaciers reculent : {glaciers} cellules couvertes (contre {_glacierCells} l'an dernier)");
            _glacierCells = glaciers;
            var cells = new List<int>();
            for (int c = 0; c < _grid.CellCount; c++)
            {
                if (_tectonics.CellElevation[c] <= 0 || !(hydrology.Glacier[c] || _climate.Biomes[c] == Biome.IceSheet))
                    continue;
                foreach (int n in _grid.Neighbors[c])
                    if (_tectonics.CellElevation[n] < 0)
                    {
                        cells.Add(n);
                        break;
                    }
            }
            _calvingCells = cells.ToArray();
        }
        Double3 camera = _terrain.Explorer.Position;
        Double3 up = camera.Normalized();
        _icebergTimer -= realDelta;
        if (_running && _icebergTimer <= 0 && _calvingCells.Length > 0 && _terrain.Explorer.AltitudeAboveGround < 60000)
        {
            _icebergTimer = 4;
            foreach (int c in _calvingCells)
            {
                Double3 sea = Double3.From(_grid.Positions[c]).Normalized();
                if (sea.DistanceTo(up) * TerrainHeightField.RadiusMeters > 300000 || _icebergs.Count >= 80 || _eventRandom.NextDouble() > 0.3)
                    continue;
                var (ax, ay) = LavaFlow.TangentFrame(sea);
                double spread = 60000 / TerrainHeightField.RadiusMeters;
                Double3 at = (sea + (ax * (_eventRandom.NextDouble() - 0.5) + ay * (_eventRandom.NextDouble() - 0.5)) * spread).Normalized();
                int hint = -1;
                if (_terrain.Field.Sample(at, 50, ref hint).Height > -5)
                    continue;   // sur la terre ou un haut-fond : pas d'iceberg
                _icebergs.Add((at, (float)(20 + 130 * Math.Pow(_eventRandom.NextDouble(), 2)), (float)(_eventRandom.NextDouble() * 6.28)));
                _calvedCount++;
            }
        }
        for (int i = _icebergs.Count - 1; i >= 0; i--)
        {
            var (direction, size, heading) = _icebergs[i];
            int c = _grid.NearestCell(direction.ToVector3(), 0);
            Double3 drift = Double3.From(_climate.Winds.Wind[c]) * 0.02;
            direction = (direction + drift * (days * 86400 / TerrainHeightField.RadiusMeters)).Normalized();
            size -= (float)(days * Math.Max(0.3, _climate.SurfaceTemperature[c] + 2) * 1.5);
            bool far = direction.DistanceTo(up) * TerrainHeightField.RadiusMeters > 400000;
            if (size < 5 || far || _tectonics.CellElevation[c] > 0)
                _icebergs.RemoveAt(i);
            else
                _icebergs[i] = (direction, size, heading);
        }
        _icebergRenderer.UpdateIcebergs(camera, _icebergs, _displaySeaLevel + _climate.SeaLevelMeters);
    }

    /// <summary>Ligne des détails : les événements de la planète vivante.</summary>
    private string EventsLine()
    {
        if (_clock != ClockMode.Climate)
            return null;
        string flood = _flood.LocalRatio > 1.5 ? $"crue (débit ×{French(_flood.LocalRatio)}, +{French(_flood.RiseM)} m)"
            : _flood.LocalRatio < 0.5 ? $"basses eaux (débit ×{French(_flood.LocalRatio)})" : "fleuve à son niveau habituel";
        string cyclone = "";
        Cyclone strongest = null;
        foreach (var cy in _cyclones.Cyclones)
            if (strongest == null || cy.MaxWind > strongest.MaxWind)
                strongest = cy;
        if (strongest != null)
            cyclone = $"   cyclones en cours : {_cyclones.Cyclones.Count} (le plus fort : {strongest.MaxWind * 3.6:0} km/h, catégorie {CycloneModel.Category(strongest.MaxWind)})";
        return $"Planète vivante : {flood}{cyclone}   depuis le début : {_cycloneCount} cyclones, {_tsunamiCount} tsunamis, " +
               $"{_tornadoCount} tornades, {_avulsionCount} nouveaux bras de fleuve, {_calvedCount} icebergs vêlés";
    }
}
