using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Planet.Simulation;

namespace Planet.Rendering;

/// <summary>
/// Photos, vidéos et cartes prises dans la simulation, rangées dans le dossier Images/Planet
/// (ou, à défaut, dans le dossier des données de Godot) :
/// <list type="bullet">
/// <item><b>Photo</b> : l'image affichée, sans le texte d'interface, en PNG à la pleine résolution ;</item>
/// <item><b>Vidéo</b> : AVI « Motion JPEG » à 30 images par seconde (au plus 1920 pixels de large),
/// encodé en tâche de fond ; si l'affichage est plus lent, des images sont répétées pour que la
/// durée reste celle du temps réel ;</item>
/// <item><b>Carte</b> : la couche affichée à l'instant t, en projection équirectangulaire
/// (longitude en abscisse, latitude en ordonnée), 2048 × 1024 pixels.</item>
/// </list>
/// </summary>
public sealed partial class CaptureManager : Node
{
    private const int VideoFps = 30;
    private const int MaxVideoWidth = 1920;
    private const float JpegQuality = 0.88f;

    private int _photoCountdown = -1;
    private AviWriter _video;
    private FileStream _videoFile;
    private string _videoPath;
    private double _videoSeconds;
    private int _videoFrames;
    private int _videoWidth, _videoHeight;
    private BlockingCollection<(Image image, int repeats)> _videoQueue;
    private Task _videoWorker;

    /// <summary>Appelé pour masquer (false) ou remettre (true) le texte d'interface.</summary>
    public Action<bool> SetInterfaceVisible { get; set; }
    /// <summary>Message à afficher (fichier enregistré…).</summary>
    public Action<string> Notify { get; set; }

    public bool IsRecording => _video != null;

    /// <summary>Dossier des captures (créé au besoin).</summary>
    public static string CaptureDirectory()
    {
        string pictures = OS.GetSystemDir(OS.SystemDir.Pictures);
        string directory = string.IsNullOrEmpty(pictures)
            ? ProjectSettings.GlobalizePath("user://captures")
            : Path.Combine(pictures, "Planet");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

    /// <summary>Photo : l'interface est masquée, l'image est prise deux images plus tard.</summary>
    public void TakePhoto()
    {
        if (_photoCountdown >= 0)
            return;
        SetInterfaceVisible?.Invoke(false);
        _photoCountdown = 2;
    }

    public void ToggleVideo()
    {
        if (IsRecording)
            StopVideo();
        else
            StartVideo();
    }

    private void StartVideo()
    {
        Vector2 size = GetViewport().GetTexture().GetSize();
        float scale = MathF.Min(1f, (float)MaxVideoWidth / size.X);
        // Dimensions paires (exigées par la plupart des lecteurs et des convertisseurs)
        _videoWidth = (int)(size.X * scale) & ~1;
        _videoHeight = (int)(size.Y * scale) & ~1;
        _videoPath = Path.Combine(CaptureDirectory(), $"planet_{Stamp()}.avi");
        _videoFile = new FileStream(_videoPath, FileMode.Create, System.IO.FileAccess.Write);
        _video = new AviWriter(_videoFile, _videoWidth, _videoHeight, VideoFps);
        _videoSeconds = 0;
        _videoFrames = 0;
        _videoQueue = new BlockingCollection<(Image, int)>(boundedCapacity: 8);
        var queue = _videoQueue;
        var writer = _video;
        int width = _videoWidth, height = _videoHeight;
        // Redimensionnement et compression JPEG hors du fil principal
        _videoWorker = Task.Run(() =>
        {
            foreach (var (image, repeats) in queue.GetConsumingEnumerable())
            {
                if (image.GetWidth() != width || image.GetHeight() != height)
                    image.Resize(width, height, Image.Interpolation.Bilinear);
                byte[] jpeg = image.SaveJpgToBuffer(JpegQuality);
                for (int r = 0; r < repeats; r++)
                    writer.AddFrame(jpeg);
            }
        });
        SetInterfaceVisible?.Invoke(false);
    }

    private void StopVideo()
    {
        _videoQueue.CompleteAdding();
        _videoWorker.Wait();
        _video.Dispose();
        _videoFile.Dispose();
        int frames = _video.FrameCount;
        _video = null;
        _videoFile = null;
        SetInterfaceVisible?.Invoke(true);
        Notify?.Invoke($"Vidéo enregistrée ({frames / (double)VideoFps:0.0} s) : {_videoPath}");
    }

    public override void _Process(double delta)
    {
        if (_photoCountdown > 0)
        {
            _photoCountdown--;
        }
        else if (_photoCountdown == 0)
        {
            _photoCountdown = -1;
            Image image = GetViewport().GetTexture().GetImage();
            string path = Path.Combine(CaptureDirectory(), $"planet_{Stamp()}.png");
            SetInterfaceVisible?.Invoke(!IsRecording);
            Task.Run(() => image.SavePng(path));
            Notify?.Invoke($"Photo enregistrée : {path}");
        }

        if (IsRecording)
        {
            // Autant d'images que le temps écoulé en demande (répétées si l'affichage est lent)
            _videoSeconds += delta;
            int due = (int)(_videoSeconds * VideoFps) - _videoFrames;
            if (due > 0)
            {
                Image image = GetViewport().GetTexture().GetImage();
                _videoQueue.Add((image, due));
                _videoFrames += due;
            }
        }
    }

    public override void _ExitTree()
    {
        if (IsRecording)
            StopVideo();
    }

    /// <summary>
    /// Carte équirectangulaire des couleurs des cellules (une couleur par cellule), 2048 × 1024 :
    /// chaque pixel prend la couleur de la cellule la plus proche. Calculée en tâche de fond.
    /// </summary>
    public void ExportMap(PlanetGrid grid, Color[] colors, string label)
    {
        const int width = 2048, height = 1024;
        string path = Path.Combine(CaptureDirectory(), $"carte_{label}_{Stamp()}.png");
        var colorsCopy = (Color[])colors.Clone();
        Notify?.Invoke("Carte en cours de calcul…");
        Task.Run(() =>
        {
            try
            {
                var pixels = new byte[width * height * 3];
                System.Threading.Tasks.Parallel.For(0, height, j =>
                {
                    double latitude = Math.PI / 2 - (j + 0.5) / height * Math.PI;
                    int hint = 0;
                    for (int i = 0; i < width; i++)
                    {
                        // Longitude 0 au centre, l'est à droite (repère de la planète : l'est vers −Z)
                        double longitude = (i + 0.5) / width * 2 * Math.PI - Math.PI;
                        var direction = new Vector3((float)(Math.Cos(latitude) * Math.Cos(longitude)), (float)Math.Sin(latitude),
                                                    (float)(-Math.Cos(latitude) * Math.Sin(longitude)));
                        hint = grid.NearestCell(direction, hint);
                        Color c = colorsCopy[hint];
                        int o = (j * width + i) * 3;
                        pixels[o] = (byte)c.R8;
                        pixels[o + 1] = (byte)c.G8;
                        pixels[o + 2] = (byte)c.B8;
                    }
                });
                Error error = Image.CreateFromData(width, height, false, Image.Format.Rgb8, pixels).SavePng(path);
                string message = error == Error.Ok ? $"Carte enregistrée : {path}" : $"Carte non enregistrée ({error}) : {path}";
                Callable.From(() => Notify?.Invoke(message)).CallDeferred();
            }
            catch (Exception exception)
            {
                // (message sur le fil principal : l'interface ne se touche pas depuis une tâche de fond)
                Callable.From(() => Notify?.Invoke($"Carte non enregistrée : {exception.Message}")).CallDeferred();
            }
        });
    }
}
