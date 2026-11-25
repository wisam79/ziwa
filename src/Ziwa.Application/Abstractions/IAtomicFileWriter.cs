namespace Ziwa.Application.Abstractions;

public interface IAtomicFileWriter
{
    Task WriteBytesAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);
}
