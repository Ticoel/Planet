using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Simulation.Gpu;

/// <summary>
/// Un calcul sur carte graphique prêt à l'emploi : shader compilé, pipeline et jeux de tampons
/// liés (« uniform sets »). Chaque exécution lance un fil de calcul par élément, regroupés par
/// paquets de <see cref="WorkgroupSize"/> (la taille déclarée par « local_size_x » dans le shader).
/// Un même noyau peut avoir plusieurs jeux de tampons, par exemple pour alterner deux tampons
/// d'entrée/sortie d'un pas à l'autre (« ping-pong »).
/// </summary>
public sealed class ComputeKernel : IDisposable
{
    public const int WorkgroupSize = 64;

    private readonly GpuComputeContext _context;
    private readonly string _name;
    private readonly Rid _shader;
    private readonly Rid _pipeline;
    private readonly Rid _defaultSet;
    private readonly List<Rid> _sets = new();

    /// <param name="buffers">Tampons liés aux « binding » 0, 1, 2… du set 0 du shader (jeu par
    /// défaut, utilisé par <see cref="Run"/>). Peut être vide si l'on crée ses propres jeux.</param>
    public ComputeKernel(GpuComputeContext context, string glslSource, string name, params Rid[] buffers)
    {
        _context = context;
        _name = name;
        _shader = context.CreateShader(glslSource, name);
        _pipeline = context.Device.ComputePipelineCreate(_shader);
        if (buffers.Length > 0)
            _defaultSet = CreateUniformSet(buffers);
    }

    /// <summary>Lie une liste de tampons aux « binding » 0, 1, 2… du set 0.</summary>
    public Rid CreateUniformSet(params Rid[] buffers)
    {
        var uniforms = new Godot.Collections.Array<RDUniform>();
        for (int binding = 0; binding < buffers.Length; binding++)
        {
            var uniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.StorageBuffer,
                Binding = binding,
            };
            uniform.AddId(buffers[binding]);
            uniforms.Add(uniform);
        }
        Rid set = _context.Device.UniformSetCreate(uniforms, _shader, 0);
        if (!set.IsValid)
            throw new InvalidOperationException($"Could not bind buffers for compute shader '{_name}'.");
        _sets.Add(set);
        return set;
    }

    /// <summary>Lance le calcul sur <paramref name="itemCount"/> éléments avec le jeu par défaut
    /// et attend la fin.</summary>
    /// <param name="pushConstants">Petits paramètres (multiple de 16 octets) passés au shader.</param>
    public void Run(int itemCount, byte[] pushConstants)
    {
        Record(itemCount, _defaultSet, pushConstants);
        _context.Device.Submit();
        _context.Device.Sync();
    }

    /// <summary>
    /// Enregistre une série de passes (chacune avec son jeu de tampons et ses paramètres), puis
    /// les envoie d'un coup à la carte graphique et attend la fin. Chaque passe a sa propre liste
    /// de commandes : Godot place ainsi entre elles les barrières qui garantissent qu'une passe
    /// lit bien les résultats complets de la précédente.
    /// </summary>
    public void RunPasses(int itemCount, IReadOnlyList<(Rid uniformSet, byte[] pushConstants)> passes)
    {
        foreach (var (set, push) in passes)
            Record(itemCount, set, push);
        _context.Device.Submit();
        _context.Device.Sync();
    }

    private void Record(int itemCount, Rid uniformSet, byte[] pushConstants)
    {
        var device = _context.Device;
        uint groups = (uint)((itemCount + WorkgroupSize - 1) / WorkgroupSize);

        long list = device.ComputeListBegin();
        device.ComputeListBindComputePipeline(list, _pipeline);
        device.ComputeListBindUniformSet(list, uniformSet, 0);
        device.ComputeListSetPushConstant(list, pushConstants, (uint)pushConstants.Length);
        device.ComputeListDispatch(list, groups, 1, 1);
        device.ComputeListEnd();
    }

    public void Dispose()
    {
        foreach (var set in _sets)
            _context.Free(set);
        _context.Free(_pipeline);
        _context.Free(_shader);
    }
}
