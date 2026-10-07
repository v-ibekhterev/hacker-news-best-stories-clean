using HackerNews.Api.Exports;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.Api.Tests;

public sealed class ExportTokenTests
{
    [Fact]
    public async Task Tokens_RestartAndTampering_ValidateDurableBinding()
    {
        var time = new FakeTimeProvider();
        var store = new StubExportStore();
        var tokens = new ExportTokens(store, time);
        await tokens.InitializeAsync(default);
        var cursor = new ExportCursor(1, Guid.NewGuid(), 4, 2, 10, time.GetUtcNow().AddHours(1).ToUnixTimeSeconds());
        var token = tokens.Protect(cursor);
        var restarted = new ExportTokens(store, time);
        await restarted.InitializeAsync(default);
        Assert.Equal(cursor, restarted.Unprotect(token, cursor.OperationId, 10));
        Assert.Null(restarted.Unprotect(token, Guid.NewGuid(), 10));
        Assert.Null(restarted.Unprotect(token, cursor.OperationId, 11));
        Assert.Null(restarted.Unprotect("x" + token, cursor.OperationId, 10));
        Assert.Null(restarted.Unprotect(tokens.Protect(cursor with { Version = 2 }), cursor.OperationId, 10));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Null(restarted.Unprotect(token, cursor.OperationId, 10));
    }

    [Fact]
    public void Options_InvalidBudgetsAndIntervals_AreRejected()
    {
        Assert.True(new ExportOptions().IsValid());
        Assert.False(new ExportOptions { MaximumReservedRows = 1 }.IsValid());
        Assert.False(new ExportOptions { ChunkSize = 1001 }.IsValid());
        Assert.False(new ExportOptions { MaximumResponseBytes = 1024 }.IsValid());
        Assert.False(new ExportOptions { JobDeadline = TimeSpan.Zero }.IsValid());
        Assert.False(new ExportOptions { Retention = TimeSpan.FromDays(100) }.IsValid());
    }
}
