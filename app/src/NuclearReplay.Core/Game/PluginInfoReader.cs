using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuclearReplay.Core.Game;

public sealed record PluginInfo(string Guid, string Name, string Version);

/// <summary>Reads the [BepInPlugin(guid, name, version)] attributes from a plugin DLL without loading it.</summary>
public static class PluginInfoReader
{
    /// <summary>
    /// Returns the plugin GUID, name and version. A DLL with several plugins (NuclearReplay.dll holds the recorder and
    /// the replay player) gives the recorder, else the first one. See <see cref="ReadAll"/>.
    /// </summary>
    public static PluginInfo? Read(string dllPath)
    {
        var all = ReadAll(dllPath);
        return all.FirstOrDefault(p => NuclearReplay.Shared.PluginIds.IsRecorder(p.Guid)) ?? all.FirstOrDefault();
    }

    /// <summary>
    /// Every plugin in the DLL, in metadata order. Versions are the assembly's informational version (for example
    /// 0.5.0-beta.1) when it extends the numeric [BepInPlugin] version, otherwise the plugin version. Empty when the file
    /// is missing or not a plugin.
    /// </summary>
    public static List<PluginInfo> ReadAll(string dllPath)
    {
        var plugins = new List<PluginInfo>();
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return plugins;
            var md = pe.GetMetadataReader();
            string? informational = null;
            foreach (var handle in md.CustomAttributes)
            {
                var attribute = md.GetCustomAttribute(handle);
                var name = AttributeName(md, attribute);
                if (name == "AssemblyInformationalVersionAttribute" && attribute.Parent.Kind == HandleKind.AssemblyDefinition)
                {
                    var info = md.GetBlobReader(attribute.Value);
                    if (info.ReadUInt16() == 1) informational = info.ReadSerializedString();
                    continue;
                }
                if (name != "BepInPlugin") continue;
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) continue;
                var guid = blob.ReadSerializedString();
                var title = blob.ReadSerializedString();
                var version = blob.ReadSerializedString();
                if (guid != null && version != null) plugins.Add(new PluginInfo(guid, title ?? guid, version));
            }
            // "0.5.0-beta.1+abc123": drop build metadata.
            informational = informational?.Split('+')[0];
            for (int i = 0; i < plugins.Count; i++)
                if (!string.IsNullOrWhiteSpace(informational) && informational != plugins[i].Version
                    && informational.StartsWith(plugins[i].Version + "-", StringComparison.Ordinal))
                    plugins[i] = plugins[i] with { Version = informational };
            return plugins;
        }
        catch (Exception) { }
        return new List<PluginInfo>();
    }

    private static string? AttributeName(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle type;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
            type = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            type = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        else return null;
        return type.Kind switch
        {
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)type).Name),
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)type).Name),
            _ => null,
        };
    }
}
