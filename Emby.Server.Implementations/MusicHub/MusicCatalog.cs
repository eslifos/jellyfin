using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using MediaBrowser.Common.Net;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// An online music catalog: one request at a time, well spaced, and it steps aside when it stops answering.
/// </summary>
internal sealed class MusicCatalog
{
    private const string ClientName = "CosmicMusic/1.0";
    private const int MaxFailuresInARow = 5;

    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _backOff = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly TimeSpan _spacing;
    private readonly IHttpClientFactory _httpClientFactory;
    private DateTime _nextRequest = DateTime.MinValue;

    public MusicCatalog(string name, TimeSpan spacing, IHttpClientFactory httpClientFactory)
    {
        Name = name;
        _spacing = spacing;
        _httpClientFactory = httpClientFactory;
    }

    public string Name { get; }

    public int Failures { get; set; }

    /// <summary>Gets or sets the language the answers are preferred in.</summary>
    public string Language { get; set; } = MetadataLocale.FallbackLanguage;

    public bool IsOff => Failures >= MaxFailuresInARow;

    /// <summary>Stops asking for a while because the catalog said it has had enough.</summary>
    /// <param name="wait">How long the catalog asked to wait, if it said so.</param>
    public void BackOff(TimeSpan? wait = null)
        => _nextRequest = DateTime.UtcNow + (wait > TimeSpan.Zero ? wait.Value : _backOff);

    /// <summary>Allows the next request right away.</summary>
    public void Ready() => _nextRequest = DateTime.MinValue;

    /// <summary>Gets an answer; null when the catalog does not have what was asked for.</summary>
    /// <typeparam name="T">The type of the answer.</typeparam>
    /// <param name="url">The address.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The answer.</returns>
    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
        where T : class
    {
        var wait = _nextRequest - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        _nextRequest = DateTime.UtcNow + _spacing;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ClientName);
            request.Headers.TryAddWithoutValidation("Accept-Language", Language);
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
            {
                var retry = response.Headers.RetryAfter;
                BackOff(retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow));
                throw new HttpRequestException(Name + " asked to wait", null, response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // The address may carry a private key, so it never goes into the error
                throw new HttpRequestException(Name + " could not answer", null, response.StatusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, _json, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException(Name + " sent an unexpected answer", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(Name + " took too long", ex);
        }
    }
}
