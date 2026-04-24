using System;
using System.Reflection;
using System.Linq;

var asm = Assembly.LoadFrom(@"C:\Users\paul_\.nuget\packages\scottplot\5.0.56\lib\net8.0\ScottPlot.dll");
foreach (var t in asm.ExportedTypes.Where(t => t.Name.Contains("Scatter")))
    Console.WriteLine(t.FullName);

Console.WriteLine("\n--- DataSources ---");
var ns = asm.ExportedTypes.Where(t => t.Namespace?.Contains("DataSource") == true);
foreach (var t in ns)
    Console.WriteLine(t.FullName);

Console.WriteLine("\n--- Scatter Plottable props ---");
var scatterType = asm.GetType("ScottPlot.Plottables.Scatter");
if (scatterType != null)
{
    foreach (var p in scatterType.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get;" : "")} {(p.CanWrite ? "set;" : "")} }}");
}
