using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Ciphernote.Resources;
using Ciphernote.Services.Dto;
using Ciphernote.UI;
using CodeContracts;

namespace Ciphernote.Extensions
{
    public static class NotificationExtensions
    {
        public static void NotifyNoCloud(this IPromptFactory promptFactory, ICoreStrings coreStrings)
        {
            Contract.RequiresNonNull(coreStrings, nameof(coreStrings));

            promptFactory.ShowToast(ToastType.Info, string.Empty, coreStrings.NoNetworkNotification, 
                null, TimeSpan.FromSeconds(10), PromptIcon.NoCloud);
        }

        public static void NotifyFromReponse(this IPromptFactory promptFactory, ResponseBase response, ICoreStrings coreStrings, TimeSpan? timeout = null)
        {
            Contract.RequiresNonNull(coreStrings, nameof(coreStrings));
            Contract.RequiresNonNull(response, nameof(response));

            if (!string.IsNullOrEmpty(response.ResponseMessageId))
            {
                ToastType toastType = ToastType.Info;

                switch (response.ResponseMessageType)
                {
                    case ResponseMessageType.Info:
                        toastType = ToastType.Info;
                        break;
                    case ResponseMessageType.Warning:
                        toastType = ToastType.Warning;
                        break;
                    case ResponseMessageType.Error:
                        toastType = ToastType.Error;
                        break;
                }

                var property = coreStrings.GetType().GetProperty(response.ResponseMessageId);
                var message = (string) property?.GetValue(coreStrings);

                if(!string.IsNullOrEmpty(message))
                    promptFactory.ShowToast(toastType, string.Empty, message, null, timeout, PromptIcon.Cloud);
            }
        }
    }
}
