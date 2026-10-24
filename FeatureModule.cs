using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

namespace Feather;

/// <summary>Optional dialogs live in a collectible assembly, never loaded on normal startup.</summary>
internal static class FeatureModule
{
    internal static WeakReference? LastContext { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static object? Invoke(string dataPath, string method, params object?[] arguments) =>
        WithModule(assembly =>
        {
            var type = assembly.GetType("Feather.Features.BrowserLibrary", throwOnError: true)!;
            var library = Activator.CreateInstance(type, dataPath)!;
            return type.GetMethod(method)!.Invoke(library, arguments);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void RunTests(Action<string, Action> check, string folder) => WithModule(assembly =>
    {
        assembly.GetType("Feather.Features.FeatureTests", throwOnError: true)!.GetMethod("Run")!.Invoke(null, [check, folder]);
        return null;
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? WithModule(Func<Assembly, object?> action)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Modules", "Feather.Features.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("Модуль библиотеки не найден. Пересоберите Feather или перенесите всю папку dist.", path);
        var context = new AssemblyLoadContext("Feather.Features." + Guid.NewGuid().ToString("N"), isCollectible: true);
        LastContext = new WeakReference(context);
        try { return action(context.LoadFromAssemblyPath(path)); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        finally { context.Unload(); }
    }
}
