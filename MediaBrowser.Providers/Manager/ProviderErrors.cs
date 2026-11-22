using System;
using System.Net;
using System.Net.Http;

namespace MediaBrowser.Providers.Manager;

/// <summary>
/// Tells real provider failures apart from "this title is not on that site", which is normal
/// (anime or local titles missing from TheTVDB, ids that the site merged or removed...).
/// </summary>
internal static class ProviderErrors
{
    /// <summary>Whether the provider only answered that the item does not exist there (HTTP 404).</summary>
    /// <param name="ex">The exception.</param>
    /// <returns>True when it is a "not found".</returns>
    public static bool IsNotFound(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                || e.GetType().Name.Contains("NotFound", StringComparison.Ordinal)
                || e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("Status: 404", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
