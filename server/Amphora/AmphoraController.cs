using System.Text;
using Microsoft.AspNetCore.Mvc;
using PithosDB.Core;

[ApiController]
[Route("v1")]
public class AmphoraController : ControllerBase
{
    private readonly PithosDb _db;

    public AmphoraController(PithosDb db) => _db = db;

    [HttpGet("keys/{**key}")]
    public IActionResult Get(string key)
    {
        return _db.TryGet(Encoding.UTF8.GetBytes(key), out var value)
            ? File(value!, "application/octet-stream")
            : NotFound();
    }

    [HttpPut("keys/{**key}")]
    public async Task<IActionResult> Put(string key, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        await _db.PutAsync(Encoding.UTF8.GetBytes(key), ms.ToArray(), ct);
        return NoContent();
    }

    [HttpDelete("keys/{**key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        await _db.DeleteAsync(Encoding.UTF8.GetBytes(key), ct);
        return NoContent();
    }

    [HttpGet("scan")]
    public IActionResult Scan(string? prefix, string? from, string? to)
    {
        IEnumerable<(byte[] key, byte[] value)> entries = prefix is not null
            ? _db.ScanPrefix(Encoding.UTF8.GetBytes(prefix))
            : _db.Scan(
                from is not null ? Encoding.UTF8.GetBytes(from) : null,
                to   is not null ? Encoding.UTF8.GetBytes(to)   : null);

        return Ok(entries.Select(e => new
        {
            key   = Encoding.UTF8.GetString(e.key),
            value = Convert.ToBase64String(e.value),
        }));
    }

    [HttpPost("batch")]
    public async Task<IActionResult> Batch([FromBody] BatchOp[] ops, CancellationToken ct)
    {
        var batch = new WriteBatch();
        foreach (var op in ops)
        {
            var keyBytes = Encoding.UTF8.GetBytes(op.Key);
            if (op.Op == "put")
                batch.Put(keyBytes, Convert.FromBase64String(op.Value ?? string.Empty));
            else if (op.Op == "delete")
                batch.Delete(keyBytes);
        }
        await _db.WriteAsync(batch, ct);
        return NoContent();
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct) =>
        Ok(await _db.GetStatsAsync(ct));
}

public record BatchOp(string Op, string Key, string? Value);
