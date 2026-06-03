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
var fcs = mlc.LoadFromAssemblyPath(Path.Combine(dev, "FFXIVClientStructs.dll"));

void Dump(string full, Func<FieldInfo, bool> filter = null)
{
    var t = fcs.GetType(full);
    Console.WriteLine($"\n==== {full} {(t == null ? "(MISSING)" : "")}");
    if (t == null) return;
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        if (filter == null || filter(f))
            Console.WriteLine($"   {f.FieldType.Name} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.Contains("Instance")))
        Console.WriteLine($"   static {m.ReturnType.Name} {m.Name}()");
}

Dump("FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device");
Dump("FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.SwapChain");
Dump("FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera", f => f.FieldType.Name.Contains("Matrix") || f.Name.Contains("Matrix"));
Dump("FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera", f => f.FieldType.Name.Contains("Matrix") || f.Name.Contains("Matrix"));

// Find depth/rendertarget managers
Console.WriteLine("\n==== types with RenderTarget/DepthStencil in name ====");
foreach (var t in fcs.GetTypes())
    if (t.Name.Contains("RenderTarget") || t.Name.Contains("DepthStencil") || t.Name == "RenderManager")
        Console.WriteLine("   " + t.FullName);
