namespace Ciphernote.Resources
{
    public interface ICoreStrings
    {
        string OkButtonCaption { get; }
        string CancelButtonCaption { get; }
        string CloseButtonCaption { get; }
        string YesButtonCaption { get; }
        string NoButtonCaption { get; }
        string SaveButtonCaption { get; }
        string RecordButtonCaption { get; }
        string PauseButtonCaption { get; }
        string ResumeButtonCaption { get; }

        string UntitledNote { get; }
        string DeletedNotesSearchProcessingInstructionName { get; }

        string ConfirmationPromptTitle { get; }
        string ConfirmDeleteNotesMessage { get; }
        string ConfirmDeleteNotesTitle { get; }

        string ValidationPasswordConfirmMismatch { get; }
        string LoginFailed { get; }
        string NetworkError { get; }
        string RegisterEmailTaken { get; }

        string PasswordStrengthVeryStrong { get; }
        string PasswordStrengthStrong { get; }
        string PasswordStrengthSufficient { get; }
        string PasswordStrengthWeak { get; }
        string PasswordStrengthVeryWeak { get; }
        string InvalidAccountKeyFormat { get; }

        string WelcomeNoteTitle { get; }

        string ImportStep_Initial { get; }
        string ImportStep_Help { get; }
        string ImportStep_SelectFile { get; }
        string ConfirmAbortImport { get; }
        string NoteImportSuccessFormat { get; }

        string NoNetworkNotification { get; }

        string AudioProblem { get; }
        string NoAudioDeviceForRecording { get; }
        string NoAudioDeviceForPlayback { get; }
        string MediaNotFound { get; }

        string ApiErrorSubscriptionExpired { get; }
        string ApiErrorEmailConfirmationRequired { get; }

        string AccountStatusNone { get; }
        string AccountStatusTrial { get; }
        string AccountStatusSubscribed { get; }
    }
}
