using System.Buffers;
using System.Numerics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Agents.AI.Extensions.Helpers.Audio;

/// <summary>
/// Resamples PCM16 mono audio between sample rates using the WDL resampler from NAudio.
/// <para>
/// This class maintains a reusable NAudio pipeline internally and rents temporary buffers from
/// <see cref="ArrayPool{T}"/> to minimize allocations on the hot path. Instances are thread-safe;
/// concurrent calls to <see cref="Resample"/> are serialized by an internal lock.
/// </para>
/// <para>
/// Create instances via the factory methods
/// <see cref="UpsamplePcm16Mono16kTo24k"/> and <see cref="DownsamplePcm16Mono24kTo16k"/>.
/// </para>
/// </summary>
internal sealed class AudioResampler : IDisposable
{
    private const int InitialTempOutputCapacity = 4096;

    private readonly int _inputSampleRate;
    private readonly int _outputSampleRate;

    // NAudio pipeline: MemoryStream → RawSourceWaveStream → float samples → WDL resampler → PCM16 output
    private readonly MemoryStream _inputMemoryStream;          
    private readonly RawSourceWaveStream _rawInputStream;     
    private readonly ISampleProvider _floatSource;            
    private readonly WdlResamplingSampleProvider _resampler;  
    private readonly SampleToWaveProvider16 _pcm16Provider;

    /// <summary>Fixed-size scratch buffer used to pull chunks from the NAudio pipeline.</summary>
    private readonly byte[] _readBuffer;                    
    private readonly Lock _lock = new();

    /// <summary>Pooled buffer that accumulates resampled output before the final copy.</summary>
    private byte[] _tempOutputBuffer;                        
    private bool _disposed;

    private AudioResampler(int inputSampleRate, int outputSampleRate)
    {
        _inputSampleRate = inputSampleRate;
        _outputSampleRate = outputSampleRate;
        var inputFormat = new WaveFormat(_inputSampleRate, 16, 1);

        _inputMemoryStream = new MemoryStream(capacity: 8 * 1024);
        _rawInputStream = new RawSourceWaveStream(_inputMemoryStream, inputFormat);
        _floatSource = _rawInputStream.ToSampleProvider();
        _resampler = new WdlResamplingSampleProvider(_floatSource, _outputSampleRate);
        _pcm16Provider = new SampleToWaveProvider16(_resampler);

        _readBuffer = new byte[InitialTempOutputCapacity];
        _tempOutputBuffer = ArrayPool<byte>.Shared.Rent(InitialTempOutputCapacity);
    }

    /// <summary>
    /// Creates a resampler that converts 16 kHz PCM16 mono audio to 24 kHz.
    /// </summary>
    public static AudioResampler UpsamplePcm16Mono16kTo24k() => new(16000, 24000);

    /// <summary>
    /// Creates a resampler that converts 24 kHz PCM16 mono audio to 16 kHz.
    /// </summary>
    public static AudioResampler DownsamplePcm16Mono24kTo16k() => new(24000, 16000);

    /// <summary>
    /// Resamples a PCM16 mono audio buffer from the configured input sample rate to the output sample rate.
    /// </summary>
    /// <param name="input">
    /// Raw PCM16 audio bytes. Must have an even length (each sample is 2 bytes).
    /// </param>
    /// <returns>A new byte array containing the resampled PCM16 audio.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="input"/> has an odd length.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the resampler has already been disposed.</exception>
    public byte[] Resample(ReadOnlySpan<byte> input)
    {
        if (input.Length % 2 != 0)
        {
            throw new ArgumentException("Input PCM16 buffer length must be even.", nameof(input));
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AudioResampler));

            // Reset the input stream and feed new data into the NAudio pipeline.
            _inputMemoryStream.Position = 0;
            _inputMemoryStream.SetLength(0);
            _inputMemoryStream.Write(input);
            _inputMemoryStream.Position = 0;
            _rawInputStream.Position = 0;

            // Pre-size the output buffer based on the sample-rate ratio.
            var inSamples = input.Length / 2;
            var estOutSamples = (int)Math.Ceiling(inSamples * (_outputSampleRate / (double)_inputSampleRate));
            var estOutBytes = estOutSamples * 2;
            EnsureBufferCapacity(estOutBytes);

            // Pull resampled PCM16 chunks until the pipeline is drained.
            var totalWritten = 0;
            int read;
            while ((read = _pcm16Provider.Read(_readBuffer, 0, _readBuffer.Length)) > 0)
            {
                if (totalWritten + read > _tempOutputBuffer.Length)
                {
                    EnsureBufferCapacity(totalWritten + read);
                }

                _readBuffer.AsSpan(0, read).CopyTo(_tempOutputBuffer.AsSpan(totalWritten));
                totalWritten += read;
            }

            // Return an exact-sized result
            var result = new byte[totalWritten];
            _tempOutputBuffer.AsSpan(0, totalWritten).CopyTo(result);

            return result;
        }
    }

    /// <summary>
    /// Grows <see cref="_tempOutputBuffer"/> to at least <paramref name="requiredBytes"/>,
    /// rounding up to the next power of two. The previous buffer is returned to the pool.
    /// </summary>
    private void EnsureBufferCapacity(int requiredBytes)
    {
        if (_tempOutputBuffer.Length >= requiredBytes)
        {
            return;
        }

        var newSize = (int)BitOperations.RoundUpToPowerOf2((uint)requiredBytes);
        ArrayPool<byte>.Shared.Return(_tempOutputBuffer);

        _tempOutputBuffer = ArrayPool<byte>.Shared.Rent(newSize);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rawInputStream.Dispose();
        _inputMemoryStream.Dispose();
        ArrayPool<byte>.Shared.Return(_tempOutputBuffer);
    }
}
