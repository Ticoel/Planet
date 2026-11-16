using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Terrain;

namespace Planet;

/// <summary>
/// Touche ² (sous Échap ; ` en QWERTY) ou bouton « Rapport » : rapport de bug. Une capture de l'écran tel qu'il est (interface comprise) et un
/// fichier texte à côté (même nom) qui décrit l'état du jeu à cet instant : caméra, horloge, sol
/// sous la caméra, terrain, relief en volume, grottes, événements, et les dernières lignes du
/// journal de Godot (erreurs, avertissements). Les deux fichiers sont à joindre au message.
/// </summary>
public partial class Main
{
    private void TakeBugReport()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string directory = CaptureManager.CaptureDirectory();
        string imagePath = Path.Combine(directory, $"rapport_{stamp}.png");
        string textPath = Path.Combine(directory, $"rapport_{stamp}.txt");
        try
        {
            GetViewport().GetTexture().GetImage().SavePng(imagePath);
            File.WriteAllText(textPath, BugReport(stamp), Encoding.UTF8);
            ShowNotice($"Rapport enregistré (capture et texte) : {textPath}");
        }
        catch (Exception e)
        {
            ShowNotice($"Rapport impossible : {e.Message}");
        }
    }

    private string BugReport(string stamp)
    {
        var text = new StringBuilder();
        void Section(string title) => text.AppendLine().AppendLine($"== {title} ==");
        void Safe(string label, Func<string> read)
        {
            try { text.AppendLine($"{label} : {read()}"); }
            catch (Exception e) { text.AppendLine($"{label} : (erreur : {e.GetType().Name} {e.Message})"); }
        }
        var explorer = _terrain.Explorer;
        Double3 position = explorer.Position;
        Double3 up = position.Normalized();
        Vector3 forward = explorer.Forward().ToVector3();

        text.AppendLine($"Rapport de bug Planet {ProjectSettings.GetSetting("application/config/version")} — {stamp}");
        Safe("Godot", () => Engine.GetVersionInfo()["string"].AsString());
        Safe("Système", () => $"{OS.GetName()}, carte graphique : {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterVendor()})");
        Safe("Écran", () => $"{GetViewport().GetVisibleRect().Size}, {Engine.GetFramesPerSecond():0} images/s");

        Section("Horloge");
        Safe("Horloge", () => $"{_clock}, en marche : {_running}, vitesse : {(_clock == ClockMode.Climate ? ClimateSpeeds[_climateSpeed] + " jours/s" : "géologique")}");
        Safe("Temps", () => $"{_tectonics.TimeMyr:0.0} Ma ; climat : jour {_climate.TimeDays:0.00} (année {_climate.TimeDays / 365.25:0.00})");
        Safe("Planète", () => $"rayon {PlanetConstants.RadiusKm:0} km, graine {Seed}, {_grid.CellCount} cellules, {_tectonics.AlivePlateCount} plaques");
        Safe("Couche", () => $"{_displayMode} ; qualité {QualityLevels[_quality].Label}");

        Section("Caméra");
        Safe("Position", () =>
        {
            double lat = Math.Asin(Math.Clamp(up.Y, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(up.X, up.Z) * 180 / Math.PI;
            return $"latitude {lat:0.0000}°, longitude {lon:0.0000}° ; direction ({up.X:0.000000}, {up.Y:0.000000}, {up.Z:0.000000})";
        });
        Safe("Hauteur", () => $"{explorer.AltitudeAboveGround:0.00} m au-dessus du sol (sol à {explorer.GroundHeight:0.00} m)");
        Safe("Regard", () => $"cap {explorer.Yaw * 180 / Math.PI:0.0}°, inclinaison {explorer.Pitch * 180 / Math.PI:0.0}°, axe ({forward.X:0.000}, {forward.Y:0.000}, {forward.Z:0.000})");
        Safe("Mode", () => $"{(explorer.Walking ? "marche" : "vol")}, dans une grotte : {explorer.InCave}, en transition : {explorer.InTransition}, vitesse {explorer.Speed:0.0} m/s");

        Section("Sol sous la caméra");
        Safe("Relief détaillé", () =>
        {
            int hint = -1;
            var field = _terrain.Field;
            if (field == null)
                return "aucun";
            var sample = field.Sample(up, 0.5, ref hint);
            return $"hauteur {sample.Height:0.00} m, eau {sample.WaterLevel:0.00} m, fleuve {sample.River:0.00}, croûte {sample.Debris:0.00} (jour {sample.DebrisDay:0}), " +
                   $"vasière {sample.Wetland:0.00}, cellule {hint}, près de la mer : {field.NearOcean(hint)}";
        });
        Safe("Cellule", () =>
        {
            int cell = _grid.NearestCell(up.ToVector3(), 0);
            string climate = _climate.HasTemperature
                ? $", {_climate.SurfaceTemperature[cell]:0.0} °C, neige {_climate.Snow[cell]:0} mm, pluie récente {_climate.Statistics.RecentPrecipitation[cell]:0.0} mm/j"
                : "";
            return $"n° {cell}, altitude {_tectonics.CellElevation[cell] * 1000:0} m{climate}";
        });
        Safe("Eau souterraine", () => _groundwater?.Describe(_grid.NearestCell(up.ToVector3(), 0)) ?? "—");

        Section("Terrain");
        Safe("Morceaux", () => $"{_terrain.VisibleChunkCount} visibles (niveau max {_terrain.DeepestVisibleLevel}), {_terrain.CachedChunkCount} en mémoire, " +
                               $"{_terrain.PendingChunkCount} en calcul, {_terrain.StaleChunkCount} en mise à jour, {_terrain.StaleRegionChunkCount} touchés par la croûte");
        Safe("Relief en volume", () => (_volumeRelief ? "" : "(désactivé par Maj + G) ") + _terrain.VolumeReport());

        Section("Grottes");
        Safe("Emplacements", () => $"{_caves.Sites.Count} ({_caves.Sites.Count(s => s.Kind == CaveKind.Karst)} karstiques), {_caves.Ready.Count} tracées près de la caméra");
        foreach (var cave in _caves.Ready.OrderBy(c => c.Origin.DistanceTo(position)).Take(4))
            Safe("  Grotte", () => $"{cave.Kind}, entrée à {cave.Origin.DistanceTo(position):0} m, {cave.LengthM:0} m de galeries, jusqu'à {cave.MaxDepthM:0} m sous terre ; " +
                                   $"espace à la caméra : {cave.SpaceAt(position)?.ToString() ?? "aucun"}");

        Section("Détails de la simulation");
        Safe("Lignes", () =>
        {
            var lines = new System.Collections.Generic.List<string>();
            DetailLines(lines);
            return "\n" + string.Join("\n", lines);
        });

        Section("Événements");
        Safe("En cours", () => $"{_activity.Eruptions.Count} éruptions, {_cyclones.Cyclones.Count} cyclones, {_tsunami.Waves.Count} tsunamis, crue {_flood.RiseM:0.00} m, " +
                               $"{_icebergs.Count} icebergs, {_calvingCells.Length} fronts de glaciers à la mer");

        Section("Journal de Godot (fin)");
        Safe("Fichier", () =>
        {
            string log = Path.Combine(OS.GetUserDataDir(), "logs", "godot.log");
            if (!File.Exists(log))
                return "introuvable (journalisation dans un fichier désactivée)";
            using var stream = new FileStream(log, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            return log + "\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 80)));
        });
        return text.ToString();
    }
}
