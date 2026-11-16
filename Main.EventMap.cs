using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Recensement des événements naturels en cours (étape 17) : une couche de la carte qui les
/// montre tous, et la touche B qui emmène les observer, l'un après l'autre (comme les lieux de
/// test F1 à F11, mais pour ce qui se passe en ce moment sur la planète).
/// </summary>
public partial class Main
{
    /// <summary>Un événement à aller voir : nom, position (direction), et placement de la caméra.</summary>
    private sealed record NaturalEvent(string Label, Double3 Position, Action Place);

    private readonly List<(string what, Double3 where, double day)> _recentSlides = new();
    private readonly List<(Vector3 where, float magnitude, double day)> _recentQuakes = new();
    private int _eventIndex = -1;

    private void RememberSlide(string what, Double3 where)
    {
        _recentSlides.Add((what, where, _climate.TimeDays));
        if (_recentSlides.Count > 8)
            _recentSlides.RemoveAt(0);
    }

    /// <summary>Séismes forts récents (magnitude 6 et plus, 3 derniers jours).</summary>
    private void RememberQuakes()
    {
        double day = _climate.TimeDays;
        foreach (var quake in _activity.RecentEarthquakes)
            if (quake.Magnitude >= 6)
                _recentQuakes.Add((quake.Position, quake.Magnitude, day));
        _recentQuakes.RemoveAll(q => day - q.day > 3);
        if (_recentQuakes.Count > 30)
            _recentQuakes.RemoveRange(0, _recentQuakes.Count - 30);
    }

    private static float LatitudeOf(Vector3 p) => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Normalized().Y, -1f, 1f)));

    /// <summary>Cellule propice aux tornades : orage chaud et humide, l'après-midi, sur terre aux moyennes latitudes.</summary>
    private bool TornadoWeather(int c, Vector3 sun)
    {
        Vector3 p = _grid.Positions[c].Normalized();
        float lat = Math.Abs(LatitudeOf(p));
        double hour = 12 + (Math.Atan2(-p.Z, p.X) - Math.Atan2(-sun.Z, sun.X)) / (2 * Math.PI) * 24;
        hour = ((hour % 24) + 24) % 24;
        return _tectonics.CellElevation[c] > 0 && lat is > 20 and < 55 && _climate.SurfaceTemperature[c] > 18
               && _climate.Statistics.RecentPrecipitation[c] > 6 && _climate.RelativeHumidity[c] > 0.65f && hour is > 12 and < 21;
    }

    private bool Sandstorm(int c) => _tectonics.CellElevation[c] > 0 && _climate.Statistics.AnnualPrecipitation[c] < 300
                                     && _climate.Snow[c] < 5 && _climate.Winds.Wind[c].Length() > 13;

    private bool AvalancheSnow(int c) => _tectonics.CellElevation[c] > 1.2f && _climate.Snow[c] > 300;

    /// <summary>Front du tsunami (rayon en km depuis sa source) à cet instant.</summary>
    private static double TsunamiFrontKm(TsunamiModel.Wave wave, double day) => (day - wave.StartDay) * 86400 * TsunamiModel.SpeedMs / 1000;

    /// <summary>
    /// Les événements en cours, du plus spectaculaire au plus discret : tsunamis, cyclones,
    /// éruptions fissurales, éruptions, orages à tornades, crues, tempêtes de sable, glissements
    /// récents, séismes, aurores, glaciers qui vêlent, montagnes enneigées (avalanches).
    /// </summary>
    private List<NaturalEvent> CurrentEvents()
    {
        var events = new List<NaturalEvent>();
        if (_clock != ClockMode.Climate || !_climate.HasTemperature)
            return events;
        double day = _climate.TimeDays;
        double scale = PlanetConstants.RadiusKm / PlanetConstants.EarthRadiusKm;
        var explorer = _terrain.Explorer;
        Vector3 sun = _climate.Sun.Direction;
        Double3 Cell(int c) => Double3.From(_grid.Positions[c]).Normalized();
        // Vue de côté, à quelques km et en hauteur, l'événement au centre de l'écran (visé à
        // <lift> mètres au-dessus du sol : le panache d'un volcan, le pied d'un orage…)
        Action Side(Double3 at, double distanceM, double altitudeM, double lift = 0) => () =>
        {
            var (ax, _) = LavaFlow.TangentFrame(at);
            explorer.PlaceAbove((at + ax * (distanceM / TerrainHeightField.RadiusMeters)).Normalized(), altitudeM);
            explorer.LookAt(GroundPoint(at, lift));
        };

        foreach (var wave in _tsunami.Waves)
        {
            // La côte que la vague atteindra bientôt (dans 2 à 40 minutes)
            double front = TsunamiFrontKm(wave, day);
            int best = -1;
            double bestKm = double.MaxValue;
            for (int c = 0; c < _grid.CellCount; c++)
            {
                if (_tectonics.CellElevation[c] <= 0 || !_grid.Neighbors[c].Any(n => _tectonics.CellElevation[n] < 0))
                    continue;
                double km = Math.Acos(Math.Clamp(Cell(c).Dot(wave.Origin), -1, 1)) * PlanetConstants.RadiusKm;
                double ahead = km - front;
                if (ahead > 2 * 60 * TsunamiModel.SpeedMs / 1000 && ahead < 40 * 60 * TsunamiModel.SpeedMs / 1000 && km < bestKm)
                {
                    best = c;
                    bestKm = km;
                }
            }
            if (best < 0)
                continue;
            double minutes = (bestKm - front) * 1000 / TsunamiModel.SpeedMs / 60;
            int sea = Array.Find(_grid.Neighbors[best], n => _tectonics.CellElevation[n] < 0);
            Double3 coast = Cell(best);
            events.Add(new NaturalEvent($"Tsunami (séisme de magnitude {French(wave.Magnitude)}) : la vague atteint cette côte dans ~{minutes:0} min", coast, () =>
            {
                // Sur la côte, regard vers le large d'où arrive la vague
                Double3 offshore = (coast * 0.7 + Cell(sea) * 0.3).Normalized();
                explorer.PlaceAbove(coast, 120);
                explorer.LookAt(GroundPoint(offshore, 0));
            }));
        }
        foreach (var cy in _cyclones.Cyclones.OrderByDescending(c => c.MaxWind))
        {
            Double3 eye = cy.Position;
            events.Add(new NaturalEvent($"Cyclone tropical : vents de {cy.MaxWind * 3.6:0} km/h (catégorie {CycloneModel.Category(cy.MaxWind)}), vu de haut", eye,
                () =>
                {
                    // À la verticale de l'œil, regard droit vers le bas
                    explorer.PlaceAbove(eye, 2500000 * scale);
                    explorer.LookAt(eye * TerrainHeightField.RadiusMeters);
                }));
        }
        foreach (var eruption in _activity.Eruptions.OrderByDescending(e => e.Fissure).ThenByDescending(e => e.Vei))
        {
            Double3 vent = Double3.From(eruption.Position).Normalized();
            if (VentHeight(eruption.Position) < -SubmarineVisibleDepthM)
                continue;
            events.Add(eruption.Fissure
                ? new NaturalEvent($"Éruption fissurale : fissure de {French(eruption.LengthM / 1000)} km", vent, Side(vent, 6000, 900))
                : new NaturalEvent($"Éruption volcanique (indice d'explosivité {eruption.Vei})", vent, Side(vent, 25000, 3000, 1500)));
        }
        int storm = -1, sand = -1, snow = -1;
        double stormRain = 0, sandWind = 0, snowDepth = 0;
        for (int c = 0; c < _grid.CellCount; c++)
        {
            if (TornadoWeather(c, sun) && _climate.Statistics.RecentPrecipitation[c] > stormRain)
            {
                storm = c;
                stormRain = _climate.Statistics.RecentPrecipitation[c];
            }
            if (Sandstorm(c) && _climate.Winds.Wind[c].Length() > sandWind)
            {
                sand = c;
                sandWind = _climate.Winds.Wind[c].Length();
            }
            if (AvalancheSnow(c) && _climate.Snow[c] > snowDepth)
            {
                snow = c;
                snowDepth = _climate.Snow[c];
            }
        }
        if (storm >= 0)
            events.Add(new NaturalEvent("Orages violents : des tornades peuvent naître sous ces nuages", Cell(storm), Side(Cell(storm), 4000, 300, 1200)));
        if (_flood.Ratio != null)
        {
            var hydrology = _terrain.Field?.Hydrology;
            int river = -1;
            for (int c = 0; c < _grid.CellCount; c++)
                if (hydrology != null && hydrology.River[c] && _flood.Ratio[c] > 2 && (river < 0 || _flood.Ratio[c] > _flood.Ratio[river]))
                    river = c;
            if (river >= 0)
                events.Add(new NaturalEvent($"Crue : le fleuve roule {French(_flood.Ratio[river])} fois son débit habituel et déborde", Cell(river), Side(Cell(river), 3000, 600)));
        }
        if (sand >= 0)
            events.Add(new NaturalEvent($"Tempête de sable : vent de {sandWind * 3.6:0} km/h sur le désert", Cell(sand), () => explorer.PlaceAbove(Cell(sand), 30)));
        foreach (var (what, where, when) in Enumerable.Reverse(_recentSlides))
            if (day - when < 30)
                events.Add(new NaturalEvent($"{what} récent (il y a {Math.Max(0, (day - when) * 24):0} h)", where, Side(where, 900, 250)));
        foreach (var (where, magnitude, _) in _recentQuakes.OrderByDescending(q => q.magnitude).Take(2))
        {
            Double3 epicenter = Double3.From(where).Normalized();
            events.Add(new NaturalEvent($"Séisme récent de magnitude {French(magnitude)} (épicentre)", epicenter, Side(epicenter, 8000, 3000)));
        }
        // Aurores : côté nuit, vers 63° de latitude, du côté le plus sombre
        int aurora = -1;
        for (int c = 0; c < _grid.CellCount; c++)
        {
            Vector3 p = _grid.Positions[c].Normalized();
            if (Math.Abs(LatitudeOf(p)) is > 58 and < 68 && p.Dot(sun) < -0.25f && (aurora < 0 || p.Dot(sun) < _grid.Positions[aurora].Normalized().Dot(sun)))
                aurora = c;
        }
        if (aurora >= 0)
        {
            Double3 at = Cell(aurora);
            float pole = LatitudeOf(_grid.Positions[aurora]) >= 0 ? 1f : -1f;
            events.Add(new NaturalEvent("Aurores polaires dans la nuit (regard vers le pôle)", at, () =>
            {
                explorer.PlaceAbove(at, 20);
                explorer.Face(at + Double3.UnitY * pole, 0.45);
            }));
        }
        if (_calvingFronts.Length > 0)
        {
            // Le front le plus proche de la caméra
            Double3 camera = explorer.Position.Normalized();
            Double3 front = _calvingFronts.OrderBy(f => f.DistanceTo(camera)).First();
            events.Add(new NaturalEvent("Front d'un glacier qui atteint la mer et vêle des icebergs", front, Side(front, 4000, 800)));
        }
        if (snow >= 0)
            events.Add(new NaturalEvent($"Neige épaisse en montagne ({snowDepth:N0} mm d'eau) : des avalanches peuvent partir", Cell(snow), Side(Cell(snow), 5000, 1500)));
        return events;
    }

    /// <summary>Point du sol (ou de l'eau qui le couvre) dans une direction, plus <paramref name="lift"/> mètres.</summary>
    private Double3 GroundPoint(Double3 direction, double lift)
    {
        direction = direction.Normalized();
        int hint = -1;
        double ground = _terrain.Field == null ? 0 : Math.Max(0, _terrain.Field.Sample(direction, 1, ref hint).Surface);
        return direction * (TerrainHeightField.RadiusMeters + ground + lift);
    }

    /// <summary>Touche B : emmène voir l'événement naturel suivant (Maj+B : le précédent).</summary>
    private void GoToNextEvent(int direction)
    {
        if (_clock != ClockMode.Climate || !_climate.HasTemperature)
        {
            ShowNotice("Les événements naturels se déroulent dans l'horloge climatique (touche C : figer la géologie et y passer)");
            return;
        }
        var events = CurrentEvents();
        if (events.Count == 0)
        {
            ShowNotice("Aucun événement naturel notable en ce moment : laissez passer un peu de temps");
            return;
        }
        _eventIndex = ((_eventIndex + direction) % events.Count + events.Count) % events.Count;
        var chosen = events[_eventIndex];
        _terrain.Explorer.Transition(chosen.Place);
        ShowNotice($"Événement {_eventIndex + 1} sur {events.Count} : {chosen.Label}");
    }

    // --- Couche « Événements naturels » ---------------------------------------------------------

    private void RegisterEventMap()
    {
        _ui.Registry.Add(new LayerEntry
        {
            Group = "Climat", Label = "Événements naturels",
            Help = "Tout ce qui se passe en ce moment : éruptions, fissures, cyclones, tsunamis, crues, tempêtes de sable, orages à tornades, " +
                   "aurores, glaciers qui vêlent, séismes et glissements récents. Touche B : aller les voir.",
            IsActive = () => _displayMode == DisplayMode.NaturalEvents,
            Activate = () => SetDisplayMode(DisplayMode.NaturalEvents),
        });
    }

    /// <summary>Couleurs de la couche : relief en gris, chaque événement en couleur.</summary>
    private Color[] EventColors()
    {
        var colors = PlanetColorizer.Compute(_tectonics, _climate, DisplayMode.Elevation);
        int n = colors.Length;
        for (int c = 0; c < n; c++)
        {
            float grey = colors[c].Luminance * 0.4f + (_tectonics.CellElevation[c] > 0 ? 0.22f : 0.06f);
            colors[c] = new Color(grey, grey, grey);
        }
        if (_clock != ClockMode.Climate || !_climate.HasTemperature)
            return colors;
        double day = _climate.TimeDays;
        Vector3 sun = _climate.Sun.Direction;
        var hydrology = _terrain.Field?.Hydrology;
        double cellKm = _grid.MeanEdgeAngle * PlanetConstants.RadiusKm;
        void Paint(int c, Color color, float amount) => colors[c] = colors[c].Lerp(color, Mathf.Clamp(amount, 0f, 1f));
        for (int c = 0; c < n; c++)
        {
            Vector3 p = _grid.Positions[c].Normalized();
            float lat = Math.Abs(LatitudeOf(p));
            // Fond : aurores côté nuit, neige des avalanches, tempêtes de sable, orages, crues
            if (lat is > 60 and < 73 && p.Dot(sun) < -0.1f)
                Paint(c, new Color(0.2f, 0.95f, 0.45f), 0.6f);
            if (AvalancheSnow(c))
                Paint(c, new Color(0.75f, 0.85f, 1f), 0.6f);
            if (Sandstorm(c))
                Paint(c, new Color(0.85f, 0.6f, 0.25f), 0.85f);
            if (TornadoWeather(c, sun))
                Paint(c, new Color(0.75f, 0.4f, 0.9f), 0.85f);
            if (_flood.Ratio != null && hydrology != null && hydrology.River[c] && _flood.Ratio[c] > 1.5f)
                Paint(c, new Color(0.1f, 0.9f, 1f), (_flood.Ratio[c] - 1.5f) / 2f + 0.4f);
        }
        foreach (int c in _calvingCells)
            Paint(c, Colors.White, 0.9f);
        // Événements ponctuels : par-dessus le reste
        Double3 Cell(int c) => Double3.From(_grid.Positions[c]).Normalized();
        double Km(Double3 a, Double3 b) => Math.Acos(Math.Clamp(a.Dot(b), -1, 1)) * PlanetConstants.RadiusKm;
        foreach (var wave in _tsunami.Waves)
        {
            double front = TsunamiFrontKm(wave, day);
            for (int c = 0; c < n; c++)
            {
                double km = Km(Cell(c), wave.Origin);
                if (Math.Abs(km - front) < 1.2 * cellKm)
                    Paint(c, new Color(0.7f, 0.95f, 1f), 1f - (float)(Math.Abs(km - front) / (1.2 * cellKm)));
                else if (km < 1.2 * cellKm)
                    Paint(c, new Color(0.2f, 0.4f, 1f), 1f);
            }
        }
        foreach (var cy in _cyclones.Cyclones)
            for (int c = 0; c < n; c++)
            {
                double km = Km(Cell(c), cy.Position);
                if (km < 700)
                {
                    float strength = Mathf.Clamp((float)cy.MaxWind / 70f, 0.2f, 1f);
                    Paint(c, new Color(0.55f + 0.45f * strength, 0.25f, 0.95f), 1f - (float)(km / 700));
                }
            }
        foreach (var (where, _, _) in _recentQuakes)
            Paint(_grid.NearestCell(where, 0), new Color(1f, 0.92f, 0.2f), 1f);
        foreach (var (_, where, when) in _recentSlides)
            if (day - when < 30)
                Paint(_grid.NearestCell(where.ToVector3(), 0), new Color(0.6f, 0.4f, 0.22f), 1f);
        foreach (var eruption in _activity.Eruptions)
        {
            int c = _grid.NearestCell(eruption.Position, 0);
            var color = eruption.Fissure ? new Color(1f, 0.6f, 0.1f) : new Color(1f, 0.12f, 0.08f);
            Paint(c, color, 1f);
            foreach (int nb in _grid.Neighbors[c])
                Paint(nb, color, 0.5f);
        }
        return colors;
    }
}
