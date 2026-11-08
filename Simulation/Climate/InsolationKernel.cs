using System;
using Godot;
using Planet.Simulation.Gpu;

namespace Planet.Simulation.Climate;

/// <summary>
/// Ensoleillement calculé sur la carte graphique : un fil de calcul par cellule.
/// Mêmes formules que <see cref="Insolation"/> (version processeur), qui sert de référence.
/// </summary>
public sealed class InsolationKernel : IDisposable
{
    // GLSL pour Vulkan. Les tampons std430 alignent les vec3 sur 16 octets : les positions
    // sont donc envoyées en vec4. Les paramètres changeant à chaque pas passent en
    // « push constants » (32 octets).
    private const string ShaderSource = @"#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer Positions { vec4 positions[]; };
layout(set = 0, binding = 1, std430) restrict writeonly buffer InstantInsolation { float instant[]; };
layout(set = 0, binding = 2, std430) restrict writeonly buffer DailyInsolation { float daily[]; };

layout(push_constant, std430) uniform Parameters {
    vec4 sun;          // xyz : direction du Soleil, w : flux (W/m²)
    uint cell_count;
    float sin_decl;
    float cos_decl;
    float padding;
} params;

const float PI = 3.14159265358979;

void main() {
    uint cell = gl_GlobalInvocationID.x;
    if (cell >= params.cell_count) {
        return;
    }

    vec3 p = positions[cell].xyz;
    float flux = params.sun.w;

    // Instantané : nul la nuit, maximal sous le Soleil au zénith
    instant[cell] = flux * max(dot(p, params.sun.xyz), 0.0);

    // Moyenne sur 24 h : angle horaire du coucher du soleil h0
    float sin_lat = clamp(p.y, -1.0, 1.0);
    float cos_lat = sqrt(max(0.0, 1.0 - sin_lat * sin_lat));
    float x = (cos_lat < 1e-6 || params.cos_decl < 1e-6)
        ? -sign(sin_lat * params.sin_decl) * 2.0
        : -(sin_lat * params.sin_decl) / (cos_lat * params.cos_decl);
    float h0 = acos(clamp(x, -1.0, 1.0));
    daily[cell] = flux / PI * (h0 * sin_lat * params.sin_decl + cos_lat * params.cos_decl * sin(h0));
}
";

    private readonly GpuComputeContext _context;
    private readonly int _cellCount;
    private readonly Rid _positions;
    private readonly Rid _instant;
    private readonly Rid _daily;
    private readonly ComputeKernel _kernel;
    private readonly byte[] _pushConstants = new byte[32];

    public InsolationKernel(GpuComputeContext context, Vector3[] positions)
    {
        _context = context;
        _cellCount = positions.Length;
        _positions = context.CreateBuffer(_cellCount * 16, GpuComputeContext.PackVec4(positions));
        _instant = context.CreateBuffer(_cellCount * sizeof(float));
        _daily = context.CreateBuffer(_cellCount * sizeof(float));
        _kernel = new ComputeKernel(context, ShaderSource, "insolation", _positions, _instant, _daily);
    }

    /// <summary>Tampons des résultats, lisibles directement par d'autres noyaux (températures).</summary>
    public Rid InstantBuffer => _instant;
    public Rid DailyBuffer => _daily;

    /// <summary>Calcule et relit les résultats.</summary>
    public void Compute(in SunState sun, float[] instant, float[] dailyMean)
    {
        Dispatch(sun);
        _context.ReadBuffer(_instant, instant);
        _context.ReadBuffer(_daily, dailyMean);
    }

    /// <summary>Calcule sans relire : les résultats restent sur la carte graphique.</summary>
    public void Dispatch(in SunState sun)
    {
        // Emballage des paramètres dans l'ordre exact du bloc push_constant
        WriteFloat(0, sun.Direction.X);
        WriteFloat(4, sun.Direction.Y);
        WriteFloat(8, sun.Direction.Z);
        WriteFloat(12, sun.Flux);
        BitConverter.TryWriteBytes(new Span<byte>(_pushConstants, 16, 4), (uint)_cellCount);
        WriteFloat(20, MathF.Sin(sun.Declination));
        WriteFloat(24, MathF.Cos(sun.Declination));
        WriteFloat(28, 0f);

        _kernel.Run(_cellCount, _pushConstants);
    }

    private void WriteFloat(int offset, float value) =>
        BitConverter.TryWriteBytes(new Span<byte>(_pushConstants, offset, 4), value);

    public void Dispose()
    {
        _kernel.Dispose();
        _context.Free(_daily);
        _context.Free(_instant);
        _context.Free(_positions);
    }
}
