using System.Reflection;
using System.Reflection.Emit;
using NuclearReplay.Core;

namespace NuclearReplay.Core.Tests;

/// <summary>
/// Writes a tiny managed DLL that carries [BepInPlugin(guid, name, version)], so the setup checks see an installed
/// mod on any machine without the real plugin build.
/// </summary>
internal static class FakePlugin
{
    /// <param name="guid">The [BepInPlugin] GUID. Default: for NuclearReplay.dll both real plugins (recorder and replay
    /// player, as in the real build), a made-up one for other names.</param>
    public static void Write(string path, string? version = null, string? guid = null)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        WriteMany(path, version, guid != null ? new[] { guid } : DefaultGuids(name));
    }

    /// <summary>One DLL with a plugin class per GUID, in this order.</summary>
    public static void WriteMany(string path, string? version, params string[] guids)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(name);
        // A stand-in for BepInEx's attribute; PluginInfoReader matches it by name.
        var attribute = module.DefineType("BepInEx.BepInPlugin", TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
        var ctor = attribute.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string), typeof(string), typeof(string) });
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
        attribute.CreateType();
        for (int i = 0; i < guids.Length; i++)
        {
            var plugin = module.DefineType(name + ".Plugin" + i, TypeAttributes.Public | TypeAttributes.Class);
            plugin.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { guids[i], name, version ?? AppVersion.Current }));
            plugin.CreateType();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        assembly.Save(path);
    }

    private static string[] DefaultGuids(string name) => name switch
    {
        "NuclearReplay" => new[] { "talamander.nuclearreplay.recorder", "talamander.nuclearreplay.player" },
        _ => new[] { "test." + name.ToLowerInvariant() },
    };
}
