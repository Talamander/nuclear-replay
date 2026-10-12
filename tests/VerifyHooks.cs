// Offline structural checks against installed metadata. This does not run Harmony or Unity.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

string project = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string game = args.Length > 1 ? args[1] : "/mnt/games/SteamLibrary/steamapps/common/Nuclear Option";
using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(game, "NuclearOption_Data/Managed/Assembly-CSharp.dll"));
using var plugin = AssemblyDefinition.ReadAssembly(Path.Combine(project, "dist/NuclearReplay.dll"));
var hooks = plugin.MainModule.GetType("NuclearReplay.Hooks");
int checkedBindings = 0;
foreach (string line in File.ReadLines(Path.Combine(project, "src/Hooks.cs")))
{
    var match = Regex.Match(line, "Patch\\(\"([^\"]+)\", typeof\\(([^)]+)\\), \"([^\"]+)\"(.*)");
    if (!match.Success) continue;
    string label = match.Groups[1].Value;
    string type = match.Groups[2].Value;
    string name = match.Groups[3].Value;
    TypeDefinition ResolveType(string qualified)
    {
        var names = qualified.Split('.');
        var resolved = assembly.MainModule.Types.Single(t => t.Name == names[0]);
        foreach (string nested in names.Skip(1)) resolved = resolved.NestedTypes.Single(t => t.Name == nested);
        return resolved;
    }
    var candidates = ResolveType(type).Methods.Where(m => m.Name == name);
    if (name == "SpawnMissile")
        candidates = candidates.Where(m => m.Parameters[0].ParameterType.Name == (label == "launch_definition" ? "MissileDefinition" : "GameObject"));
    var original = candidates.Single();
    if (!original.HasBody || original.RVA == 0) throw new Exception("Missing IL body: " + original.FullName);
    foreach (Match callback in Regex.Matches(match.Groups[4].Value, "\"([A-Za-z]+(?:Prefix|Postfix|Finalizer))\""))
    {
        var hook = hooks.Methods.Single(m => m.Name == callback.Groups[1].Value);
        if (hook.ReturnType.FullName != "System.Void") throw new Exception("Observation hook could suppress game behavior: " + hook.Name);
        foreach (var parameter in hook.Parameters)
        {
            string expected;
            if (parameter.Name == "__state") continue;
            if (parameter.Name == "__instance") expected = original.DeclaringType.FullName;
            else if (parameter.Name == "__result") expected = original.ReturnType.FullName;
            else expected = original.Parameters.Single(p => p.Name == parameter.Name).ParameterType.FullName;
            if (parameter.ParameterType.IsByReference || parameter.ParameterType.FullName != expected)
                throw new Exception($"Hook parameter mismatch or mutable game parameter: {hook.Name}.{parameter.Name}: {expected}");
        }
    }
    checkedBindings++;
}
if (checkedBindings < 19) throw new Exception("Expected at least 19 parsed bindings; got " + checkedBindings);
Console.WriteLine($"PASS: {checkedBindings} installed-assembly bindings have IL bodies and matching observer callback parameters. Harmony execution remains untested.");
int effectFields = 0;
void RequireEffectField(string typeName, string name, string expectedType)
{
    TypeDefinition owner = assembly.MainModule.GetType(typeName) ?? throw new Exception("Missing effect source type: " + typeName);
    FieldDefinition field = owner.Fields.Single(f => f.Name == name);
    if (field.IsStatic || field.IsNotSerialized || field.FieldType.FullName != expectedType ||
        (!field.IsPublic && !field.CustomAttributes.Any(a => a.AttributeType.FullName == "UnityEngine.SerializeField")))
        throw new Exception("Effect asset serialized field contract differs: " + field.FullName);
    effectFields++;
}
foreach (string field in new[] { "impactEffectGround", "impactEffectArmor", "impactEffectWater", "selfDestructEffect" })
    RequireEffectField("Gun", field, "UnityEngine.GameObject");
foreach (string type in new[] { "EscapeCapsule", "VLSBooster" }) RequireEffectField(type, "splashPrefab", "UnityEngine.GameObject");
RequireEffectField("Missile", "warhead", "Missile/Warhead");
foreach (string field in new[] { "airEffect", "armorEffect", "terrainEffect", "waterSurfaceEffect", "underwaterEffect", "fizzleEffect" })
    RequireEffectField("Missile/Warhead", field, "UnityEngine.GameObject");
foreach (string field in new[] { "splash_large", "rotorStrike_solid", "rotorStrike_dirt", "rotorStrike_water", "vehicleWreckDestroyed",
    "contactSmoke", "contactDust", "scorchMarkDecal", "tracer" }) RequireEffectField("GameAssets", field, "UnityEngine.GameObject");
RequireEffectField("GameAssets", "groundImpacts", "UnityEngine.GameObject[]");
RequireEffectField("UnitPart", "damageEffects", "System.Collections.Generic.List`1<DamageEffect>");
RequireEffectField("DamageEffect", "prefab", "UnityEngine.GameObject");
RequireEffectField("UnitPart", "disintegrationEffects", "UnityEngine.GameObject[]");
RequireEffectField("ShipPart", "sinkEffect", "UnityEngine.GameObject");
RequireEffectField("MountedCargo", "damageEffects", "System.Collections.Generic.List`1<DamageEffect>");
RequireEffectField("GroundVehicle", "wreckage", "UnityEngine.GameObject");
RequireEffectField("MapBuilding", "destroyedPrefab", "UnityEngine.GameObject");
RequireEffectField("MapSettingsManager", "Maps", "MapSettingsManager/Map[]");
RequireEffectField("MapSettingsManager/Map", "Details", "NuclearOption.SceneLoading.MapDetails");
RequireEffectField("MapSettingsManager/Map", "Prefab", "MapSettings");
RequireEffectField("NuclearOption.SceneLoading.MapDetails", "PrefabName", "System.String");
Console.WriteLine($"PASS: {effectFields} installed serialized effect asset fields match the fixed source catalog. Native asset traversal remains untested.");
var manager = assembly.MainModule.Types.Single(t => t.Name == "EffectManager");
var impactSet = manager.NestedTypes.Single(t => t.Name == "ImpactSet");
void RequireCacheField(TypeDefinition owner, string name, string expectedType)
{
    var field = owner.Fields.Single(f => f.Name == name);
    if (!field.IsPrivate || field.IsStatic || field.FieldType.FullName != expectedType)
        throw new Exception("Ground impact cache field differs: " + field.FullName);
}
RequireCacheField(manager, "groundImpacts", "System.Collections.Generic.List`1<EffectManager/ImpactSet>");
RequireCacheField(impactSet, "gameObject", "UnityEngine.GameObject");
var singleton = assembly.MainModule.Types.Single(t => t.Name == "SceneSingleton`1");
var singletonField = singleton.Fields.Single(f => f.Name == "i");
if (!singletonField.IsStatic || !singletonField.IsPublic || !(singletonField.FieldType is GenericParameter) ||
    !(manager.BaseType is GenericInstanceType managerBase) || managerBase.ElementType.FullName != singleton.FullName ||
    managerBase.GenericArguments.Count != 1 || managerBase.GenericArguments[0].FullName != manager.FullName)
    throw new Exception("Ground impact singleton is no longer a direct static field.");
var maps = assembly.MainModule.GetType("MapSettingsManager");
if (!(maps.BaseType is GenericInstanceType mapsBase) || mapsBase.ElementType.FullName != singleton.FullName ||
    mapsBase.GenericArguments.Count != 1 || mapsBase.GenericArguments[0].FullName != maps.FullName ||
    singleton.Methods.Any(method => method.IsConstructor && method.IsStatic) || maps.Methods.Any(method => method.IsConstructor && method.IsStatic))
    throw new Exception("Map catalog singleton is no longer a direct field without type initialization.");
Console.WriteLine("PASS: map catalog uses serialized original-prefab fields and a direct cached singleton; no loader getter or static initializer. Native catalog availability remains untested.");
