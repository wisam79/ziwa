using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Auth;

namespace Ziwa.Infrastructure.Auth;

public sealed class AtomicLocalAuthSessionStore : ILocalAuthSessionStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _authDirectory;
    private readonly string _sessionFilePath;
    private readonly string _signatureFilePath;
    private readonly IAtomicFileWriter _atomicWriter;
    private readonly byte[] _encryptionKey;
    private readonly byte[] _signingKey;

    public AtomicLocalAuthSessionStore(
        ProjectStorageOptions storageOptions,
        IAtomicFileWriter atomicWriter)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        _atomicWriter = atomicWriter;
        _authDirectory = Path.Combine(storageOptions.RootDirectory, "Auth");
        _sessionFilePath = Path.Combine(_authDirectory, "session.bin");
        _signatureFilePath = Path.Combine(_authDirectory, "license.sig");

        string seedMaterial = $"{storageOptions.RootDirectory}|{Environment.UserName}|{Environment.MachineName}|ZiwaStudio.Auth.v1";
        _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes("enc:" + seedMaterial));
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes("sig:" + seedMaterial));
    }

    public async Task<PersistedAuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sessionFilePath))
        {
            return null;
        }

        byte[] envelope;
        try
        {
            envelope = await File.ReadAllBytesAsync(_sessionFilePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (envelope.Length <= NonceSize + TagSize)
        {
            return null;
        }

        ReadOnlySpan<byte> nonce = envelope.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = envelope.AsSpan(NonceSize, TagSize);
        ReadOnlySpan<byte> cipher = envelope.AsSpan(NonceSize + TagSize);
        byte[] plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(_encryptionKey, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            PersistedAuthSession? loaded = JsonSerializer.Deserialize<PersistedAuthSession>(plain, JsonOptions);
            if (loaded is null || loaded.Profile is null || string.IsNullOrWhiteSpace(loaded.Profile.UserId))
            {
                return null;
            }

            if (!string.Equals(loaded.Profile.Plan, "free", StringComparison.OrdinalIgnoreCase) &&
                !await VerifyLicenseSignatureAsync(loaded.Profile, cancellationToken))
            {
                UserAccountProfile downgraded = loaded.Profile with
                {
                    Plan = "free",
                    Status = "none",
                    LicenseKey = null,
                    ExpiresAtUtc = null,
                };
                return loaded with { Profile = downgraded };
            }

            return loaded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task SaveAsync(PersistedAuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(_authDirectory);

        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        byte[] envelope = new byte[NonceSize + TagSize + plain.Length];
        Span<byte> nonce = envelope.AsSpan(0, NonceSize);
        Span<byte> tag = envelope.AsSpan(NonceSize, TagSize);
        Span<byte> cipher = envelope.AsSpan(NonceSize + TagSize);

        try
        {
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_encryptionKey, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag);

            await _atomicWriter.WriteAtomicallyAsync(
                _sessionFilePath,
                (stream, ct) => stream.WriteAsync(envelope, ct).AsTask(),
                cancellationToken);

            if (!string.Equals(session.Profile.Plan, "free", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(session.Profile.UserId))
            {
                byte[] sigBytes = Encoding.UTF8.GetBytes(ComputeProfileSignature(session.Profile));
                await _atomicWriter.WriteAtomicallyAsync(
                    _signatureFilePath,
                    (stream, ct) => stream.WriteAsync(sigBytes, ct).AsTask(),
                    cancellationToken);
            }
            else if (File.Exists(_signatureFilePath))
            {
                File.Delete(_signatureFilePath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TryDeleteFile(_sessionFilePath);
        TryDeleteFile(_signatureFilePath);
        return Task.CompletedTask;
    }

    private async Task<bool> VerifyLicenseSignatureAsync(UserAccountProfile profile, CancellationToken ct)
    {
        if (!File.Exists(_signatureFilePath))
        {
            return false;
        }

        try
        {
            string storedHex = (await File.ReadAllTextAsync(_signatureFilePath, ct)).Trim();
            string expectedHex = ComputeProfileSignature(profile);
            byte[] storedBytes = Encoding.UTF8.GetBytes(storedHex);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedHex);
            return storedBytes.Length == expectedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(storedBytes, expectedBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private string ComputeProfileSignature(UserAccountProfile profile)
    {
        long expUnix = profile.ExpiresAtUtc?.ToUnixTimeSeconds() ?? 0;
        string canonical = $"{profile.UserId}|{profile.Plan}|{profile.Status}|{expUnix}";
        byte[] mac = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(mac);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
