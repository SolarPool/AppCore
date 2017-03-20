using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Ciphernote.Util
{
    public static class TaskUtil
    {
        public static async void RunWithCompletionSource<T>(TaskCompletionSource<T> tcs, Func<Task<T>> action)
        {
            try
            {
                var result = await action();
                tcs.SetResult(result);
            }

            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }

        public static async void RunWithCompletionSource(TaskCompletionSource<Unit> tcs, Func<Task> action)
        {
            try
            {
                await action();
                tcs.SetResult(Unit.Default);
            }

            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
    }
}
