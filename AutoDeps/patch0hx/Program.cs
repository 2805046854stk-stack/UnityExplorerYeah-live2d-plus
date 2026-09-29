using System;
using Mono.Cecil;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: patch0hx <in.dll> <out.dll> [newVersion=0.11.4.0]");
            return 2;
        }
        string input = args[0];
        string output = args[1];
        Version newVer = new Version(args.Length > 2 ? args[2] : "0.11.4.0");

        var rp = new ReaderParameters { ReadWrite = false, ReadSymbols = false };
        var asm = AssemblyDefinition.ReadAssembly(input, rp);
        bool found = false;
        foreach (var aref in asm.MainModule.AssemblyReferences)
        {
            if (aref.Name == "Mono.Cecil")
            {
                Console.WriteLine("before: " + aref.FullName);
                aref.Version = newVer;
                Console.WriteLine("after : " + aref.FullName);
                found = true;
            }
        }
        if (!found)
        {
            Console.Error.WriteLine("No Mono.Cecil AssemblyRef found!");
            return 1;
        }
        var wp = new WriterParameters();
        asm.Write(output, wp);
        Console.WriteLine("written: " + output);
        return 0;
    }
}
