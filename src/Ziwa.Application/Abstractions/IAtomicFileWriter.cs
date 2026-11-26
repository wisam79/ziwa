using System.IO;

namespace Ziwa.Application.Abstractions;

public interface IAtomicFileWriter
{
    Task WriteBytesAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    async Task WriteAtomicallyAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await write(buffer, cancellationToken);
        await WriteBytesAtomicallyAsync(destinationPath, buffer.ToArray(), cancellationToken);
    }
}
