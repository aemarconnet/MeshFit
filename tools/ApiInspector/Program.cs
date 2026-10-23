// Lists members of SOLIDWORKS interop types without SOLIDWORKS installed (metadata only).
//
//   dotnet run --project tools/ApiInspector -- <Type[,Type...]> [memberFilter[,filter...]] [--dir <folder>]
//
// Examples:
//   dotnet run --project tools/ApiInspector -- IPropertyManagerPageListbox
//   dotnet run --project tools/ApiInspector -- IModelDoc2,IModelDocExtension Select
//   dotnet run --project tools/ApiInspector -- swNumberboxUnitType_e            (enum names and values)
//
// By default the interop DLLs are read from the NuGet cache (~/.nuget/packages/solidworks.interop.*),
// which exists after one restore/build of src/MeshFit.SolidWorks.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

var argList = args.ToList();
string dir = null;
int d = argList.IndexOf("--dir");
if (d >= 0 && d + 1 < argList.Count) { dir = argList[d + 1]; argList.RemoveRange(d, 2); }
if (argList.Count == 0)
{
    Console.WriteLine("usage: ApiInspector <Type[,Type...]> [memberFilter[,...]] [--dir <folder with interop dlls>]");
    return 1;
}
dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var dlls = Directory.GetFiles(dir, "SolidWorks.Interop.*.dll", SearchOption.AllDirectories);
if (dlls.Length == 0) { Console.WriteLine($"No SolidWorks.Interop.*.dll under {dir}. Build src/MeshFit.SolidWorks once to restore them."); return 1; }
var resolverPaths = dlls.Concat(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));
var types = argList[0].Split(',');
var filters = argList.Count > 1 ? argList[1].Split(',') : null;
foreach (var path in dlls.GroupBy(Path.GetFileName).Select(g => g.Last()))
{
    var asm = mlc.LoadFromAssemblyPath(path);
    foreach (var t in asm.GetTypes().Where(t => types.Contains(t.Name)))
    {
        if (t.IsEnum)
        {
            foreach (var f in t.GetFields().Where(f => f.IsLiteral))
                if (filters == null || filters.Any(x => f.Name.Contains(x))) Console.WriteLine($"{t.Name}.{f.Name} = {f.GetRawConstantValue()}");
            continue;
        }
        foreach (var m in t.GetMembers())
            if (filters == null || filters.Any(x => m.Name.Contains(x))) Console.WriteLine($"{t.Name}.{m}");
    }
}
return 0;
