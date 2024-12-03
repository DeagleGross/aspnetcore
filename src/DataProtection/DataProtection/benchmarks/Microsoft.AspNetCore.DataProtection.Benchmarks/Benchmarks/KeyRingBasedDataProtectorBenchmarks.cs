// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.DataProtection.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class KeyRingBasedDataProtectorBenchmarks
{
    private readonly IDataProtector _dataProtector;
    private readonly int _repeatCount;

    const string LoremIpsumData = """
        Lorem ipsum dolor sit amet, consectetur adipiscing elit.
        In sit amet libero in urna pretium ullamcorper sit amet at est.
        Morbi finibus dui non aliquam faucibus. Maecenas tempor viverra vulputate.
        Sed id luctus nibh. Etiam eu metus ligula.
    """;

    public KeyRingBasedDataProtectorBenchmarks()
    {
        _repeatCount = 100;

        var services = new ServiceCollection()
            .AddDataProtection()
            .Services.BuildServiceProvider();
        _dataProtector = services.GetDataProtector("SamplePurpose");
    }


    [Benchmark]
    public void Protect()
    {
        for (var i = 0; i < _repeatCount; i++)
        {
            _ = _dataProtector.Protect(LoremIpsumData);
        }
    }

    [Benchmark]
    public void Unprotect()
    {
        var protectedData = _dataProtector.Protect(LoremIpsumData);

        for (var i = 0; i < _repeatCount; i++)
        {
            _ = _dataProtector.Unprotect(protectedData);
        }
    }
}
