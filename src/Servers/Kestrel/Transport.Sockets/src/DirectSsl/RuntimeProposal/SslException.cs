// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

namespace System.Net.Security;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Security.SslException

/// <summary>
/// Thrown for OpenSSL operation failures that are not <see cref="SslOperationStatus.WantRead"/>,
/// <see cref="SslOperationStatus.WantWrite"/>, or clean EOF.
/// </summary>
internal sealed class SslException : Exception
{
    public SslException(string message) : base(message) { }
    public SslException(string message, Exception? innerException) : base(message, innerException) { }
    public SslException(string message, string? openSslErrorString) : base(message)
    {
        NativeErrorString = openSslErrorString;
    }

    public string? NativeErrorString { get; }
}
