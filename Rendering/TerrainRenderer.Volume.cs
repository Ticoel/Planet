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
/// </summary>
public partial class TerrainRenderer
{
    private const int VolumeMaskedRadius = 5;          // blocs (≈ 90 m) qui remplacent la surface
    private const int VolumeDrawnRadius = 6;           // plus une couronne enfoncée
    private const int VolumeMaskSize = 16;             // masque : 16 × 16 blocs autour de la caméra
    private const double VolumeMaxAltitude = 250;      // au-dessus, la surface suffit
    private const double VolumeReanchorM = 1500;
    private const int VolumeBuilders = 3;

    private sealed class VolumeEntry
    {
        public int I, J;
        public Task<VolumeChunkMesh> Build;
        public bool Rebuild, Built;
        public Double3 Center;
        public Rid Mesh, Instance;
    }

    private VolumeFrame _volumeFrame;
    private TerrainHeightField _volumeField;
    private int _volumeCaveVersion = -1;
    private readonly Dictionary<(int, int), VolumeEntry> _volume = new();
    private Image _volumeMaskImage;
    private ImageTexture _volumeMaskTexture;
    private byte[] _volumeMaskBytes = new byte[VolumeMaskSize * VolumeMaskSize];

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
        bool wanted = VolumeEnabled && Field != null && Explorer.AltitudeAboveGround < VolumeMaxAltitude && !Explorer.InTransition;
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

        // Blocs arrivés
        foreach (var entry in _volume.Values)
        {
            if (entry.Build == null || !entry.Build.IsCompleted)
                continue;
            if (entry.Build.IsCompletedSuccessfully)
                ShowVolumeChunk(entry, entry.Build.Result);
            else
                GD.PushWarning($"Relief en volume : {entry.Build.Exception?.GetBaseException().Message}");
            entry.Build = null;
        }
        // Blocs à calculer, les plus proches d'abord
        int building = _volume.Values.Count(e => e.Build != null);
        var wantedKeys = new List<(int i, int j, int d2)>();
        for (int dj = -VolumeDrawnRadius; dj <= VolumeDrawnRadius; dj++)
            for (int di = -VolumeDrawnRadius; di <= VolumeDrawnRadius; di++)
                if (di * di + dj * dj <= VolumeDrawnRadius * VolumeDrawnRadius + 1)
                    wantedKeys.Add((ci + di, cj + dj, di * di + dj * dj));
        foreach (var (i, j, _) in wantedKeys.OrderBy(k => k.d2))
        {
            if (building >= VolumeBuilders)
                break;
            if (!_volume.TryGetValue((i, j), out var entry))
                _volume[(i, j)] = entry = new VolumeEntry { I = i, J = j };
            else if (entry.Build != null || (entry.Built && !entry.Rebuild))
                continue;
            entry.Rebuild = false;
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
            if (di * di + dj * dj > (VolumeDrawnRadius + 2) * (VolumeDrawnRadius + 2) && entry.Build == null)
            {
                FreeVolumeChunk(entry);
                _volume.Remove(key);
            }
        }

        // Position des blocs, et masque des blocs prêts qui remplacent la surface
        var mask = new byte[VolumeMaskSize * VolumeMaskSize];
        int corner = VolumeMaskSize / 2;
        foreach (var entry in _volume.Values)
        {
            if (!entry.Instance.IsValid)
                continue;
            RenderingServer.InstanceSetTransform(entry.Instance, new Transform3D(Godot.Basis.Identity, (entry.Center - camera).ToVector3()));
            int di = entry.I - ci, dj = entry.J - cj;
            int mi = di + corner, mj = dj + corner;
            if (di * di + dj * dj <= VolumeMaskedRadius * VolumeMaskedRadius + 1 && mi >= 0 && mj >= 0 && mi < VolumeMaskSize && mj < VolumeMaskSize)
                mask[mj * VolumeMaskSize + mi] = 255;
        }
        if (!mask.AsSpan().SequenceEqual(_volumeMaskBytes))
        {
            _volumeMaskBytes = mask;
            _volumeMaskImage.SetData(VolumeMaskSize, VolumeMaskSize, false, Image.Format.R8, mask);
            _volumeMaskTexture.Update(_volumeMaskImage);
        }
        _material.SetShaderParameter("volume_enabled", true);
        _material.SetShaderParameter("volume_origin", (_volumeFrame.Origin - camera).ToVector3());
        _material.SetShaderParameter("volume_axis_x", _volumeFrame.AxisX.ToVector3());
        _material.SetShaderParameter("volume_axis_y", _volumeFrame.AxisY.ToVector3());
        _material.SetShaderParameter("volume_corner", new Vector2(ci - corner, cj - corner));
        _material.SetShaderParameter("volume_chunk", (float)VolumeChunkBuilder.ChunkM);
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
        Double3 point = direction.Normalized() * TerrainHeightField.RadiusMeters;
        foreach (var entry in _volume.Values)
            if (entry.Built && (entry.Center.Normalized() * TerrainHeightField.RadiusMeters).DistanceTo(point) < radiusM + VolumeChunkBuilder.ChunkM * 1.5)
                entry.Rebuild = true;
    }
}
