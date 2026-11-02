// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_ASYNC_PRESENT: see AsyncPresentQueue.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private AsyncPresentQueue? _asyncPresent;

        // On the present thread: the swapchain, its semaphores and the queue as the render thread left them (it waits for
        // this present before it touches any of them again).
        private int PresentOnThread(AsyncPresentQueue.Job job)
        {
            _scheduler.WaitSubmitted(job.Tick);
            var swapchain = _swapchain;
            var imageIndex = job.ImageIndex;
            var renderFinished = _renderFinishedPerImage[imageIndex];
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            Result result;
            var missLock = Diagnostics.FrameMissProbe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            lock (_queueGate)
            {
                var missCall = missLock != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                result = _swapchainApi.QueuePresent(_queue, &presentInfo);
                if (missLock != 0)
                {
                    Diagnostics.FrameMissProbe.Add(Diagnostics.FrameMissProbe.Counter.PresentLock, missCall - missLock);
                    Diagnostics.FrameMissProbe.Add(Diagnostics.FrameMissProbe.Counter.PresentCall, System.Diagnostics.Stopwatch.GetTimestamp() - missCall);
                }
            }

            return (int)result;
        }

        // On the present thread, right after the call: the flip's outcome, as CompletePresentation reports it.
        private void CompletePresentOnThread(AsyncPresentQueue.Job job, int result)
        {
            var presented = (Result)result is Result.Success or Result.SuboptimalKhr;
            if (!presented && (Result)result != Result.ErrorOutOfDateKhr)
            {
                return; // the render thread raises it
            }

            if (presented)
            {
                VideoOutExports.ReportPresentedFrame();
            }

            if (job.FlipRequestId != 0)
            {
                if (presented)
                {
                    VideoOutExports.MarkFlipPresented(job.FlipRequestId);
                }
                else
                {
                    VideoOutExports.DiscardFlip(job.FlipRequestId);
                }

                _commandStream.RetryBlocked();
            }
        }

        // On the render thread: wait for the present in flight and handle its result (recreation, errors). While the
        // swapchain is being recreated anyway, only errors are raised.
        private void FinishAsyncPresent(bool recreating = false)
        {
            if (_asyncPresent is not { } queue)
            {
                return;
            }

            bool finished;
            int result;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueuePresent))
            {
                finished = queue.TryFinish(out _, out result);
            }

            queue.MaybeReport();
            if (!finished)
            {
                return;
            }

            var presentResult = (Result)result;
            if (recreating && presentResult is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
            {
                return;
            }

            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            if (presentResult == Result.SuboptimalKhr)
            {
                RecreateSwapchainResources("present suboptimal", presentResult);
            }
        }

        // At shutdown: the present in flight finishes; its result no longer matters.
        private void StopAsyncPresent()
        {
            if (_asyncPresent is not { } queue)
            {
                return;
            }

            try
            {
                queue.TryFinish(out _, out _);
            }
            catch (InvalidOperationException)
            {
            }

            queue.Dispose();
            _asyncPresent = null;
        }
    }
}
