using System;
using System.Collections.Generic;
using Godot;
using Planet.Simulation.Gpu;

namespace Planet.Simulation.Climate;

/// <summary>
/// Atmosphère et hydrologie de surface calculées sur la carte graphique : un fil de calcul par
/// cellule, des centaines de sous-pas par jour simulé. L'état (température, vapeur, sol, neige)
/// alterne entre deux tampons d'un sous-pas à l'autre ; les cumuls (pluie, évaporation,
/// ruissellement, neige) s'additionnent dans un tampon relu puis remis à zéro à chaque image.
/// Les constantes physiques sont générées depuis <see cref="AtmosphereModel"/> : le shader et la
/// référence processeur partagent exactement les mêmes valeurs et les mêmes formules.
/// </summary>
public sealed class AtmosphereKernel : IDisposable
{
    private const string ShaderBody = @"
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer Neighbors { int neighbors[]; };
// x : capacité thermique, y : albédo hors glace/neige, z : altitude (km), w : 1 si océan
layout(set = 0, binding = 1, std430) restrict readonly buffer Surface { vec4 surface[]; };
// x : réserve utile du sol (mm), y : facteur d'évapotranspiration
layout(set = 0, binding = 2, std430) restrict readonly buffer LandCover { vec4 land_cover[]; };
// xyz : pente du relief (m/m)
layout(set = 0, binding = 3, std430) restrict readonly buffer Slope { vec4 slope[]; };
layout(set = 0, binding = 4, std430) restrict readonly buffer Insolation { float insolation[]; };
layout(set = 0, binding = 5, std430) restrict readonly buffer Positions { vec4 positions[]; };
// xyz : vent (m/s), w : convergence (1/s)
layout(set = 0, binding = 6, std430) restrict readonly buffer Wind { vec4 wind[]; };
// x : température (°C, niveau de la mer), y : vapeur (mm), z : sol (mm), w : neige (mm)
layout(set = 0, binding = 7, std430) restrict readonly buffer StateIn { vec4 state_in[]; };
layout(set = 0, binding = 8, std430) restrict writeonly buffer StateOut { vec4 state_out[]; };
// x : pluie + neige, y : évaporation, z : ruissellement, w : chute de neige (mm cumulés)
layout(set = 0, binding = 9, std430) restrict buffer Accumulation { vec4 accumulation[]; };
// Longueur de face / aire de la cellule (1/m), 6 cases par cellule
layout(set = 0, binding = 10, std430) restrict readonly buffer FaceWeights { float face_weights[]; };
// xyz : courant océanique de surface (m/s)
layout(set = 0, binding = 11, std430) restrict readonly buffer Current { vec4 current[]; };

layout(push_constant, std430) uniform Parameters {
    float dt;
    uint cell_count;
    float padding0;
    float padding1;
} params;

void main() {
    uint cell = gl_GlobalInvocationID.x;
    if (cell >= params.cell_count) {
        return;
    }

    vec4 here = state_in[cell];
    vec4 w_here = wind[cell];
    vec3 p = positions[cell].xyz;

    float sum_t = 0.0;
    float sum_w = 0.0;
    float count = 0.0;
    float advection_t = 0.0;
    float advection_w = 0.0;
    float ocean_advection_t = 0.0;
    bool ocean_here = surface[cell].w > 0.5;
    vec3 current_here = current[cell].xyz;
    for (int k = 0; k < 6; k++) {
        int n = neighbors[cell * 6u + uint(k)];
        if (n < 0) {
            continue;
        }
        float tn = state_in[n].x;
        float wn = state_in[n].y;
        sum_t += tn;
        sum_w += wn;
        count += 1.0;
        vec3 d = positions[n].xyz - p;
        vec3 direction = normalize(d - p * dot(d, p));
        float face = 0.5 * dot(w_here.xyz + wind[n].xyz, direction) * face_weights[cell * 6u + uint(k)];
        // Chaleur : forme advective amont ; vapeur : forme en flux, conservative
        if (face < 0.0) {
            advection_t += -face * (tn - here.x);
        }
        advection_w += face > 0.0 ? -face * here.y : -face * wn;
        // Courants océaniques : entre cellules de mer, forme advective amont
        if (ocean_here && surface[n].w > 0.5) {
            float flow = 0.5 * dot(current_here + current[n].xyz, direction) * face_weights[cell * 6u + uint(k)];
            if (flow < 0.0) {
                ocean_advection_t += -flow * (tn - here.x);
            }
        }
    }
    float mean_t = sum_t / count;
    float mean_w = sum_w / count;

    float t = here.x;
    float w = here.y;
    float soil = here.z;
    float snow = here.w;
    vec4 s = surface[cell];
    vec4 lc = land_cover[cell];
    bool ocean = s.w > 0.5;
    float dt = params.dt;

    // Température
    float surface_t = t - LAPSE_RATE * s.z;
    float ice = ocean ? clamp((ICE_START - surface_t) / (ICE_START - ICE_FULL), 0.0, 1.0) : 0.0;
    float snow_cover = ocean ? 0.0 : clamp(snow / SNOW_COVER, 0.0, 1.0);
    float albedo = max(mix(s.y, ICE_ALBEDO, ice), mix(s.y, lc.z, snow_cover));
    float capacity = ocean ? mix(s.x, SEA_ICE_CAPACITY, ice) : s.x;
    float net = (1.0 - albedo) * insolation[cell]
              - (OLR_A + OLR_B * t)
              + COUPLING * (mean_t - t)
              + ATMOSPHERE_CAPACITY * advection_t;
    float new_t = t + dt * net / capacity + (ocean ? dt * OCEAN_TRANSPORT * ocean_advection_t : 0.0);

    // Vapeur d'eau (mm/s)
    float saturation = min(MAX_SATURATION, SATURATION_REF * exp(CLAUSIUS_CLAPEYRON * (surface_t - SATURATION_REF_T)));
    float humidity = clamp(w / saturation, 0.0, 2.0);
    float speed = length(w_here.xyz);
    float potential = EVAPORATION_RATE * (1.0 + speed / EVAPORATION_WIND)
                    * max(0.0, 1.0 - humidity) * (saturation / SATURATION_REF);
    float evaporation = ocean
        ? potential * (1.0 - ice)
        : potential * lc.y * clamp(soil / lc.x, 0.0, 1.0);

    vec3 sl = slope[cell].xyz;
    float uplift = max(0.0, w_here.x * sl.x + w_here.y * sl.y + w_here.z * sl.z);
    float precipitation = max(0.0, w - saturation) / CONDENSATION_TIME
                        + w * humidity * humidity / BACKGROUND_RAIN_TIME
                        + CONVERGENCE_RAIN * max(0.0, w_here.w) * w
                        + OROGRAPHIC_RAIN * uplift * w / VAPOR_SCALE_HEIGHT;
    precipitation = min(precipitation, w / dt + evaporation);

    float new_w = max(0.0, w + dt * (evaporation - precipitation + advection_w + MOISTURE_COUPLING * (mean_w - w)));

    // Pluie ou neige, fonte, sol, ruissellement, glaciers
    float snow_fraction = clamp((RAIN_ALL - surface_t) / (RAIN_ALL - SNOW_ALL), 0.0, 1.0);
    float snowfall = precipitation * snow_fraction;
    float rain = precipitation - snowfall;
    float new_soil = 0.0;
    float new_snow = 0.0;
    float runoff = 0.0;
    if (!ocean) {
        float melt = surface_t > MELT_BASE ? MELT_RATE * (surface_t - MELT_BASE) : 0.0;
        melt = min(melt, snow / dt + snowfall);
        new_snow = snow + dt * (snowfall - melt);
        float glacier_flow = max(0.0, new_snow - GLACIER_MAX);
        new_snow -= glacier_flow;

        new_soil = soil + dt * (rain + melt - evaporation);
        float excess = max(0.0, new_soil - lc.x);
        new_soil = max(0.0, new_soil - excess);
        runoff = excess + glacier_flow;
    }

    state_out[cell] = vec4(new_t, new_w, new_soil, new_snow);
    accumulation[cell] += vec4(precipitation * dt, evaporation * dt, runoff, snowfall * dt);
}
";

    private readonly GpuComputeContext _context;
    private readonly AtmosphereModel _model;
    private readonly int _cellCount;
    private readonly Rid _neighbors;
    private readonly Rid _faceWeights;
    private readonly Rid _surface;
    private readonly Rid _landCover;
    private readonly Rid _slope;
    private readonly Rid _positions;
    private readonly Rid _wind;
    private readonly Rid _oceanCurrent;
    private readonly Rid[] _state = new Rid[2];
    private readonly Rid _accumulation;
    private readonly ComputeKernel _kernel;
    private readonly byte[] _zeros;

    // Jeux de tampons : [ensoleillement (0 instantané, 1 moyenne 24 h), sens A→B ou B→A]
    private readonly Rid[,] _sets = new Rid[2, 2];
    private int _current;

    /// <summary>Source GLSL complète (constantes comprises), exposée pour les tests.</summary>
    public static string BuildShaderSource(AtmosphereModel model) =>
        "#version 450\n\n" + model.ShaderConstants() + ShaderBody;

    public AtmosphereKernel(GpuComputeContext context, AtmosphereModel model,
                            Rid instantInsolation, Rid dailyInsolation)
    {
        _context = context;
        _model = model;
        _cellCount = model.Grid.CellCount;
        int vec4Bytes = _cellCount * 16;

        var neighborBytes = new byte[model.NeighborTable.Length * sizeof(int)];
        Buffer.BlockCopy(model.NeighborTable, 0, neighborBytes, 0, neighborBytes.Length);
        _neighbors = context.CreateBuffer(neighborBytes.Length, neighborBytes);
        var weightBytes = new byte[model.FaceWeightTable.Length * sizeof(float)];
        Buffer.BlockCopy(model.FaceWeightTable, 0, weightBytes, 0, weightBytes.Length);
        _faceWeights = context.CreateBuffer(weightBytes.Length, weightBytes);
        _surface = context.CreateBuffer(vec4Bytes);
        _landCover = context.CreateBuffer(vec4Bytes);
        _slope = context.CreateBuffer(vec4Bytes);
        _positions = context.CreateBuffer(vec4Bytes, GpuComputeContext.PackVec4(model.Grid.Positions));
        _wind = context.CreateBuffer(vec4Bytes);
        _oceanCurrent = context.CreateBuffer(vec4Bytes);
        _state[0] = context.CreateBuffer(vec4Bytes);
        _state[1] = context.CreateBuffer(vec4Bytes);
        _accumulation = context.CreateBuffer(vec4Bytes);
        _zeros = new byte[vec4Bytes];

        _kernel = new ComputeKernel(context, BuildShaderSource(model), "atmosphere");
        Rid[] insolation = { instantInsolation, dailyInsolation };
        for (int source = 0; source < 2; source++)
            for (int direction = 0; direction < 2; direction++)
                _sets[source, direction] = _kernel.CreateUniformSet(
                    _neighbors, _surface, _landCover, _slope, insolation[source], _positions, _wind,
                    _state[direction], _state[1 - direction], _accumulation, _faceWeights, _oceanCurrent);
    }

    /// <summary>Envoie surface, couverture du sol et pentes (après changement du relief ou des biomes).</summary>
    public void UploadSurface()
    {
        _context.WriteBuffer(_surface, _model.Surface);
        _context.WriteBuffer(_landCover, _model.LandCover);
        _context.WriteBuffer(_slope, _model.Slope);
    }

    /// <summary>Envoie le vent (vec4 : vent, convergence).</summary>
    public void UploadWind(float[] windPacked) => _context.WriteBuffer(_wind, windPacked);

    /// <summary>Envoie les courants océaniques (vec4 : courant, 0).</summary>
    public void UploadCurrent(float[] currentPacked) => _context.WriteBuffer(_oceanCurrent, currentPacked);

    /// <summary>Remplace l'état courant et remet les cumuls à zéro.</summary>
    public void UploadState(float[] state)
    {
        _current = 0;
        _context.WriteBuffer(_state[0], state);
        ResetAccumulation();
    }

    public void ResetAccumulation() => _context.Device.BufferUpdate(_accumulation, 0, (uint)_zeros.Length, _zeros);

    /// <summary>Enchaîne <paramref name="substeps"/> sous-pas en une seule commande.</summary>
    public void Run(int substeps, float dtSeconds, bool dailyMeanInsolation)
    {
        var push = new byte[16];
        BitConverter.TryWriteBytes(new Span<byte>(push, 0, 4), dtSeconds);
        BitConverter.TryWriteBytes(new Span<byte>(push, 4, 4), (uint)_cellCount);
        int source = dailyMeanInsolation ? 1 : 0;
        var passes = new List<(Rid, byte[])>(substeps);
        for (int i = 0; i < substeps; i++)
        {
            passes.Add((_sets[source, _current], push));
            _current = 1 - _current;
        }
        _kernel.RunPasses(_cellCount, passes);
    }

    public void ReadState(float[] destination) => _context.ReadBuffer(_state[_current], destination);

    /// <summary>Relit les cumuls puis les remet à zéro.</summary>
    public void ReadAndResetAccumulation(float[] destination)
    {
        _context.ReadBuffer(_accumulation, destination);
        ResetAccumulation();
    }

    public void Dispose()
    {
        _kernel.Dispose();
        foreach (var rid in new[] { _oceanCurrent, _faceWeights, _accumulation, _state[1], _state[0], _wind, _positions, _slope, _landCover, _surface, _neighbors })
            _context.Free(rid);
    }
}
