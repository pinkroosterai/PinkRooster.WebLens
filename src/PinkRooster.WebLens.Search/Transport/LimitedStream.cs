namespace PinkRooster.WebLens.Search;

/// <summary>Reads a response body but fails once it exceeds a byte cap, so an oversize body is a protocol failure, not an out-of-memory.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public static async Task<Stream> OpenAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limit)
        {
            throw new InvalidDataException($"Response body of {length} bytes exceeds the {limit} byte limit.");
        }

        return new LimitedStream(await response.Content.ReadAsStreamAsync(ct), limit);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken);
        Count(count);
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private void Count(int read)
    {
        _read += read;
        if (_read > limit)
        {
            throw new InvalidDataException($"Response body exceeds the {limit} byte limit.");
        }
    }
}
