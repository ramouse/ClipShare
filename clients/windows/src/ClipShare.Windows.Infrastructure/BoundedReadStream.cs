namespace ClipShare.Windows.Infrastructure;

internal sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, LimitCount(count));
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer[..LimitCount(buffer.Length)], cancellationToken)
            .ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override int ReadByte()
    {
        if (_bytesRead >= maximumBytes)
        {
            EnsureSourceFinished();
            return -1;
        }

        int value = inner.ReadByte();
        if (value >= 0)
        {
            _bytesRead++;
        }

        return value;
    }

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
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int LimitCount(int requested)
    {
        if (_bytesRead >= maximumBytes)
        {
            EnsureSourceFinished();
            return 0;
        }

        return (int)Math.Min(requested, maximumBytes - _bytesRead);
    }

    private void Account(int read) => _bytesRead = checked(_bytesRead + read);

    private void EnsureSourceFinished()
    {
        if (inner.ReadByte() >= 0)
        {
            throw new InvalidDataException("上传流超过允许的最大字节数。");
        }
    }
}
