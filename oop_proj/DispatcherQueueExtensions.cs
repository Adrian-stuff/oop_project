using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace oop_proj
{
    public static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this DispatcherQueue queue, Func<Task> func)
        {
            var tcs = new TaskCompletionSource<object?>();
            queue.TryEnqueue(async () =>
            {
                try
                {
                    await func();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }
    }

}
