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
var ig = mlc.LoadFromAssemblyPath(Path.Combine(dev, "Dalamud.Bindings.ImGui.dll"));
var imgui = ig.GetType("Dalamud.Bindings.ImGui.ImGui");

Console.WriteLine("== ImGui capture-mouse APIs ==");
foreach (var m in imgui.GetMethods().Where(m => m.Name.Contains("WantCapture") || m.Name.Contains("CaptureMouse") || m.Name == "GetIO"))
    Console.WriteLine("  " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");

Console.WriteLine("\n== ImGuiIOPtr.WantCaptureMouse (settable?) ==");
var io = ig.GetTypes().FirstOrDefault(t => t.Name == "ImGuiIOPtr");
if (io != null)
    foreach (var p in io.GetProperties().Where(p => p.Name.Contains("WantCapture")))
        Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}  canWrite={p.CanWrite}");
