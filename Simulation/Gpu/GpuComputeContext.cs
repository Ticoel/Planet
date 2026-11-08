using System;
using Godot;

namespace Planet.Simulation.Gpu;

/// <summary>
/// Accès à la carte graphique pour le calcul (compute shaders), via un RenderingDevice local :
/// un « deuxième écran invisible » de Vulkan, indépendant de l'affichage, sur lequel on envoie
/// des données, on lance des calculs massivement parallèles, puis on relit les résultats.
/// Toute la physique du climat (étapes 3 à 6) passera par ici.
/// </summary>
public sealed class GpuComputeContext : IDisposable
{
    public RenderingDevice Device { get; }

    /// <summary>Nom de la carte graphique utilisée (pour l'affichage).</summary>
    public string DeviceName => Device.GetDeviceName();

    private GpuComputeContext(RenderingDevice device)
    {
        Device = device;
    }

    /// <summary>Crée le contexte, ou renvoie null si la carte graphique n'est pas utilisable
    /// pour le calcul (moteur de rendu « Compatibilité », pilote sans Vulkan…).</summary>
    public static GpuComputeContext TryCreate()
    {
        try
        {
            var device = RenderingServer.CreateLocalRenderingDevice();
            return device == null ? null : new GpuComputeContext(device);
        }
        catch (Exception exception)
        {
            SimulationLog.Warning($"GPU compute unavailable: {exception.Message}");
            return null;
        }
    }

    /// <summary>Compile un compute shader GLSL (Vulkan, « #version 450 »).</summary>
    public Rid CreateShader(string glslSource, string name)
    {
        var source = new RDShaderSource
        {
            Language = RenderingDevice.ShaderLanguage.Glsl,
            SourceCompute = glslSource,
        };
        RDShaderSpirV spirV = Device.ShaderCompileSpirVFromSource(source);
        string error = spirV.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Compute shader '{name}' failed to compile:\n{error}");

        Rid shader = Device.ShaderCreateFromSpirV(spirV, name);
        if (!shader.IsValid)
            throw new InvalidOperationException($"Compute shader '{name}' could not be created.");
        return shader;
    }

    /// <summary>Crée un tampon de données sur la carte graphique (taille en octets).</summary>
    public Rid CreateBuffer(int sizeBytes, byte[] initialData = null)
    {
        Rid buffer = Device.StorageBufferCreate((uint)sizeBytes, initialData ?? Array.Empty<byte>());
        if (!buffer.IsValid)
            throw new InvalidOperationException($"Could not allocate a GPU buffer of {sizeBytes} bytes.");
        return buffer;
    }

    public void WriteBuffer(Rid buffer, float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        Device.BufferUpdate(buffer, 0, (uint)bytes.Length, bytes);
    }

    /// <summary>Relit un tampon de floats depuis la carte graphique.</summary>
    public void ReadBuffer(Rid buffer, float[] destination)
    {
        byte[] bytes = Device.BufferGetData(buffer, 0, (uint)(destination.Length * sizeof(float)));
        Buffer.BlockCopy(bytes, 0, destination, 0, Math.Min(bytes.Length, destination.Length * sizeof(float)));
    }

    /// <summary>Emballe des positions en vec4 (alignement imposé par les tampons GLSL std430).</summary>
    public static byte[] PackVec4(Vector3[] vectors)
    {
        var floats = new float[vectors.Length * 4];
        for (int i = 0; i < vectors.Length; i++)
        {
            floats[4 * i] = vectors[i].X;
            floats[4 * i + 1] = vectors[i].Y;
            floats[4 * i + 2] = vectors[i].Z;
        }
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Free(Rid rid)
    {
        if (rid.IsValid)
            Device.FreeRid(rid);
    }

    public void Dispose()
    {
        Device.Free();
    }
}
