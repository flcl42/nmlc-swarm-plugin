// SPDX-FileCopyrightText: 2026 flcl42
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Microsoft.Extensions.Configuration;
using Nethermind.JsonRpc;

namespace Nmlc.Swarm.Plugin;

/// <inheritdoc />
public sealed class SwarmRpcModule(IConfiguration configuration) : ISwarmRpcModule, IDisposable
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxConcurrentDownloads = 4;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient? _client = CreateClient(configuration["Swarm:BeeUrl"]);
    private int _activeDownloads;

    /// <inheritdoc />
    public Task<ResultWrapper<byte[]>> swarm_getBytes(string reference, CancellationToken cancellationToken) =>
        DownloadAsync("bytes", reference, cancellationToken);

    /// <inheritdoc />
    public Task<ResultWrapper<byte[]>> swarm_getFile(string reference, CancellationToken cancellationToken) =>
        DownloadAsync("bzz", reference, cancellationToken);

    private async Task<ResultWrapper<byte[]>> DownloadAsync(string endpoint, string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) reference = reference[2..];
        if (reference.Length != 64 || !IsHex(reference))
            return ResultWrapper<byte[]>.Fail("Expected a 32-byte hexadecimal Swarm reference.", ErrorCodes.InvalidParams);
        if (_client is null)
            return ResultWrapper<byte[]>.Fail("Set Swarm:BeeUrl to an HTTP or HTTPS Bee API endpoint.", ErrorCodes.ResourceUnavailable);

        if (Interlocked.Increment(ref _activeDownloads) > MaxConcurrentDownloads)
        {
            Interlocked.Decrement(ref _activeDownloads);
            return ResultWrapper<byte[]>.Fail("Too many concurrent Swarm downloads.", ErrorCodes.LimitExceeded);
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            string path = endpoint == "bzz" ? $"bzz/{reference}/" : $"bytes/{reference}";
            using HttpResponseMessage response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ResultWrapper<byte[]>.Fail("Swarm content was not found.", ErrorCodes.StandardResourceNotFound);
            if (response.StatusCode != HttpStatusCode.OK)
                return ResultWrapper<byte[]>.Fail("Bee could not retrieve the Swarm content.", ErrorCodes.ResourceUnavailable);

            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token);
            return ResultWrapper<byte[]>.Success(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return ResultWrapper<byte[]>.Fail("Swarm content exceeds 1 MiB.", ErrorCodes.LimitExceeded);
        }
        catch (HttpRequestException)
        {
            return ResultWrapper<byte[]>.Fail("Bee could not retrieve the Swarm content.", ErrorCodes.ResourceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ResultWrapper<byte[]>.Fail("Swarm download timed out.", ErrorCodes.Timeout);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);
        }
    }

    private static bool IsHex(ReadOnlySpan<char> reference)
    {
        foreach (char digit in reference)
            if (!char.IsAsciiHexDigit(digit)) return false;
        return true;
    }

    private static HttpClient? CreateClient(string? beeUrl) =>
        Uri.TryCreate(beeUrl, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" && uri.Host.Length > 0 &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }
            : null;

    /// <inheritdoc />
    public void Dispose() => _client?.Dispose();
}
