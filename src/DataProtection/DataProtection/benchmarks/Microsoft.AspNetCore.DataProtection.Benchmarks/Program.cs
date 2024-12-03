using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

//using Microsoft.AspNetCore.DataProtection.Benchmarks.Benchmarks;

//// for debug
//KeyRingBasedDataProtectorBenchmarks b = new();
//b.Protect();
//Console.ReadLine();
