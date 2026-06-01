using System.Reflection;

string dev = Environment.ExpandEnvironmentVariables(@"%APPDATA%\XIVLauncher\addon\Hooks\dev");
var paths = new List<string>(Directory.GetFiles(dev, "*.dll"));
string SharedLatest(string app)
{
    var b = $@"C:\Program Files\dotnet\shared\{app}";
    return Directory.Exists(b) ? Directory.GetDirectories(b).OrderBy(x => x).Last() : null;
}
foreach (var app in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
{
    var d = SharedLatest(app);
    if (d != null) paths.AddRange(Directory.GetFiles(d, "*.dll"));
}
var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.GroupBy(Path.GetFileName).Select(g => g.First())));
Assembly Load(string n) => mlc.LoadFromAssemblyPath(Path.Combine(dev, n));
var dal = Load("Dalamud.dll");
var igAsm = Load("Dalamud.Bindings.ImGui.dll");

string Sig(MethodInfo m) => m.ReturnType.Name + " " + m.Name + "(" +
    string.Join(", ", m.GetParameters().Select(p => (p.ParameterType.IsByRef ? "ref " : "") + TypeName(p.ParameterType) + " " + p.Name)) + ")";
string TypeName(Type t) => t.IsByRef ? t.GetElementType().Name : t.Name;

// ImDrawListPtr image/circle methods
var dlp = igAsm.GetType("Dalamud.Bindings.ImGui.ImDrawListPtr");
Console.WriteLine("== ImDrawListPtr AddImageQuad / AddImage / AddCircle ==");
foreach (var m in dlp.GetMethods().Where(m => m.Name is "AddImageQuad" or "AddImage" or "AddCircle").Take(8))
    Console.WriteLine("  " + Sig(m));

// ITextureProvider
var tp = dal.GetType("Dalamud.Plugin.Services.ITextureProvider");
Console.WriteLine("\n== ITextureProvider (Create*/raw) ==");
if (tp != null)
    foreach (var m in tp.GetMethods().Where(m => m.Name.Contains("Create") || m.Name.Contains("Raw")))
        Console.WriteLine("  " + Sig(m));

// IDalamudTextureWrap — search by simple name
Console.WriteLine("\n== IDalamudTextureWrap ==");
var tw = dal.GetTypes().FirstOrDefault(t => t.Name == "IDalamudTextureWrap");
if (tw != null)
{
    Console.WriteLine("  full: " + tw.FullName);
    foreach (var p in tw.GetProperties()) Console.WriteLine("    prop " + p.PropertyType.Name + " " + p.Name);
    foreach (var m in tw.GetMethods().Where(m => !m.IsSpecialName)) Console.WriteLine("    " + Sig(m));
}

// RawImageSpecification
Console.WriteLine("\n== RawImageSpecification ==");
var ris = dal.GetTypes().FirstOrDefault(t => t.Name == "RawImageSpecification");
if (ris != null)
{
    Console.WriteLine("  full: " + ris.FullName);
    foreach (var c in ris.GetConstructors()) Console.WriteLine("    ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
    foreach (var p in ris.GetProperties()) Console.WriteLine("    prop " + p.PropertyType.Name + " " + p.Name);
    foreach (var m in ris.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => !m.IsSpecialName)) Console.WriteLine("    static " + Sig(m));
}

// ImTextureID
Console.WriteLine("\n== ImTextureID ==");
var tid = igAsm.GetTypes().FirstOrDefault(t => t.Name == "ImTextureID");
if (tid != null) { foreach (var c in tid.GetConstructors()) Console.WriteLine("    ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")"); foreach (var op in tid.GetMethods().Where(m => m.IsSpecialName && m.Name.StartsWith("op_"))) Console.WriteLine("    " + Sig(op)); }
