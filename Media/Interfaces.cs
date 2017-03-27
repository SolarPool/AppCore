using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;

namespace Ciphernote.Media
{
    public interface IPcmAudioDecoder
    {
        /// <summary>
        /// PCM 16-Bit Signed, 16Khz, Mono
        /// </summary>
        Stream GetDecoderStream(Stream source);
    }

    public interface IAudioEncoder
    {
        IObservable<Unit> EncodeAsync(IObservable<short[]> source, Stream destination);
    }

    public interface IPcmAudioCaptureSource : IDisposable
    {
        /// <summary>
        /// PCM 16-Bit Signed, 16Khz, Mono
        /// </summary>
        IObservable<short[]> Samples { get; }

        void Start();
        void Stop();
    }
}
