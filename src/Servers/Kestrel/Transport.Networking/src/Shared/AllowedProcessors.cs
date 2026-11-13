// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal static class AllowedProcessors
{
    /// <summary>
    /// Returns the logical processors this process may run on (<c>Cpus_allowed_list</c> in <c>/proc/self/status</c>, which reflects cgroup cpusets and <c>taskset</c>),
    /// or <see langword="null"/> when they cannot be read.
    /// </summary>
    internal static HashSet<int>? TryReadLinux()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
                {
                    continue;
                }
                var allowed = new HashSet<int>();
                foreach (var part in line["Cpus_allowed_list:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var range = part.Split('-');
                    var first = int.Parse(range[0], CultureInfo.InvariantCulture);
                    var last = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : first;
                    for (var cpu = first; cpu <= last; cpu++)
                    {
                        allowed.Add(cpu);
                    }
                }
                return allowed;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        return null;
    }
}
