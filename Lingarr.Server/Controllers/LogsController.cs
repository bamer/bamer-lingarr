using System.Text.Json;
using Lingarr.Server.Attributes;
using Lingarr.Server.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[LingarrAuthorize]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    [HttpGet("stream")]
    public async Task GetLogStreamAsync(CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        
        foreach (var log in InMemoryLogSink.GetRecentLogs(100))
        {
            string json = JsonSerializer.Serialize(log);
            await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        }
        await Response.Body.FlushAsync(cancellationToken);
        
        var logQueue = InMemoryLogSink.LogQueue;
        // Track progress by sequence number: the queue is bounded (1000) so Count
        // stops growing under heavy log load — a count comparison would freeze the stream
        var lastSeq = InMemoryLogSink.GetRecentLogs(100).LastOrDefault()?.Seq ?? 0;
        
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        
        while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
        {
            var pending = logQueue.Where(log => log.Seq > lastSeq).ToList();
            foreach (var log in pending)
            {
                string json = JsonSerializer.Serialize(log);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            }
            if (pending.Count > 0)
            {
                await Response.Body.FlushAsync(cancellationToken);
                lastSeq = pending[^1].Seq;
            }
        }
    }
}
