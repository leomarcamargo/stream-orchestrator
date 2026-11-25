using System.Security.Cryptography;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Services.Interfaces;

namespace StreamOrchestrator.Web.Services;

/// <summary>
/// Consumes a locally generated media buffer chunk by chunk, hashing every chunk and
/// pacing the reads so a session lasts the requested duration. This is controlled local
/// media consumption; it does not talk to YouTube, Spotify or any other platform.
/// </summary>
public sealed class PlaybackService : IPlaybackService
{
    private const int MediaSize = 1024 * 1024;
    private const int ChunkSize = 16 * 1024;

    private readonly byte[] media = CreateMedia();
    private readonly byte[] expectedHash;

    public PlaybackService()
    {
        expectedHash = SHA256.HashData(media);
    }

    public async Task PlayAsync(
        PlaybackSession session,
        RunSettings settings,
        CancellationToken cancellationToken
    )
    {
        var chunkCount = MediaSize / ChunkSize;
        var chunkDelay = TimeSpan.FromSeconds((double)settings.DurationSeconds / chunkCount);
        var failAtChunk = Random.Shared.Next(100) < settings.FailureRatePercent
            ? Random.Shared.Next(1, chunkCount)
            : -1;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            if (chunk == failAtChunk)
            {
                throw new InvalidOperationException("Injected playback failure.");
            }

            hash.AppendData(media, chunk * ChunkSize, ChunkSize);
            session.Progress = (chunk + 1) * 100 / chunkCount;
            await Task.Delay(chunkDelay, cancellationToken);
        }

        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expectedHash))
        {
            throw new InvalidDataException("Consumed media does not match the source checksum.");
        }
    }

    private static byte[] CreateMedia()
    {
        var buffer = new byte[MediaSize];
        new Random(42).NextBytes(buffer);
        return buffer;
    }
}
