using System.Net.Http.Headers;

namespace XcavateProfileApi.Services.Notifications;

/// <summary>Where and with which key to reach the realXmarketNotificationsApi.</summary>
public sealed class NotificationsOptions
{
    public const string DefaultBaseUrl = "https://notifications-api.xcavate.io";

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public string ApiKey { get; init; } = string.Empty;
}

/// <summary>
/// Client for the notifications service's server-to-server send endpoint. Failures are logged,
/// never thrown: by the time a payload gets here the mutation it reports on has already
/// succeeded, so delivery problems must stay invisible to callers.
/// </summary>
public class NotificationsApiClient(
    IHttpClientFactory httpFactory,
    NotificationsOptions options,
    ILogger<NotificationsApiClient> logger)
{
    public const string HttpClientName = "notifications-api";

    /// <summary>The trailing slash is required — Django redirects the unslashed form.</summary>
    private const string SendPath = "api/fcm/send-notification/";

    private readonly Uri _sendUri = new(new Uri(options.BaseUrl.TrimEnd('/') + "/"), SendPath);

    public async Task SendAsync(PushNotification notification, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _sendUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", options.ApiKey);
            request.Content = JsonContent.Create(new
            {
                chain = notification.Chain,
                address = notification.Address,
                title = notification.Title,
                body = notification.Body
            });

            var http = httpFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Expected for members who never installed the app: their address has no linked
                // device on the notifications service.
                logger.LogInformation(
                    "Notifications API returned {Status} for {Chain}:{Address}",
                    (int)response.StatusCode, notification.Chain, notification.Address);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to send push notification to {Chain}:{Address}",
                notification.Chain, notification.Address);
        }
    }
}
