using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Agent;
using Autodesk.AutoCAD.Runtime;

namespace Nexus.Agent.Acad.Modules;

/// <summary>Reads modules.json and loads the modules whose product is detected.</summary>
internal static class ModuleLoader
{
    private sealed class ModuleManifest
    {
        public List<ModuleEntry> Modules { get; set; } = new();
    }

    private sealed class ModuleEntry
    {
        public string Name { get; set; } = "";
        public string Assembly { get; set; } = "";
        public string? ProductName { get; set; }
        public DetectRules Detect { get; set; } = new();
    }

    private sealed class DetectRules
    {
        public List<string> CommandLineProducts { get; set; } = new();
        public List<string> LoadedAssemblies { get; set; } = new();
        public List<string> LoadedArxPrefixes { get; set; } = new();
    }

    public sealed record LoadedModule(string Name, string? ProductName);

    public static List<LoadedModule> LoadAll(string directory, AcadModuleContext context)
    {
        var log = context.Log;
        var loaded = new List<LoadedModule>();
        string manifestPath = Path.Combine(directory, "modules.json");
        if (!File.Exists(manifestPath))
        {
            log.Info("No modules.json; running core only.");
            return loaded;
        }

        ModuleManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? new ModuleManifest();
        }
        catch (System.Exception ex)
        {
            log.Error("modules.json is invalid; running core only.", ex);
            return loaded;
        }

        var environment = ProductEnvironment.Capture(log);
        foreach (var entry in manifest.Modules)
        {
            try
            {
                string? why = environment.Match(entry.Detect);
                if (why is null)
                {
                    log.Info($"Module {entry.Name}: product not detected, not loaded.");
                    continue;
                }

                string path = Path.Combine(directory, entry.Assembly);
                if (!File.Exists(path))
                {
                    log.Warn($"Module {entry.Name}: detected ({why}) but {path} is missing.");
                    continue;
                }

                var asm = Assembly.LoadFrom(path);
                var types = asm.GetTypes().Where(t => typeof(IAcadAgentModule).IsAssignableFrom(t) && !t.IsAbstract).ToList();
                foreach (var type in types)
                {
                    var module = (IAcadAgentModule)Activator.CreateInstance(type)!;
                    module.Initialize(context);
                    log.Info($"Module {module.Name} loaded ({why}).");
                }
                if (types.Count > 0) loaded.Add(new LoadedModule(entry.Name, entry.ProductName));
            }
            catch (System.Exception ex)
            {
                // A broken module must not take the core agent (or AutoCAD) down.
                log.Error($"Module {entry.Name} failed to load.", ex);
            }
        }
        return loaded;
    }

    private sealed class ProductEnvironment
    {
        private readonly List<string> _products = new();
        private readonly HashSet<string> _assemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _arx = new();

        public static ProductEnvironment Capture(AgentLog log)
        {
            var env = new ProductEnvironment();
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("/product", StringComparison.OrdinalIgnoreCase))
                    env._products.Add(args[i + 1]);

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = a.GetName().Name;
                if (name is not null) env._assemblies.Add(name);
            }

            try
            {
                foreach (string? m in SystemObjects.DynamicLinker.GetLoadedModules())
                    if (m is not null) env._arx.Add(Path.GetFileName(m));
            }
            catch (System.Exception ex)
            {
                log.Warn("Could not list loaded ObjectARX modules.", ex);
            }

            log.Info($"Product detection: /product={string.Join(",", env._products)}; {env._arx.Count} ARX modules; {env._assemblies.Count} assemblies.");
            return env;
        }

        /// <summary>Returns a human-readable reason if any rule matches, else null.</summary>
        public string? Match(DetectRules rules)
        {
            foreach (var p in rules.CommandLineProducts)
                if (_products.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    return $"/product {p}";
            foreach (var a in rules.LoadedAssemblies)
                if (_assemblies.Contains(a))
                    return $"assembly {a} loaded";
            foreach (var prefix in rules.LoadedArxPrefixes)
            {
                var hit = _arx.FirstOrDefault(m => m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return $"module {hit} loaded";
            }
            return null;
        }
    }
}
