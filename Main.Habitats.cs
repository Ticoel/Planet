using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Milieux hydrothermaux (étape 14a) dans le jeu : la liste suit la planète (volcans, dorsales,
/// pluie), les plus proches de la caméra sont peints sur le sol (bassins, dépôts, tapis
/// microbiens) et fument (vapeur, jets des geysers), et l'interface les montre : couche, sonde,
/// courbe, réglage de leur abondance.
/// </summary>
public partial class Main
{
    /// <summary>Distance (km) jusqu'à laquelle les milieux sont peints sur le sol, et fument.</summary>
    private const double HabitatPaintRangeKm = 40, HabitatSteamRangeKm = 15;
    private const double HabitatRefreshSeconds = 1.0;

    /// <summary>Abondance des milieux hydrothermaux par rapport à la Terre (réglage du joueur).</summary>
    public double HydrothermalAbundance { get; set; } = 1.0;

    private VolcanicHabitats _habitats;
    private TectonicPlates _habitatSource;
    private double _habitatTimeMyr = double.NaN, _habitatAbundance = double.NaN;
    private bool _habitatClimateRain;
    private double _habitatTimer;
    // Milieux proches de la caméra (liste refaite quatre fois par seconde), posés sur le relief
    private Habitat[] _nearHabitats = Array.Empty<Habitat>();
    private int _nearHabitatCountdown;
    private readonly Dictionary<Habitat, Double3> _habitatAnchors = new();
    private TerrainHeightField _habitatAnchorField;
    private readonly Vector4[] _habitatSites = new Vector4[16];
    private readonly Vector4[] _habitatKinds = new Vector4[16];
    private readonly HashSet<Vector3> _visitedHabitats = new();

    /// <summary>Recalcule la liste des milieux quand la planète, le climat ou le réglage ont changé.</summary>
    private void EnsureHabitats(double delta)
    {
        _habitatTimer -= delta;
        bool climateRain = _clock == ClockMode.Climate && _climate.HasTemperature && _climate.Statistics.CompletedYears > 0;
        bool changed = !ReferenceEquals(_habitatSource, _tectonics) || _habitatAbundance != HydrothermalAbundance
                       || _habitatClimateRain != climateRain;
        // (curseur d'abondance en cours de réglage : au plus un recalcul par seconde)
        if (ReferenceEquals(_habitatSource, _tectonics) && _habitatClimateRain == climateRain && _habitatTimer > 0)
            return;
        // (pendant que la tectonique tourne : au plus une fois par seconde)
        if (!changed && (_habitatTimeMyr == _tectonics.TimeMyr || _habitatTimer > 0))
            return;
        // Abondance changée : les replats des bassins changent aussi, le relief est recalculé
        if (_habitatSource != null && _habitatAbundance != HydrothermalAbundance && !double.IsNaN(_habitatAbundance))
        {
            if (_clock == ClockMode.Climate)
                RebuildTerrainNow();
            else
                _terrainTimeMyr = double.NaN;
        }
        _habitatTimer = HabitatRefreshSeconds;
        _habitatSource = _tectonics;
        _habitatTimeMyr = _tectonics.TimeMyr;
        _habitatAbundance = HydrothermalAbundance;
        _habitatClimateRain = climateRain;
        // Pluie : celle du climat simulé quand une année complète est connue, sinon l'estimation
        float[] rain = climateRain ? _climate.Statistics.AnnualPrecipitation : _tectonics.AnnualPrecipitation;
        _habitats = new VolcanicHabitats(_tectonics, rain, HydrothermalAbundance);
        _habitatAnchors.Clear();
        _nearHabitatCountdown = 0;
        if (_displayMode == DisplayMode.VolcanicHabitats)
            _colorsDirty = true;
    }

    /// <summary>
    /// Replats des bassins à creuser dans le relief détaillé (l'eau ne tient pas sur une pente) :
    /// un par milieu émergé. Les emplacements ne dépendent que des volcans et de l'abondance.
    /// </summary>
    private TerrainBench[] HabitatBenches() =>
        new VolcanicHabitats(_tectonics, _tectonics.AnnualPrecipitation, HydrothermalAbundance).Benches();

    /// <summary>Point du sol où se trouve un milieu : le fond de son bassin, sur le relief détaillé.</summary>
    private Double3 Anchor(Habitat habitat)
    {
        if (_habitatAnchors.TryGetValue(habitat, out Double3 anchor))
            return anchor;
        Double3 direction = Double3.From(habitat.Position).Normalized();
        int hint = -1;
        double height = _terrain.Field.Height(direction, 2, ref hint);
        anchor = direction * (TerrainHeightField.RadiusMeters + height);
        _habitatAnchors[habitat] = anchor;
        return anchor;
    }

    /// <summary>
    /// Milieux proches : peinture du sol (shader du terrain) et vapeur (places restantes des
    /// panaches, après les éruptions et les fumerolles des volcans).
    /// </summary>
    private void UpdateHabitats(ref int plumeCount, bool climate)
    {
        if (_tectonics == null || _terrain.Field == null || _habitats == null)
        {
            _terrain.SetShaderParameter("habitat_count", 0);
            return;
        }
        if (!ReferenceEquals(_habitatAnchorField, _terrain.Field))
        {
            _habitatAnchorField = _terrain.Field;
            _habitatAnchors.Clear();
            _nearHabitatCountdown = 0;
        }
        Double3 camera = _terrain.Explorer.Position;
        if (--_nearHabitatCountdown <= 0)
        {
            _nearHabitatCountdown = 15;
            Vector3 cameraDirection = camera.Normalized().ToVector3();
            float cosRange = MathF.Cos((float)(HabitatPaintRangeKm / PlanetConstants.RadiusKm));
            _nearHabitats = _terrain.Explorer.AltitudeAboveGround > HabitatPaintRangeKm * 1000
                ? Array.Empty<Habitat>()
                : _habitats.All.Where(h => !h.Submarine && h.Position.Dot(cameraDirection) > cosRange)
                    .OrderByDescending(h => h.Position.Dot(cameraDirection)).Take(_habitatSites.Length).ToArray();
        }

        double minutes = _climate.TimeDays * 1440.0;
        int painted = 0;
        foreach (var habitat in _nearHabitats)
        {
            Double3 anchor = Anchor(habitat);
            Double3 relative = anchor - camera;
            _habitatSites[painted] = new Vector4((float)relative.X, (float)relative.Y, (float)relative.Z, habitat.RadiusM);
            _habitatKinds[painted] = new Vector4((float)habitat.Kind, habitat.TemperatureC, habitat.Ph, habitat.Variation);
            painted++;

            // Vapeur : seulement dans l'horloge climatique (comme les fumerolles des volcans), de près
            if (!climate || plumeCount >= _eruptionVents.Length || relative.Length() > HabitatSteamRangeKm * 1000)
                continue;
            float radius, height, intensity;
            switch (habitat.Kind)
            {
                case HabitatKind.Geyser:
                    // Au repos : un filet de vapeur ; en éruption : le jet et son panache
                    float activity = habitat.GeyserActivity(minutes);
                    radius = 3f + 3f * activity;
                    height = 25f + (habitat.JetHeightM * 2.2f - 25f) * activity;
                    intensity = 0.35f + 0.65f * activity;
                    break;
                case HabitatKind.HotSpring:
                    radius = 0.6f * habitat.RadiusM;
                    height = 30f + 2.5f * Math.Max(0f, habitat.TemperatureC - 40f);
                    intensity = 0.25f + 0.35f * Math.Clamp((habitat.TemperatureC - 40f) / 55f, 0f, 1f);
                    break;
                case HabitatKind.MudPot:
                    radius = 0.5f * habitat.RadiusM;
                    height = 35f;
                    intensity = 0.3f;
                    break;
                default:
                    radius = 0.25f * habitat.RadiusM;
                    height = 120f + 0.6f * Math.Min(habitat.TemperatureC, 450f);
                    intensity = 0.55f;
                    break;
            }
            Vector3 up = anchor.Normalized().ToVector3();
            _eruptionVents[plumeCount] = new Vector4(up.X, up.Y, up.Z, intensity);
            _eruptionShapes[plumeCount] = new Vector4(radius, height, -2f, (float)(anchor.Length() - TerrainHeightField.RadiusMeters));
            Vector3 wind = _climate.Winds.Wind[habitat.Cell];
            float speed = wind.Length();
            // (vent au sol : la vapeur des sources se couche moins que le panache d'un volcan)
            _eruptionWinds[plumeCount] = speed < 1e-3f ? Vector4.Zero : new Vector4(wind.X / speed, wind.Y / speed, wind.Z / speed, speed);
            plumeCount++;
        }
        _terrain.SetShaderParameter("habitat_count", painted);
        _terrain.SetShaderParameter("habitat_sites", _habitatSites);
        _terrain.SetShaderParameter("habitat_kinds", _habitatKinds);
    }

    // --- Couche ----------------------------------------------------------------------------------------

    private static readonly Color[] HabitatColors =
    {
        new(0.95f, 0.85f, 0.15f),   // fumerolles
        new(1f, 0.5f, 0.1f),        // sources chaudes
        new(0.55f, 0.38f, 0.22f),   // mares de boue
        new(1f, 0.3f, 0.05f),       // geysers
        new(0.2f, 0.8f, 0.5f),      // évents côtiers
        new(0.7f, 0.05f, 0.1f),     // fumeurs noirs
        new(0.3f, 0.9f, 1f),        // sources alcalines
    };

    /// <summary>Couleurs des cellules pour une couche (celle des milieux est calculée ici).</summary>
    private Color[] LayerColors(DisplayMode mode)
    {
        if (mode != DisplayMode.VolcanicHabitats)
            return PlanetColorizer.Compute(_tectonics, _climate, mode);
        // Relief en gris, les cellules qui abritent des milieux en couleur (celle du plus chaud)
        var colors = PlanetColorizer.Compute(_tectonics, _climate, DisplayMode.Elevation);
        for (int c = 0; c < colors.Length; c++)
        {
            float grey = colors[c].Luminance * 0.45f + (_tectonics.CellElevation[c] > 0 ? 0.2f : 0.05f);
            colors[c] = new Color(grey, grey, grey);
            if (_habitats == null)
                continue;
            Habitat hottest = null;
            foreach (int i in _habitats.InCell(c))
                if (hottest == null || _habitats.All[i].TemperatureC > hottest.TemperatureC)
                    hottest = _habitats.All[i];
            if (hottest != null)
                colors[c] = HabitatColors[(int)hottest.Kind];
        }
        return colors;
    }

    // --- Interface ---------------------------------------------------------------------------------------

    private void RegisterHabitats()
    {
        var registry = _ui.Registry;
        registry.Add(new LayerEntry
        {
            Group = "Milieux", Label = "Milieux hydrothermaux",
            Help = "Jaune : fumerolles ; orange : sources chaudes, geysers ; brun : mares de boue ; rouge sombre : fumeurs noirs ; cyan : sources alcalines ; vert : évents côtiers",
            IsActive = () => _displayMode == DisplayMode.VolcanicHabitats,
            Activate = () => SetDisplayMode(DisplayMode.VolcanicHabitats),
        });
        registry.Add(new ProbeField { Label = "Milieux hydrothermaux", Read = cell => _habitats?.Describe(cell) });
        registry.Add(new TimeSeries
        {
            Label = "Milieux sur terre", Format = "0", Color = (1f, 0.6f, 0.2f), Shown = false,
            Sample = () => _habitats?.LandCount,
        });
        registry.Add(new TimeSeries
        {
            Label = "Milieux sous la mer", Format = "0", Color = (0.3f, 0.9f, 1f), Shown = false,
            Sample = () => _habitats?.SubmarineCount,
        });
        registry.Add(new SimParameter
        {
            Id = "volcano.hydrothermal", Group = "Volcanisme", Label = "Milieux hydrothermaux", Unit = "×", Min = 0, Max = 3, Step = 0.1, Reference = 1, Format = "0.0",
            Help = "Abondance des fumerolles, sources chaudes, geysers et sources sous-marines par rapport à la Terre (0 : aucun).",
            Get = () => HydrothermalAbundance, Set = v => HydrothermalAbundance = v,
        });
    }

    /// <summary>Touche J : emmène la caméra à la source chaude ou au geyser suivant, du plus proche au plus lointain.</summary>
    private void GoToNextHabitat()
    {
        if (_terrain.Field == null || _habitats == null)
            return;
        // (la vapeur et les jets ne se voient que dans l'horloge climatique)
        if (_clock != ClockMode.Climate)
            SetClock(ClockMode.Climate);
        EnsureHabitats(0);
        Vector3 camera = _terrain.Explorer.Position.Normalized().ToVector3();
        var all = _habitats.All.Where(h => h.Kind is HabitatKind.HotSpring or HabitatKind.Geyser).ToArray();
        // Chaque appui passe au plus proche pas encore visité (puis le tour recommence)
        if (all.All(h => _visitedHabitats.Contains(h.Position)))
            _visitedHabitats.Clear();
        var sites = all.Where(h => !_visitedHabitats.Contains(h.Position))
            .OrderByDescending(h => h.Position.Dot(camera)).Take(1).ToArray();
        if (sites.Length == 0)
        {
            ShowNotice("Ni source chaude ni geyser sur cette planète pour l'instant (il faut des volcans actifs et de la pluie)");
            return;
        }
        var habitat = sites[0];
        _visitedHabitats.Add(habitat.Position);
        Double3 anchor = Anchor(habitat);
        Double3 site = anchor.Normalized();
        Double3 east = Double3.UnitY.Cross(site).Normalized();
        Double3 north = site.Cross(east);
        // De côté, à quelques rayons du bassin, à hauteur d'homme : on voit le bassin et sa vapeur
        double distance = Math.Max(90, 7.0 * habitat.RadiusM);
        var explorer = _terrain.Explorer;
        explorer.PlaceAbove((site - (east * 0.8 + north * 0.6).Normalized() * (distance / TerrainHeightField.RadiusMeters)).Normalized(), 0.22 * distance);
        explorer.Face(site - explorer.Position.Normalized(), -0.2);
        string detail = habitat.Kind == HabitatKind.Geyser
            ? $"geyser, jet de {habitat.JetHeightM:0} m toutes les {habitat.PeriodMinutes:0} min"
            : $"source chaude, {habitat.TemperatureC:0} °C, pH {French(habitat.Ph)}";
        ShowNotice($"Lieu : {detail}");
    }
}
