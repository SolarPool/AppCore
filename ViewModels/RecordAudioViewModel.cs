using System.Diagnostics;
using Autofac;
using System.Reactive;
using Ciphernote.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Ciphernote.Resources;
using ReactiveUI;

namespace Ciphernote.ViewModels
{
    public class RecordAudioViewModel : ViewModelBase
    {
        public RecordAudioViewModel(IComponentContext ctx, ICoreStrings coreStrings) : base(ctx)
        {
            source = ctx.Resolve<IPcmAudioCaptureSource>();
            speexEncoder = ctx.ResolveKeyed<IAudioEncoder>(MimeTypeProvider.MimeTypeAudioSpeex);

            Recordings = recordingsSubject.AsObservable();

            disposables.Add(source);

            recordButtonCaption = this.WhenAny(x => x.IsRecording,
                    x => x.Value ? coreStrings.SaveButtonCaption : coreStrings.RecordButtonCaption)
                .ToProperty(this, x => x.RecordButtonCaption);

            pauseButtonCaption = this.WhenAny(x => x.IsRecordingPaused,
                    x => x.Value ? coreStrings.ResumeButtonCaption : coreStrings.PauseButtonCaption)
                .ToProperty(this, x => x.PauseButtonCaption);

            showPauseButton = this.WhenAny(x => x.IsRecording, x => x.Value)
                .ToProperty(this, x => x.ShowPauseButton);

            showCancelButton = this.WhenAny(x => x.IsRecording, x => x.Value)
                .ToProperty(this, x => x.ShowCancelButton);

            RecordCommand = ReactiveCommand.CreateFromTask(ExecuteRecord, 
                this.WhenAny(x=> x.CanRecord, x=> x.Value));

            PauseCommand = ReactiveCommand.CreateFromTask(ExecutePause,
                this.WhenAny(x => x.IsRecording, x => x.Value));

            CancelCommand = ReactiveCommand.CreateFromTask(ExecuteCancel,
                this.WhenAny(x => x.IsRecording, x => x.Value));
        }

        public override void Dispose()
        {
            captureSourceSub?.Dispose();
            captureSourceSub = null;

            lock (sampleLock)
            {
                samplesSubject?.OnCompleted();
                samplesSubject = null;
            }

            CleanupSpeexStream();

            base.Dispose();
        }

        private readonly IPcmAudioCaptureSource source;
        private readonly IAudioEncoder speexEncoder;
        private readonly object sampleLock = new object();
        private Subject<short[]> samplesSubject;
        private FileStream speexStream;
        private readonly Subject<FileStream> recordingsSubject = new Subject<FileStream>();

        private IDisposable captureSourceSub;
        private DateTime recordingStarted;
        private IDisposable updateRecordingTimeSub;
        private readonly List<int> recordedSegments = new List<int>();

        private readonly ObservableAsPropertyHelper<string> recordButtonCaption;
        private readonly ObservableAsPropertyHelper<string> pauseButtonCaption;
        private readonly ObservableAsPropertyHelper<bool> showPauseButton;
        private readonly ObservableAsPropertyHelper<bool> showCancelButton;

        public string RecordButtonCaption => recordButtonCaption.Value;
        public string PauseButtonCaption => pauseButtonCaption.Value;
        public bool ShowPauseButton => showPauseButton.Value;
        public bool ShowCancelButton => showCancelButton.Value;

        public ReactiveCommand RecordCommand { get; private set; }
        public ReactiveCommand PauseCommand { get; private set; }
        public ReactiveCommand CancelCommand { get; private set; }

        public IObservable<FileStream> Recordings { get; private set; }

        private bool canRecord;

        public bool CanRecord
        {
            get { return canRecord; }
            set { this.RaiseAndSetIfChanged(ref canRecord, value); }
        }

        private double recordingVolume;

        public double RecordingVolume
        {
            get { return recordingVolume; }
            set { this.RaiseAndSetIfChanged(ref recordingVolume, value); }
        }

        private bool isRecording;

        public bool IsRecording
        {
            get { return isRecording; }
            set { this.RaiseAndSetIfChanged(ref isRecording, value); }
        }

        private bool isRecordingPaused;

        public bool IsRecordingPaused
        {
            get { return isRecordingPaused; }
            set { this.RaiseAndSetIfChanged(ref isRecordingPaused, value); }
        }

        public string RecordingTime => 
            IsRecording ? (
                IsRecordingPaused ?
                     TimeSpan.FromMilliseconds(recordedSegments.Sum()).ToString(@"mm\:ss") :
                    ((DateTime.UtcNow - recordingStarted) + TimeSpan.FromMilliseconds(recordedSegments.Sum())).ToString(@"mm\:ss")
                ) : 
            "00:00";

        public void Init()
        {
            captureSourceSub = source.Samples
                .Subscribe(OnDataAvailable);

            try
            {
                source.Start();
                CanRecord = true;
            }

            catch (Exception)
            {
                CanRecord = false;
            }
        }

        private Task ExecuteRecord()
        {
            if (!IsRecording)
            {
                speexStream = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite);
                samplesSubject = new Subject<short[]>();
                speexEncoder.EncodeAsync(samplesSubject.AsObservable(), speexStream).Subscribe();

                recordingStarted = DateTime.UtcNow;

                updateRecordingTimeSub = Observable.Interval(TimeSpan.FromMilliseconds(100))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => this.RaisePropertyChanged(nameof(RecordingTime)));

                IsRecording = true;
            }

            else
            {
                IsRecording = false;

                // save recording
                lock (sampleLock)
                {
                    samplesSubject.OnCompleted();
                    samplesSubject = null;
                }

                speexStream.Seek(0, SeekOrigin.Begin);
                recordingsSubject.OnNext(speexStream);
                speexStream = null;

                // cleanup
                updateRecordingTimeSub?.Dispose();
                updateRecordingTimeSub = null;
                recordedSegments.Clear();

                // ReSharper disable once ExplicitCallerInfoArgument
                this.RaisePropertyChanged(nameof(RecordingTime));
            }

            return Task.FromResult(true);
        }

        private Task ExecutePause()
        {
            if (!IsRecordingPaused)
            {
                recordedSegments.Add((int) (DateTime.UtcNow - recordingStarted).TotalMilliseconds);
                IsRecordingPaused = true;
            }

            else
            {
                recordingStarted = DateTime.UtcNow;
                IsRecordingPaused = false;
            }

            return Task.FromResult(true);
        }

        private Task ExecuteCancel()
        {
            IsRecordingPaused = false;
            IsRecording = false;

            // stop recording
            lock (sampleLock)
            {
                samplesSubject.OnCompleted();
                samplesSubject = null;
            }

            CleanupSpeexStream();

            // cleanup
            updateRecordingTimeSub?.Dispose();
            updateRecordingTimeSub = null;
            recordedSegments.Clear();

            // ReSharper disable once ExplicitCallerInfoArgument
            this.RaisePropertyChanged(nameof(RecordingTime));

            return Task.FromResult(true);
        }

        void CleanupSpeexStream()
        {
            if (speexStream != null)
            {
                try
                {
                    File.Delete(speexStream.Name);
                }

                catch (IOException)
                {
                    // ignored
                }

                speexStream.Dispose();
                speexStream = null;
            }
        }

        private void OnDataAvailable(short[] samples)
        {
            lock (sampleLock)
            {
                if (IsRecording && !IsRecordingPaused)
                {
                    samplesSubject.OnNext(samples);
                }
            }

            // update volumne
            var max = samples.Max();
            var maxVolume = (double)max / short.MaxValue;
            RxApp.MainThreadScheduler.Schedule(() => RecordingVolume = maxVolume);
        }
    }
}
