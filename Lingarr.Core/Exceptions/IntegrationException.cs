using System.Net;

namespace Lingarr.Core.Exceptions;

/// <summary>
/// Thrown when an external integration (Radarr, Sonarr, etc.) returns an error.
/// Carries the HTTP status code so callers can branch on 404, 500, etc.
/// </summary>
public sealed class IntegrationException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }

    public IntegrationException(string message, HttpStatusCode? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public IntegrationException(string message, Exception innerException, HttpStatusCode? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
