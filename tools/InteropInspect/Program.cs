using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

// Prints types / members of the generated IL2CPP interop assemblies without loading them for execution.
// Usage: InteropInspect <TypeNameRegex> [MemberRegex] [--game <dir>] [--all] [--static]
const string DefaultGameDir = @"D:\SteamLibrary\steamapps\common\Can 'Em All";

var positional = new List<string>();
string gameDir = Environment.GetEnvironmentVariable("CANEMALL_DIR") ?? DefaultGameDir;
bool listOnly = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameDir = args[++i];
    else if (args[i] == "--types") listOnly = true;
    else positional.Add(args[i]);
}

if (positional.Count == 0)
{
    Console.WriteLine("usage: InteropInspect <TypeNameRegex> [MemberRegex] [--types] [--game <dir>]");
    return 1;
}

var typeRx = new Regex(positional[0], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
var memberRx = positional.Count > 1 ? new Regex(positional[1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null;

var dirs = new[] { Path.Combine(gameDir, "BepInEx", "interop"), Path.Combine(gameDir, "BepInEx", "core") };
var paths = new List<string>();
foreach (var d in dirs)
    if (Directory.Exists(d)) paths.AddRange(Directory.GetFiles(d, "*.dll"));
if (paths.Count == 0)
{
    Console.Error.WriteLine($"no assemblies found under {gameDir}");
    return 2;
}
paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

// Same simple name can exist in several folders; first one wins (interop before core before runtime).
var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var p in paths) byName.TryAdd(Path.GetFileName(p), p);

var resolver = new PathAssemblyResolver(byName.Values);
using var mlc = new MetadataLoadContext(resolver, typeof(object).Assembly.GetName().Name);

int shown = 0;
foreach (var file in dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.dll")).OrderBy(Path.GetFileName))
{
    Assembly asm;
    try { asm = mlc.LoadFromAssemblyPath(file); }
    catch (Exception) { continue; } // native / non-managed files

    Type[] types;
    try { types = asm.GetTypes(); }
    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
    catch (Exception) { continue; }

    var named = new List<(string Name, Type Type)>();
    foreach (var t in types)
    {
        // Some generated nested types cannot be resolved by MetadataLoadContext; skip them.
        try { if (t.FullName != null) named.Add((t.FullName, t)); } catch (Exception) { }
    }
    foreach (var (name, t) in named.OrderBy(n => n.Name, StringComparer.Ordinal))
    {
        if (!typeRx.IsMatch(name)) continue;
        PrintType(asm, t);
        shown++;
    }
}

if (shown == 0) Console.WriteLine("(no matching types)");
return 0;

void PrintType(Assembly asm, Type t)
{
    string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : "class";
    string baseName = "";
    try { if (t.BaseType != null && t.BaseType.FullName != "System.Object") baseName = " : " + Short(t.BaseType); } catch { }
    Console.WriteLine($"[{asm.GetName().Name}] {kind} {t.FullName}{baseName}");
    if (listOnly) return;

    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    try
    {
        foreach (var f in t.GetFields(F))
        {
            if (f.Name.StartsWith("NativeFieldInfoPtr_") || f.Name.StartsWith("NativeMethodInfoPtr_") || f.Name.StartsWith("NativeClassPtr")) continue;
            if (t.IsEnum && f.Name == "value__") continue;
            if (!Match(f.Name)) continue;
            Console.WriteLine($"    field    {(f.IsStatic ? "static " : "")}{Short(f.FieldType)} {f.Name}");
        }
        foreach (var p in t.GetProperties(F))
        {
            if (!Match(p.Name)) continue;
            var acc = (p.GetMethod != null ? "get;" : "") + (p.SetMethod != null ? "set;" : "");
            bool st = (p.GetMethod ?? p.SetMethod)?.IsStatic == true;
            Console.WriteLine($"    property {(st ? "static " : "")}{Short(p.PropertyType)} {p.Name} {{ {acc} }}");
        }
        foreach (var c in t.GetConstructors(F))
        {
            if (!Match(".ctor")) continue;
            Console.WriteLine($"    ctor     ({Params(c)})");
        }
        foreach (var m in t.GetMethods(F))
        {
            if (m.IsSpecialName) continue;
            if (m.Name.StartsWith("NativeMethodInfoPtr_")) continue;
            if (!Match(m.Name)) continue;
            Console.WriteLine($"    method   {(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({Params(m)})");
        }
        if (t.IsEnum)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (Match(f.Name)) Console.WriteLine($"    value    {f.Name} = {f.GetRawConstantValue()}");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"    (member load failed: {e.GetType().Name}: {e.Message})");
    }
}

bool Match(string name) => memberRx == null || memberRx.IsMatch(name);

string Params(MethodBase m)
{
    try { return string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}")); }
    catch { return "?"; }
}

string Short(Type t)
{
    try
    {
        if (t.IsGenericType)
        {
            var n = t.GetGenericTypeDefinition().FullName ?? t.Name;
            int tick = n.IndexOf('`');
            if (tick >= 0) n = n[..tick];
            return $"{n}<{string.Join(", ", t.GetGenericArguments().Select(Short))}>";
        }
        if (t.IsArray) return Short(t.GetElementType()) + "[]";
        if (t.IsByRef) return "ref " + Short(t.GetElementType());
        return t.FullName ?? t.Name;
    }
    catch { return "?"; }
}
