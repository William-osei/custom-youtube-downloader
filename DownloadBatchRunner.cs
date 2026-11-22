using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CustomYouTubeDownloader
{
    internal static class DownloadBatchRunner
    {
        // A video ID owns its output files until both stages finish, including across overlapping playlists.
        internal static async Task RunAsync(IList<string> videoKeys, int limit, Func<int, Task<bool>> transfer,
            Func<int, Task> postprocess, CancellationToken token)
        {
            if (limit < 1 || limit > 4)
                throw new ArgumentOutOfRangeException("limit");

            var byVideo = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
            var ready = new SortedSet<int>();
            for (int i = 0; i < videoKeys.Count; i++)
            {
                Queue<int> group;
                if (!byVideo.TryGetValue(videoKeys[i], out group))
                {
                    group = new Queue<int>();
                    byVideo.Add(videoKeys[i], group);
                    ready.Add(i);
                }
                else
                    group.Enqueue(i);
            }

            var pending = new Queue<int>();
            var active = new List<Task<StageResult>>();
            int downloading = 0;
            bool processing = false;
            Exception failure = null;
            while (true)
            {
                // Even on cancellation, let saved videos finish recording their recoverable state.
                if (!processing && pending.Count > 0)
                {
                    processing = true;
                    active.Add(RunStageAsync(pending.Dequeue(), true, transfer, postprocess));
                }
                while (!token.IsCancellationRequested && failure == null && ready.Count > 0 &&
                    downloading < limit && pending.Count + downloading < 2 * limit)
                {
                    int index = ready.Min;
                    ready.Remove(index);
                    downloading++;
                    active.Add(RunStageAsync(index, false, transfer, postprocess));
                }
                if (active.Count == 0)
                    break;

                var finished = await Task.WhenAny(active);
                active.Remove(finished);
                var result = await finished;
                if (result.Postprocessing)
                    processing = false;
                else
                    downloading--;
                if (result.Error != null && failure == null)
                    failure = result.Error;
                if (result.NeedsPostprocessing)
                    pending.Enqueue(result.Index);
                else
                {
                    var group = byVideo[videoKeys[result.Index]];
                    if (group.Count > 0)
                        ready.Add(group.Dequeue());
                }
            }
            token.ThrowIfCancellationRequested();
            if (failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class StageResult
        {
            public int Index;
            public bool Postprocessing;
            public bool NeedsPostprocessing;
            public Exception Error;
        }

        private static async Task<StageResult> RunStageAsync(int index, bool postprocessing,
            Func<int, Task<bool>> transfer, Func<int, Task> postprocess)
        {
            var result = new StageResult { Index = index, Postprocessing = postprocessing };
            try
            {
                if (postprocessing)
                    await postprocess(index);
                else
                    result.NeedsPostprocessing = await transfer(index);
            }
            catch (Exception ex) { result.Error = ex; }
            return result;
        }
    }
}
