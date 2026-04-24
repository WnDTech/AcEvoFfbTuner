using System;
using System.IO.MemoryMappedFiles;

string[] names = { @"Local\acevo_pmf_physics", @"Local\acevo_pmf_graphics", @"Local\acevo_pmf_static" };
foreach (var name in names)
{
    bool exists = false;
    try
    {
        using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        exists = true;
    }
    catch { }
    Console.WriteLine($"{name} -> {exists}");
}
