using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>
/// Relief en volume autour de la caméra (étape 18, voir <see cref="VolumeChunkBuilder"/>) : des
/// blocs de 16 m calculés en tâche de fond, dessinés avec le matériau du terrain (mêmes couleurs,
/// neige, eau, croûte remaniée), qui remplacent le relief détaillé à moins d'une centaine de
/// mètres de la caméra (le shader du terrain n'y dessine plus la surface : masque des blocs
/// prêts). Une couronne de blocs en plus, au-delà du masque, est dessinée un peu enfoncée sous
/// la surface : elle bouche les fentes du raccord sans se battre avec elle.
/// <para>Le volume ne remplace la surface que là où elle est déjà à son plus fin : sur un relief
/// encore grossier (juste après un saut de la caméra), les deux ne se raccordaient pas (marches,
/// éclats). Dans une grotte, toute la grotte est construite et masquée, pas seulement les
/// environs de la caméra : sinon, au-delà, les jupes des morceaux de terrain pendaient dans le
/// tunnel et le relief du dessus se voyait par en dessous.</para>
/// </summary>
public partial class TerrainRenderer
{
    private const int VolumeMaskedRadius = 5;          // blocs (≈ 90 m) qui remplacent la surface
    private const int VolumeDrawnRadius = 6;           // plus une couronne enfoncée
    private const int VolumeMaskSize = 64;             // masque : 64 × 64 blocs (≈ 1 km) autour de la caméra
    private const int VolumeMaxCaveChunks = 400;       // blocs d'une grotte construits au plus
    private const double VolumeMaxAltitude = 250;      // au-dessus, la surface suffit
    private const double VolumeReanchorM = 1500;
    private const int VolumeBuilders = 3;

    private sealed class VolumeEntry
    {
        public int I, J;
        public Task<VolumeChunkMesh> Build;
        public bool Rebuild, Built;
        // Construction échouée : pas retentée à chaque image (jusqu'à une modification du relief)
        public bool Failed;
        public Double3 Center;
        public Rid Mesh, Instance;
        public bool Shown = true;
    }

    // Blocs de la grotte où se trouve la caméra (du plus proche au plus lointain), pour quel repère
    // et quelle version des grottes
    private (int i, int j)[] _caveChunks = Array.Empty<(int, int)>();
    private readonly HashSet<(int, int)> _caveChunkSet = new();
    private (VolumeFrame frame, int version, Cave cave) _caveChunksFor;

    private VolumeFrame _volumeFrame;
    private TerrainHeightField _volumeField;
    private int _volumeCaveVersion = -1;
    private readonly Dictionary<(int, int), VolumeEntry> _volume = new();
    private Image _volumeMaskImage;
    private ImageTexture _volumeMaskTexture;
    private byte[] _volumeMaskBytes = new byte[VolumeMaskSize * VolumeMaskSize];
    private byte[] _volumeMaskNext = new byte[VolumeMaskSize * VolumeMaskSize];

    /// <summary>Décalages des blocs voulus autour de la caméra, du plus proche au plus lointain (calculés une fois).</summary>
    private static readonly (int di, int dj)[] VolumeOffsets = BuildVolumeOffsets();

    private static (int, int)[] BuildVolumeOffsets()
    {
        var offsets = new List<(int di, int dj)>();
        for (int dj = -VolumeDrawnRadius; dj <= VolumeDrawnRadius; dj++)
            for (int di = -VolumeDrawnRadius; di <= VolumeDrawnRadius; di++)
                if (di * di + dj * dj <= VolumeDrawnRadius * VolumeDrawnRadius + 1)
                    offsets.Add((di, dj));
        offsets.Sort((a, b) => (a.di * a.di + a.dj * a.dj).CompareTo(b.di * b.di + b.dj * b.dj));
        return offsets.ToArray();
    }

    /// <summary>Relief en volume autorisé (horloge climatique : le relief ne change plus que par la croûte modulaire).</summary>
    public bool VolumeEnabled { get; set; }
    /// <summary>Grottes à creuser dans le volume, et leur version (change quand une grotte arrive ou part).</summary>
    public IReadOnlyList<Cave> VolumeCaves { get; private set; } = Array.Empty<Cave>();
    private int _requestedCaveVersion;

    public void SetVolumeCaves(IReadOnlyList<Cave> caves, int version)
    {
        VolumeCaves = caves;
        _requestedCaveVersion = version;
    }

    /// <summary>Blocs de volume affichés.</summary>
    public int VolumeChunkCount => _volume.Values.Count(e => e.Instance.IsValid);
    /// <summary>État du relief en volume, pour le rapport de bug (touche ²).</summary>
    public string VolumeReport()
    {
        if (_volumeFrame == null)
            return $"inactif (autorisé : {VolumeEnabled}, altitude {Explorer.AltitudeAboveGround:0.0} m, transition : {Explorer.InTransition})";
        Double3 local = _volumeFrame.ToLocal(Explorer.Position);
        int ci = (int)Math.Floor(local.X / VolumeChunkBuilder.ChunkM), cj = (int)Math.Floor(local.Y / VolumeChunkBuilder.ChunkM);
        var text = new System.Text.StringBuilder();
        text.AppendLine($"actif ; ancrage à {_volumeFrame.Origin.DistanceTo(Explorer.Position):0} m ; caméra dans le bloc ({ci}, {cj}), " +
                        $"position locale ({local.X:0.0}, {local.Y:0.0}, {local.Z:0.0})");
        text.AppendLine($"blocs : {_volume.Count} suivis, {_volume.Values.Count(e => e.Built)} construits, {VolumeChunkCount} dessinés, " +
                        $"{_volume.Values.Count(e => e.Built && !e.Instance.IsValid)} vides, {_volume.Values.Count(e => e.Build != null)} en calcul, " +
                        $"{VolumeMaskedCount} remplacent la surface ; grottes creusées : {VolumeCaves.Count}");
        // Carte des blocs autour de la caméra : # masqué et dessiné, + dessiné hors masque,
        // o construit vide, ~ en calcul, . absent ; C : caméra
        int corner = VolumeMaskSize / 2;
        for (int dj = VolumeDrawnRadius + 1; dj >= -VolumeDrawnRadius - 1; dj--)
        {
            text.Append("  ");
            for (int di = -VolumeDrawnRadius - 1; di <= VolumeDrawnRadius + 1; di++)
            {
                char c = '.';
                if (_volume.TryGetValue((ci + di, cj + dj), out var e))
                {
                    int mi = di + corner, mj = dj + corner;
                    bool masked = mi >= 0 && mj >= 0 && mi < VolumeMaskSize && mj < VolumeMaskSize && _volumeMaskBytes[mj * VolumeMaskSize + mi] != 0;
                    c = e.Build != null && !e.Built ? '~' : e.Instance.IsValid ? (masked ? '#' : '+') : e.Built ? 'o' : '.';
                }
                text.Append(di == 0 && dj == 0 ? 'C' : c);
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Blocs qui remplacent la surface (masque du shader).</summary>
    public int VolumeMaskedCount => _volumeMaskBytes.Count(b => b != 0);

    private void SetupVolume()
    {
        _volumeMaskImage = Image.CreateEmpty(VolumeMaskSize, VolumeMaskSize, false, Image.Format.R8);
        _volumeMaskTexture = ImageTexture.CreateFromImage(_volumeMaskImage);
        _material.SetShaderParameter("volume_mask", _volumeMaskTexture);
        _material.SetShaderParameter("volume_enabled", false);
    }

    private void UpdateVolume()
    {
        Double3 camera = Explorer.Position;
        // (hauteur au-dessus de la roche, pas de l'eau : au-dessus de la mer, le fond est loin)
        bool wanted = VolumeEnabled && Field != null && !Explorer.InTransition && HeightAboveRock() < VolumeMaxAltitude;
        if (!wanted)
        {
            if (_volume.Count > 0)
                ClearVolume();
            _material.SetShaderParameter("volume_enabled", false);
            return;
        }
        // Nouveau relief, ou caméra loin de l'ancrage : nouveau repère, tout est refait
        if (_volumeFrame == null || Field != _volumeField || _volumeFrame.Origin.DistanceTo(camera) > VolumeReanchorM)
        {
            ClearVolume();
            _volumeField = Field;
            _volumeFrame = new VolumeFrame(camera.Normalized(), Explorer.GroundHeight);
        }
        // Nouvelles grottes : les blocs qu'elles touchent sont refaits
        if (_requestedCaveVersion != _volumeCaveVersion)
        {
            _volumeCaveVersion = _requestedCaveVersion;
            foreach (var entry in _volume.Values)
                foreach (var cave in VolumeCaves)
                    if (entry.Center.DistanceTo(cave.Origin) < cave.BoundingRadius + VolumeChunkBuilder.ChunkM * 1.5)
                        entry.Rebuild = true;
        }

        Double3 local = _volumeFrame.ToLocal(camera);
        int ci = (int)Math.Floor(local.X / VolumeChunkBuilder.ChunkM), cj = (int)Math.Floor(local.Y / VolumeChunkBuilder.ChunkM);
        UpdateCaveChunks(camera, ci, cj);

        // Blocs arrivés
        foreach (var entry in _volume.Values)
        {
            if (entry.Build == null || !entry.Build.IsCompleted)
                continue;
            if (entry.Build.IsCompletedSuccessfully)
                ShowVolumeChunk(entry, entry.Build.Result);
            else
            {
                entry.Failed = true;
                GD.PushWarning($"Relief en volume : {entry.Build.Exception?.GetBaseException().Message}");
            }
            entry.Build = null;
        }
        // Blocs à calculer, les plus proches d'abord
        int building = 0;
        foreach (var entry in _volume.Values)
            if (entry.Build != null)
                building++;
        foreach (var (i, j) in WantedVolumeChunks(ci, cj))
        {
            if (building >= VolumeBuilders)
                break;
            if (!_volume.TryGetValue((i, j), out var entry))
                _volume[(i, j)] = entry = new VolumeEntry { I = i, J = j };
            else if (entry.Build != null || (entry.Built && !entry.Rebuild) || (entry.Failed && !entry.Rebuild))
                continue;
            entry.Rebuild = false;
            entry.Failed = false;
            var frame = _volumeFrame;
            var field = Field;
            var caves = VolumeCaves;
            entry.Build = Task.Run(() => VolumeChunkBuilder.Build(frame, i, j, field, caves));
            building++;
        }
        // Blocs trop loin : libérés
        foreach (var key in _volume.Keys.ToArray())
        {
            var entry = _volume[key];
            int di = key.Item1 - ci, dj = key.Item2 - cj;
            if (di * di + dj * dj > (VolumeDrawnRadius + 2) * (VolumeDrawnRadius + 2) && entry.Build == null && !_caveChunkSet.Contains(key))
            {
                FreeVolumeChunk(entry);
                _volume.Remove(key);
            }
        }

        // Position des blocs, et masque des blocs prêts qui remplacent la surface
        var mask = _volumeMaskNext;
        Array.Clear(mask);
        int corner = VolumeMaskSize / 2;
        bool inCave = _caveChunkSet.Count > 0;
        foreach (var entry in _volume.Values)
        {
            if (!entry.Instance.IsValid)
                continue;
            int di = entry.I - ci, dj = entry.J - cj;
            int mi = di + corner, mj = dj + corner;
            bool cave = inCave && _caveChunkSet.Contains((entry.I, entry.J));
            // Surface pas encore à son plus fin : le bloc n'est pas montré (hors de la grotte où
            // l'on est : elle doit rester close)
            bool shown = cave || FineSurfaceAt(entry.Center);
            if (shown != entry.Shown)
            {
                entry.Shown = shown;
                RenderingServer.InstanceSetVisible(entry.Instance, shown);
            }
            if (!shown)
                continue;
            RenderingServer.InstanceSetTransform(entry.Instance, new Transform3D(Godot.Basis.Identity, (entry.Center - camera).ToVector3()));
            // Un bloc dessine de −0,5 à +15,5 m de sa case : la dernière demi-rangée est celle des
            // voisins en x+, y+ et en diagonale. Sans eux, une fente (ni surface ni volume)
            // laissait voir le ciel le long du bord
            if ((cave || di * di + dj * dj <= VolumeMaskedRadius * VolumeMaskedRadius + 1) && mi >= 0 && mj >= 0 && mi < VolumeMaskSize && mj < VolumeMaskSize
                && VolumeChunkBuilt(entry.I + 1, entry.J) && VolumeChunkBuilt(entry.I, entry.J + 1) && VolumeChunkBuilt(entry.I + 1, entry.J + 1))
                mask[mj * VolumeMaskSize + mi] = 255;
        }
        if (!mask.AsSpan().SequenceEqual(_volumeMaskBytes))
        {
            (_volumeMaskBytes, _volumeMaskNext) = (mask, _volumeMaskBytes);
            _volumeMaskImage.SetData(VolumeMaskSize, VolumeMaskSize, false, Image.Format.R8, mask);
            _volumeMaskTexture.Update(_volumeMaskImage);
        }
        _material.SetShaderParameter("volume_enabled", true);
        _material.SetShaderParameter("volume_origin", (_volumeFrame.Origin - camera).ToVector3());
        _material.SetShaderParameter("volume_axis_x", _volumeFrame.AxisX.ToVector3());
        _material.SetShaderParameter("volume_axis_y", _volumeFrame.AxisY.ToVector3());
        _material.SetShaderParameter("volume_corner", new Vector2(ci - corner, cj - corner));
        _material.SetShaderParameter("volume_chunk", (float)VolumeChunkBuilder.ChunkM);
        _material.SetShaderParameter("volume_mask_size", (float)VolumeMaskSize);
    }

    /// <summary>Blocs voulus, les plus proches d'abord : autour de la caméra, puis toute la grotte où elle se trouve.</summary>
    private IEnumerable<(int i, int j)> WantedVolumeChunks(int ci, int cj)
    {
        foreach (var (di, dj) in VolumeOffsets)
            yield return (ci + di, cj + dj);
        foreach (var chunk in _caveChunks)
            yield return chunk;
    }

    /// <summary>
    /// Blocs de la grotte où se trouve la caméra : tous ceux que ses galeries traversent (et une
    /// rangée autour, pour le masque). Refaits quand la caméra change de grotte, de repère, ou
    /// quand les grottes changent.
    /// </summary>
    private void UpdateCaveChunks(Double3 camera, int ci, int cj)
    {
        Cave current = null;
        if (Explorer.InCave)
            foreach (var cave in VolumeCaves)
                if (cave.Contains(cave.ToLocal(camera)))
                {
                    current = cave;
                    break;
                }
        if (current == null)
        {
            _caveChunks = Array.Empty<(int, int)>();
            _caveChunkSet.Clear();
            _caveChunksFor = default;
            return;
        }
        if (_caveChunksFor.frame != _volumeFrame || _caveChunksFor.version != _volumeCaveVersion || _caveChunksFor.cave != current)
        {
            _caveChunksFor = (_volumeFrame, _volumeCaveVersion, current);
            _caveChunkSet.Clear();
            double size = VolumeChunkBuilder.ChunkM;
            foreach (var (a, ra, b, rb) in current.Segments)
            {
                Double3 la = _volumeFrame.ToLocal(current.ToWorld(a)), lb = _volumeFrame.ToLocal(current.ToWorld(b));
                double margin = Math.Max(ra, rb) + 3;
                int i0 = (int)Math.Floor((Math.Min(la.X, lb.X) - margin) / size) - 1, i1 = (int)Math.Floor((Math.Max(la.X, lb.X) + margin) / size) + 1;
                int j0 = (int)Math.Floor((Math.Min(la.Y, lb.Y) - margin) / size) - 1, j1 = (int)Math.Floor((Math.Max(la.Y, lb.Y) + margin) / size) + 1;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        if (_caveChunkSet.Count < VolumeMaxCaveChunks)
                            _caveChunkSet.Add((i, j));
            }
            _caveChunks = _caveChunkSet.ToArray();
        }
        // Les plus proches de la caméra d'abord
        Array.Sort(_caveChunks, (p, q) => ((p.i - ci) * (p.i - ci) + (p.j - cj) * (p.j - cj)).CompareTo((q.i - ci) * (q.i - ci) + (q.j - cj) * (q.j - cj)));
    }

    /// <summary>
    /// La surface affichée à cet endroit est-elle à son plus fin (niveau maximal ou juste
    /// au-dessus) ? Sinon, elle ne se raccorde pas au volume, calculé au mètre près.
    /// </summary>
    private bool FineSurfaceAt(Double3 point)
    {
        var (face, s, t) = CubeSphere.FromDirection(point.Normalized());
        for (int level = Quadtree.MaxLevel; level >= Quadtree.MaxLevel - 1; level--)
        {
            int n = 1 << level;
            var key = new ChunkKey(face, level, Math.Clamp((int)(s * n), 0, n - 1), Math.Clamp((int)(t * n), 0, n - 1));
            if (_cache.TryGetValue(key.Packed, out var entry) && entry.Visible)
                return true;
        }
        return false;
    }

    /// <summary>Bloc construit (vide ou non) : sa part de surface est dessinée.</summary>
    private bool VolumeChunkBuilt(int i, int j) => _volume.TryGetValue((i, j), out var entry) && entry.Built;

    private int _rockHint = -1;

    /// <summary>Hauteur de la caméra au-dessus de la roche (le fond, sous la mer ou un lac).</summary>
    private double HeightAboveRock()
    {
        Double3 position = Explorer.Position;
        return position.Length() - TerrainHeightField.RadiusMeters - Field.Sample(position.Normalized(), 1, ref _rockHint).Height;
    }

    private void ShowVolumeChunk(VolumeEntry entry, VolumeChunkMesh chunk)
    {
        FreeVolumeChunk(entry);
        entry.Center = chunk.Center;
        entry.Built = true;
        if (chunk.Indices.Length == 0)
            return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = chunk.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = chunk.Normals;
        arrays[(int)Mesh.ArrayType.Custom0] = chunk.Custom0;
        arrays[(int)Mesh.ArrayType.Custom1] = chunk.Custom1;
        arrays[(int)Mesh.ArrayType.TexUV] = chunk.Water;
        arrays[(int)Mesh.ArrayType.TexUV2] = chunk.Debris;
        arrays[(int)Mesh.ArrayType.Color] = chunk.Colors;
        arrays[(int)Mesh.ArrayType.Index] = chunk.Indices;
        long format = ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                      | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift);
        entry.Mesh = RenderingServer.MeshCreate();
        RenderingServer.MeshAddSurfaceFromArrays(entry.Mesh, RenderingServer.PrimitiveType.Triangles, arrays,
                                                 EmptyBlendShapes, EmptyLods, (RenderingServer.ArrayFormat)format);
        RenderingServer.MeshSurfaceSetMaterial(entry.Mesh, 0, _material.GetRid());
        entry.Instance = RenderingServer.InstanceCreate2(entry.Mesh, GetWorld3D().Scenario);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(entry.Instance, ShadowSetting);
        RenderingServer.InstanceSetTransform(entry.Instance, new Transform3D(Godot.Basis.Identity, (entry.Center - Explorer.Position).ToVector3()));
        entry.Shown = true;
    }

    private static void FreeVolumeChunk(VolumeEntry entry)
    {
        if (entry.Instance.IsValid)
            RenderingServer.FreeRid(entry.Instance);
        if (entry.Mesh.IsValid)
            RenderingServer.FreeRid(entry.Mesh);
        entry.Instance = default;
        entry.Mesh = default;
    }

    private void ClearVolume()
    {
        foreach (var entry in _volume.Values)
            FreeVolumeChunk(entry);
        _volume.Clear();
        _volumeFrame = null;
        _caveChunks = Array.Empty<(int, int)>();
        _caveChunkSet.Clear();
        _caveChunksFor = default;
        if (_volumeMaskBytes.Any(b => b != 0))
        {
            _volumeMaskBytes = new byte[VolumeMaskSize * VolumeMaskSize];
            _volumeMaskImage.SetData(VolumeMaskSize, VolumeMaskSize, false, Image.Format.R8, _volumeMaskBytes);
            _volumeMaskTexture.Update(_volumeMaskImage);
        }
    }

    /// <summary>Croûte modulaire : les blocs de volume qui touchent la zone modifiée sont refaits.</summary>
    private void InvalidateVolume(Double3 direction, double radiusM)
    {
        if (_volumeFrame == null)
            return;
        Double3 point = direction.Normalized() * TerrainHeightField.RadiusMeters;
        foreach (var entry in _volume.Values)
        {
            // (centre d'après le repère : un bloc qui a échoué n'a pas de centre connu)
            double half = VolumeChunkBuilder.ChunkM / 2;
            Double3 center = _volumeFrame.ToWorld(new Double3(entry.I * VolumeChunkBuilder.ChunkM + half, entry.J * VolumeChunkBuilder.ChunkM + half, 0));
            if ((entry.Built || entry.Failed) && (center.Normalized() * TerrainHeightField.RadiusMeters).DistanceTo(point) < radiusM + VolumeChunkBuilder.ChunkM * 1.5)
                entry.Rebuild = true;
        }
    }
}
