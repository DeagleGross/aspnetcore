// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DirectSslTransportApp;

/// <summary>
/// Prints a startup banner that identifies the loaded <c>System.Net.Security</c> assembly and the
/// native OpenSSL shim. Used to confirm that the patched runtime DLLs (built from the TlsSession
/// PoC branch) have been overlaid correctly onto the running .NET shared framework.
/// </summary>
/// <remarks>
/// The verification is reflection-only: it does not statically reference <c>TlsContext</c> or
/// <c>TlsSession</c>, so it compiles cleanly even when the unpatched net10 SDK ref assembly is in
/// use. Discovery for the native shim looks next to <c>typeof(SslStream).Assembly.Location</c> and
/// is only attempted on Linux.
/// </remarks>
internal static class RuntimeVerification
{
    private const string ManagedAssemblyName = "System.Net.Security";
    private const string NativeShimName = "libSystem.Security.Cryptography.Native.OpenSsl.so";

    private static readonly string[] PatchedApiTypes =
    [
        "System.Net.Security.TlsContext",
        "System.Net.Security.TlsSession",
        "System.Net.Security.TlsOperationStatus",
    ];

    public static void Print(TextWriter? writer = null)
    {
        writer ??= Console.Out;

        var sb = new StringBuilder();
        sb.AppendLine("=== TlsSession PoC — Runtime Assembly Verification ===");

        AppendManagedAssemblyInfo(sb);
        sb.AppendLine();
        AppendNativeShimInfo(sb);

        sb.AppendLine("=== End Verification ===");
        writer.Write(sb.ToString());
        writer.Flush();
    }

    private static void AppendManagedAssemblyInfo(StringBuilder sb)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ManagedAssemblyName}:");

        var asm = typeof(SslStream).Assembly;
        var location = asm.Location;

        if (string.IsNullOrEmpty(location))
        {
            sb.AppendLine("  Location:           *** could not determine (single-file deploy or in-memory load) ***");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Version:            {asm.GetName().Version}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  MVID:               {{{asm.ManifestModule.ModuleVersionId}}}");
            sb.AppendLine("  SHA256:             (skipped — no file path)");
            sb.AppendLine("  File size:          (skipped — no file path)");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Location:           {location}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Version:            {asm.GetName().Version}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  MVID:               {{{asm.ManifestModule.ModuleVersionId}}}");

            try
            {
                var hash = ComputeSha256(location);
                var size = new FileInfo(location).Length;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  SHA256:             {hash}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  File size:          {size.ToString("N0", CultureInfo.InvariantCulture)} bytes");
            }
            catch (Exception ex)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  SHA256:             *** failed: {ex.GetType().Name}: {ex.Message} ***");
                sb.AppendLine("  File size:          (skipped)");
            }
        }

        var missingTypes = new System.Collections.Generic.List<string>();
        foreach (var fullName in PatchedApiTypes)
        {
            var present = asm.GetType(fullName, throwOnError: false) is not null;
            var shortName = fullName.AsSpan(fullName.LastIndexOf('.') + 1).ToString();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Has {shortName,-22} {present}");
            if (!present)
            {
                missingTypes.Add(shortName);
            }
        }

        if (missingTypes.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  *** WARNING: Patched API types not found: {string.Join(", ", missingTypes)}. Overlay may have failed. ***");
        }
    }

    private static void AppendNativeShimInfo(StringBuilder sb)
    {
        sb.AppendLine("Native OpenSSL Shim:");

        if (!OperatingSystem.IsLinux())
        {
            sb.AppendLine("  (skipped — non-Linux platform; native shim only ships on Linux)");
            return;
        }

        var sslStreamLocation = typeof(SslStream).Assembly.Location;
        if (string.IsNullOrEmpty(sslStreamLocation))
        {
            sb.AppendLine("  (skipped — could not determine shared framework directory)");
            return;
        }

        var sharedFxDir = Path.GetDirectoryName(sslStreamLocation);
        if (string.IsNullOrEmpty(sharedFxDir))
        {
            sb.AppendLine("  (skipped — could not determine shared framework directory)");
            return;
        }

        var nativePath = Path.Combine(sharedFxDir, NativeShimName);
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Location:           {nativePath}");

        if (!File.Exists(nativePath))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  *** NOT FOUND at {nativePath} ***");
            return;
        }

        try
        {
            var hash = ComputeSha256(nativePath);
            var size = new FileInfo(nativePath).Length;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  SHA256:             {hash}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  File size:          {size.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        }
        catch (Exception ex)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  SHA256:             *** failed: {ex.GetType().Name}: {ex.Message} ***");
            sb.AppendLine("  File size:          (skipped)");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
