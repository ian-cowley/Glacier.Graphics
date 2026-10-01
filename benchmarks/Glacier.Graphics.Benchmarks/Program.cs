namespace Glacier.Graphics.Benchmarks;

using System;
using BenchmarkDotNet.Running;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--bdn", StringComparison.OrdinalIgnoreCase))
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return;
        }

        DirectBenchmarkRunner.RunAll();
    }
}
