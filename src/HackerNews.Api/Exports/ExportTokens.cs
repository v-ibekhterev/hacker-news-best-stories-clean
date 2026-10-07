using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace HackerNews.Api.Exports;

public sealed class ExportTokens(IExportStore store, TimeProvider timeProvider)
{
    private byte[]? key;

    public async Task InitializeAsync(CancellationToken cancellationToken) =>
        key = await store.GetSigningKeyAsync(cancellationToken);

    public string Protect(ExportCursor cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor);
        var signature = HMACSHA256.HashData(Key, bytes);
        return WebEncoders.Base64UrlEncode(bytes) + "." + WebEncoders.Base64UrlEncode(signature);
    }

    public ExportCursor? Unprotect(string token, Guid operationId, int pageSize)
    {
        if (token.Length > 2048)
        {
            return null;
        }
        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(parts[0]);
            var signature = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(Key, bytes)))
            {
                return null;
            }
            var cursor = JsonSerializer.Deserialize<ExportCursor>(bytes);
            return cursor is { Version: 1, Position: > 0 } && cursor.OperationId == operationId &&
                cursor.PageSize == pageSize && cursor.ExpiresUnixSeconds > timeProvider.GetUtcNow().ToUnixTimeSeconds()
                ? cursor : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private byte[] Key => key ?? throw new InvalidOperationException("Export signing key is not initialized.");
}
