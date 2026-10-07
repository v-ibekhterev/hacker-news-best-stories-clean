using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HackerNews.Api.Configuration;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Clients;

public sealed class HackerNewsUpdatesClient(
    HttpClient httpClient,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider)
{
    private const int MaximumEventCharacters = 1_048_576;

    public Task ListenAsync(Action<IReadOnlyCollection<long>> onUpdated, CancellationToken cancellationToken) =>
        ListenAsync(ids =>
        {
            onUpdated(ids);
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task ListenAsync(Func<IReadOnlyCollection<long>, Task> onUpdated, CancellationToken cancellationToken)
    {
        using var response = await OpenEventStreamAsync(cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        await ReadEventsAsync(reader, onUpdated, cancellationToken);
    }

    private async Task<HttpResponseMessage> OpenEventStreamAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "updates.json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var connectTimeout = new CancellationTokenSource(options.Value.RequestTimeout, timeProvider);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connecting.Token);
        connectTimeout.CancelAfter(Timeout.InfiniteTimeSpan);

        try
        {
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                throw new HttpRequestException("Hacker News updates response is not an event stream.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task ReadEventsAsync(StreamReader reader, Func<IReadOnlyCollection<long>, Task> onUpdated,
        CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        var eventName = "";

        while (true)
        {
            var line = await ReadLineWithIdleTimeoutAsync(reader, cancellationToken);

            if (line.Length == 0)
            {
                await DispatchEventAsync(eventName, data, onUpdated);
                eventName = "";
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var (field, value) = ParseField(line);

            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                AppendData(data, value);
            }
        }
    }

    private async Task<string> ReadLineWithIdleTimeoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        using var idleTimeout = new CancellationTokenSource(options.Value.StreamIdleTimeout, timeProvider);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleTimeout.Token);

        return await reader.ReadLineAsync(reading.Token)
            ?? throw new IOException("Hacker News updates stream ended.");
    }

    private static Task DispatchEventAsync(string eventName, StringBuilder data,
        Func<IReadOnlyCollection<long>, Task> onUpdated)
    {
        if (eventName is "cancel" or "auth_revoked")
        {
            throw new HttpRequestException($"Hacker News updates stream signaled {eventName}.");
        }

        return eventName is "put" or "patch"
            ? onUpdated(ExtractItemIds(eventName, data.ToString()))
            : Task.CompletedTask;
    }

    private static (string Field, string Value) ParseField(string line)
    {
        var separator = line.IndexOf(':');

        if (separator < 0)
        {
            return (line, "");
        }

        var value = line[(separator + 1)..];
        return (line[..separator], value.StartsWith(' ') ? value[1..] : value);
    }

    private static void AppendData(StringBuilder data, string value)
    {
        if (data.Length + value.Length + 1 > MaximumEventCharacters)
        {
            throw new JsonException("Hacker News updates event exceeds the size limit.");
        }

        data.Append(value).Append('\n');
    }

    internal static IReadOnlyCollection<long> ExtractItemIds(string eventName, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("data", out var data))
        {
            throw new JsonException("Invalid Firebase updates event envelope.");
        }
        var path = pathElement.GetString()!;
        if (!path.StartsWith('/'))
        {
            throw new JsonException("Invalid Firebase updates event path.");
        }

        var ids = new HashSet<long>();
        if (eventName != "patch")
        {
            ReadAtPath(path, data, ids);
            return ids;
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Firebase patch data must be an object.");
        }

        foreach (var property in data.EnumerateObject())
        {
            ReadAtPath(path.TrimEnd('/') + "/" + property.Name, property.Value, ids);
        }

        return ids;
    }

    private static void ReadAtPath(string path, JsonElement data, HashSet<long> ids)
    {
        if (path == "/")
        {
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items))
            {
                ReadIds(items, ids);
            }
        }
        else if (path == "/items" || path.StartsWith("/items/", StringComparison.Ordinal))
        {
            ReadIds(data, ids);
        }
    }

    private static void ReadIds(JsonElement data, HashSet<long> ids)
    {
        switch (data.ValueKind)
        {
            case JsonValueKind.Number:
                if (!data.TryGetInt64(out var id) || id <= 0)
                {
                    throw new JsonException("Invalid item ID in updates event.");
                }
                ids.Add(id);
                break;
            case JsonValueKind.Array:
                foreach (var item in data.EnumerateArray())
                {
                    ReadIds(item, ids);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in data.EnumerateObject())
                {
                    ReadIds(property.Value, ids);
                }
                break;
            case JsonValueKind.Null:
                break;
            default:
                throw new JsonException("Invalid items data in updates event.");
        }
    }
}
