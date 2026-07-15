using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Kumori.AutoMapperCompatPatcher;

internal static class Program
{
    internal const string MarkerNamespace = "Kumori.Build";
    internal const string MarkerName = "AutoMapperCompatibilityMarker";
    private const string MapperConfigurationType = "AutoMapper.MapperConfiguration";
    private const string LegacyConfigurationAction =
        "System.Action`1<AutoMapper.IMapperConfigurationExpression>";

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1)
                throw new ArgumentException("Usage: Kumori.AutoMapperCompatPatcher <osu.Game.dll>");

            string assemblyPath = Path.GetFullPath(args[0]);
            Patch(assemblyPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AutoMapper compatibility patch failed: {ex}");
            return 1;
        }
    }

    internal static void Patch(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The osu! game assembly was not found.", assemblyPath);

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath)!);
        using var assembly = AssemblyDefinition.ReadAssembly(
            assemblyPath,
            new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadingMode = ReadingMode.Deferred,
            });
        ModuleDefinition module = assembly.MainModule;

        if (module.GetType(MarkerNamespace, MarkerName) is not null)
        {
            EnsureLegacyConstructorCallsAreAbsent(module);
            Console.WriteLine($"AutoMapper compatibility already patched: {assemblyPath}");
            return;
        }

        List<(MethodDefinition Method, Instruction Instruction, MethodReference Constructor)> sites =
            FindLegacyConstructorCalls(module).ToList();
        if (sites.Count == 0)
        {
            throw new InvalidOperationException(
                "No legacy AutoMapper constructor calls were found. The upstream osu! assembly changed; " +
                "review and remove or update this compatibility patch instead of silently continuing.");
        }

        AssemblyNameReference loggingAssembly = ResolveLoggingAssemblyReference(module, assemblyPath);
        var loggerFactoryType = new TypeReference(
            "Microsoft.Extensions.Logging",
            "ILoggerFactory",
            module,
            loggingAssembly);
        var nullLoggerFactoryType = new TypeReference(
            "Microsoft.Extensions.Logging.Abstractions",
            "NullLoggerFactory",
            module,
            loggingAssembly);
        var nullLoggerFactoryInstance = new FieldReference(
            "Instance",
            nullLoggerFactoryType,
            nullLoggerFactoryType);

        foreach ((MethodDefinition method, Instruction instruction, MethodReference legacyConstructor) in sites)
        {
            var patchedConstructor = new MethodReference(
                ".ctor",
                module.TypeSystem.Void,
                legacyConstructor.DeclaringType)
            {
                HasThis = true,
                CallingConvention = MethodCallingConvention.Default,
            };
            patchedConstructor.Parameters.Add(
                new ParameterDefinition(module.ImportReference(legacyConstructor.Parameters[0].ParameterType)));
            patchedConstructor.Parameters.Add(new ParameterDefinition(loggerFactoryType));

            ILProcessor processor = method.Body.GetILProcessor();
            processor.InsertBefore(
                instruction,
                processor.Create(OpCodes.Ldsfld, nullLoggerFactoryInstance));
            instruction.Operand = patchedConstructor;
        }

        module.Types.Add(new TypeDefinition(
            MarkerNamespace,
            MarkerName,
            TypeAttributes.NotPublic |
            TypeAttributes.Abstract |
            TypeAttributes.Sealed |
            TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object));

        EnsureLegacyConstructorCallsAreAbsent(module);
        WriteAtomically(assembly, assemblyPath);
        Console.WriteLine($"Patched {sites.Count} AutoMapper constructor call(s): {assemblyPath}");
    }

    private static IEnumerable<(MethodDefinition, Instruction, MethodReference)> FindLegacyConstructorCalls(
        ModuleDefinition module)
    {
        foreach (TypeDefinition type in Flatten(module.Types))
        {
            foreach (MethodDefinition method in type.Methods.Where(candidate => candidate.HasBody))
            {
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code != Code.Newobj ||
                        instruction.Operand is not MethodReference constructor ||
                        constructor.Name != ".ctor" ||
                        constructor.DeclaringType.FullName != MapperConfigurationType ||
                        constructor.Parameters.Count != 1 ||
                        constructor.Parameters[0].ParameterType.FullName != LegacyConfigurationAction)
                    {
                        continue;
                    }

                    yield return (method, instruction, constructor);
                }
            }
        }
    }

    private static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in Flatten(type.NestedTypes))
                yield return nested;
        }
    }

    private static AssemblyNameReference ResolveLoggingAssemblyReference(
        ModuleDefinition module,
        string assemblyPath)
    {
        AssemblyNameReference? existing = module.AssemblyReferences.FirstOrDefault(
            reference => reference.Name == "Microsoft.Extensions.Logging.Abstractions");
        if (existing is not null)
            return existing;

        string loggingPath = Path.Combine(
            Path.GetDirectoryName(assemblyPath)!,
            "Microsoft.Extensions.Logging.Abstractions.dll");
        if (!File.Exists(loggingPath))
        {
            throw new FileNotFoundException(
                "Microsoft.Extensions.Logging.Abstractions.dll must be beside osu.Game.dll before patching.",
                loggingPath);
        }

        using var loggingAssembly = AssemblyDefinition.ReadAssembly(loggingPath);
        var added = new AssemblyNameReference(loggingAssembly.Name.Name, loggingAssembly.Name.Version)
        {
            Culture = loggingAssembly.Name.Culture,
            PublicKeyToken = loggingAssembly.Name.PublicKeyToken,
        };
        module.AssemblyReferences.Add(added);
        return added;
    }

    private static void EnsureLegacyConstructorCallsAreAbsent(ModuleDefinition module)
    {
        if (FindLegacyConstructorCalls(module).Any())
            throw new InvalidOperationException("Legacy AutoMapper constructor calls remain after patching.");
    }

    private static void WriteAtomically(AssemblyDefinition assembly, string assemblyPath)
    {
        string temporaryPath = $"{assemblyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            assembly.Write(temporaryPath);
            File.Move(temporaryPath, assemblyPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort only; failure to remove a temporary file must not hide the patch result.
            }
        }
    }
}
