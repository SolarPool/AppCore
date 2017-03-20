using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Importers;
using Ciphernote.Importers.Dom;
using Ciphernote.Importers.Notes;
using Ciphernote.Logging;
using Ciphernote.Model;
using Ciphernote.Resources;
using Ciphernote.Services;
using Ciphernote.UI;
using CodeContracts;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class NoteImportViewModel : ViewModelBase
    {
        public NoteImportViewModel(IComponentContext ctx, 
            ICoreStrings coreStrings, 
            IAppCoreSettings appSettings, 
            CryptoService cryptoService,
            MainViewModel mainViewModel,
            IEnumerable<INoteImporter> noteImporters,
            IEnumerable<IDomImporter> domImporters,
            IPromptFactory promptFactory) : base(ctx)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;
            this.cryptoService = cryptoService;
            this.mainViewModel = mainViewModel;
            this.noteImporters = noteImporters;
            this.domImporters = domImporters;
            this.promptFactory = promptFactory;

            disposables.Add(cts);

            stepInfo = this.WhenAny(x => x.Step, x => {
                if (x.Value == StepType.Initial)
                    return coreStrings.ImportStep_Initial;
                else if (x.Value == StepType.Help)
                    return coreStrings.ImportStep_Help;
                else if (x.Value == StepType.SelectFile)
                    return coreStrings.ImportStep_SelectFile;

                return "";
                })
                .ToProperty(this, x => x.StepInfo);

            isFileSelected = this.WhenAny(x => x.SelectedFile, x => !string.IsNullOrEmpty(x.Value))
                .ToProperty(this, x => x.IsFileSelected);

            SetImporterCmd = ReactiveCommand.CreateFromTask<string>(ExecuteSetImporter);

            NextStepCommand = ReactiveCommand.CreateFromTask(ExecuteNextStep, 
                this.WhenAny(x=> x.Step, x=> x.IsFileSelected, (x, y) =>
                {
                    if (x.Value == StepType.SelectFile && !y.Value)
                        return false;

                    return true;
                }));

            PreviousStepCommand = ReactiveCommand.CreateFromTask(ExecutePreviousStep);
            SkipHelpStepsCommand = ReactiveCommand.CreateFromTask(ExecuteSkipHelpSteps);

            AbortImportCommand = ReactiveCommand.CreateFromTask(ExecuteAbortImport);
        }

        private readonly ICoreStrings coreStrings;
        private readonly IAppCoreSettings appSettings;
        private readonly IEnumerable<INoteImporter> noteImporters;
        private readonly IEnumerable<IDomImporter> domImporters;
        private readonly IPromptFactory promptFactory;
        private readonly CryptoService cryptoService;
        private readonly MainViewModel mainViewModel;
        private int totalNoteCount;
        private int processedNoteCount;
        private int importedNoteCount;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private INoteImporter noteImporter;
        private readonly ObservableAsPropertyHelper<string> stepInfo;
        private readonly ObservableAsPropertyHelper<bool> isFileSelected;
        
        public enum StepType
        {
            Initial = 1,
            Help,
            SelectFile,
            Import,
            Complete
        }

        public ReactiveCommand SetImporterCmd { get; private set; }
        public ReactiveCommand NextStepCommand { get; }
        public ReactiveCommand PreviousStepCommand { get; }
        public ReactiveCommand SkipHelpStepsCommand { get; }
        public ReactiveCommand AbortImportCommand { get; }

        private async Task ExecuteSetImporter(string param)
        {
            noteImporter = noteImporters.First(x => x.Title == param);
            await ExecuteNextStep();
        }

        private StepType step = StepType.Initial;

        public StepType Step
        {
            get { return step; }
            set { this.RaiseAndSetIfChanged(ref step, value); }
        }

        private int helpImageIndex;

        private string helpImage;

        public string HelpImage
        {
            get { return helpImage; }
            set { this.RaiseAndSetIfChanged(ref helpImage, value); }
        }

        public string StepInfo => stepInfo.Value;

        public string[] SupportedFileExtensions => noteImporter.SupportedFileExtensions
            .Select(x=> x.ToLower()).ToArray();

        private string selectedFile;

        public string SelectedFile
        {
            get { return selectedFile; }
            set { this.RaiseAndSetIfChanged(ref selectedFile, value); }
        }

        private Stream selectedStream;

        public Stream SelectedStream
        {
            get { return selectedStream; }
            set
            {
                this.RaiseAndSetIfChanged(ref selectedStream, value);

                if(value != null)
                    disposables.Add(value);
            }
        }

        public bool IsFileSelected => isFileSelected.Value;

        private string currentNoteTitle;

        public string CurrentNoteTitle
        {
            get { return currentNoteTitle; }
            set { this.RaiseAndSetIfChanged(ref currentNoteTitle, value); }
        }

        private double progress;

        public double Progress
        {
            get { return progress; }
            set { this.RaiseAndSetIfChanged(ref progress, value); }
        }

        private string importResult;

        public string ImportResult
        {
            get { return importResult; }
            set { this.RaiseAndSetIfChanged(ref importResult, value); }
        }

        private bool importSuccess;

        public bool ImportSuccess
        {
            get { return importSuccess; }
            set { this.RaiseAndSetIfChanged(ref importSuccess, value); }
        }

        private async Task ExecuteNextStep()
        {
            switch (Step)
            {
                case StepType.Help:
                    if (helpImageIndex < noteImporter.HelpImages.Length - 1)
                    {
                        helpImageIndex++;
                        HelpImage = noteImporter.HelpImages[helpImageIndex];
                    }

                    else
                        Step++;
                    break;

                default:
                    Step++;

                    if (Step == StepType.Help)
                    {
                        helpImageIndex = 0;
                        HelpImage = noteImporter.HelpImages[helpImageIndex];
                    }

                    else if (Step == StepType.Import)
                        await DoImport();
                    break;
            }
        }

        private Task ExecutePreviousStep()
        {
            switch (Step)
            {
                case StepType.Help:
                    if (helpImageIndex > 0)
                    {
                        helpImageIndex--;
                        HelpImage = noteImporter.HelpImages[helpImageIndex];
                    }

                    else
                        Step--;
                    break;

                default:
                    if (Step <= StepType.SelectFile)
                    {
                        SelectedFile = null;

                        if (SelectedStream != null)
                        {
                            disposables.Remove(SelectedStream);

                            SelectedStream.Dispose();
                            SelectedStream = null;
                        }
                    }

                    if (Step > StepType.Initial)
                    {
                        Step--;

                        if (Step == StepType.Help)
                        {
                            helpImageIndex = noteImporter.HelpImages.Length - 1;
                            HelpImage = noteImporter.HelpImages[helpImageIndex];
                        }

                    }
                    break;
            }

            return Task.FromResult(true);
        }

        private Task ExecuteSkipHelpSteps()
        {
            Step = StepType.SelectFile;

            return Task.FromResult(true);
        }

        private async Task ExecuteAbortImport()
        {
            if (!cts.IsCancellationRequested)
            {
                var result = await promptFactory.Confirm(PromptType.OkCancel, coreStrings.ConfirmationPromptTitle,
                    coreStrings.ConfirmAbortImport);

                if (result == PromptResult.Ok)
                    cts.Cancel();
            }
        }

        private async Task DoImport()
        {
            try
            {
                totalNoteCount = await noteImporter.GetImportStatsAsync(SelectedStream, cts.Token);

                disposables.Add(mainViewModel.SuppressNoteUpdatedNotifications());

                this.Log().Info(() => $"Importing {totalNoteCount} notes using {noteImporter.GetType().Name}");

                SelectedStream.Seek(0, SeekOrigin.Begin);

                var options = new NoteImportOptions
                {
                    DomImportOptions = new DomImportOptions
                    {
                        ImportExternalImages = appSettings.ImportExternalImages
                    }
                };

                var obs = noteImporter.ImportNotes(SelectedStream, options, cts.Token);

                await obs.SubscribeOn(TaskPoolScheduler.Default)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(note => Observable.FromAsync(async () =>
                    {
                        processedNoteCount++;

                        if (note != null)
                        {
                            CurrentNoteTitle = note.Title;

                            note.IsSyncPending = true;

                            await mainViewModel.SaveNoteAsync(note, null, false);

                            importedNoteCount++;
                        }

                        Progress = ((double)processedNoteCount / totalNoteCount);
                    }))
                    .Concat()
                    .ToTask();

                await CompleteImport();
            }

            catch (TaskCanceledException)
            {
                await CompleteImport();
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(DoImport), ex);

                ImportSuccess = false;
                ImportResult = ex.Message;
            }
        }

        private async Task CompleteImport()
        {
            ImportSuccess = true;
            ImportResult = string.Format(coreStrings.NoteImportSuccessFormat, importedNoteCount, totalNoteCount);

            await ExecuteNextStep();
        }
    }
}
