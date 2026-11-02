// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;
using BindingLayout = SharpEmu.ShaderCompiler.Resources.BindingLayout;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PendingComputePollingTests
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Presenter = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;

    // Use the real host and provider without constructing a device. Only pending-task consumption runs:
    // the native pipeline cache, device and debug-name callback remain absent.
    private sealed class Fixture
    {
        public readonly IShaderPipelineHost Host = (IShaderPipelineHost)RuntimeHelpers.GetUninitializedObject(Presenter);
        public readonly IDictionary Pending;
        public readonly IDictionary Published;
        public readonly ShaderPipelineCache Provider;

        public Fixture()
        {
            Assert.True((bool)Presenter.GetField("_asyncComputePipelines", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
                "Run this regression process with SHARPEMU_ASYNC_COMPUTE_PIPELINES=1.");
            Pending = InitializeDictionary("_pendingComputePipelines");
            Published = InitializeDictionary("_pipelineEntries");
            InitializeDictionary("_linkedComputeCompiles");
            var guest = new PipelineTestGuest();
            Provider = new ShaderPipelineCache(guest.Context, Host, guest.Compiler, guest.Registry);
        }

        private IDictionary InitializeDictionary(string name)
        {
            var field = Presenter.GetField(name, Members)!;
            var dictionary = (IDictionary)Activator.CreateInstance(field.FieldType)!;
            field.SetValue(Host, dictionary);
            return dictionary;
        }

        public void Add(ulong id, Task<Pipeline> task)
        {
            var type = Pending.GetType().GenericTypeArguments[1];
            var entry = RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("Compile", Members)!.SetValue(entry, task);
            type.GetField("Layout", Members)!.SetValue(entry, new PipelineLayout(123));
            Pending.Add(id, entry);
        }

        public bool Poll(ulong id, uint callees, bool throughProvider, out PipelineHandle handle)
        {
            var stage = new ShaderProgramInfo { Hash = id, Bindings = new BindingLayout { CallTargetCount = callees } };
            var input = new ComputeInputInfo { Stage = new ShaderStageResources(stage, null!) };
            return throughProvider
                ? Provider.TryCreateComputePipeline(input, new ShaderProgram(id), out handle)
                : Host.TryCreateComputePipeline(new ComputePipelineDescription { Input = input, Program = new ShaderProgram(id), Stage = stage }, out handle);
        }
    }

    private static Task<T> OnThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static bool IsHeld(object gate)
    {
        if (!Monitor.TryEnter(gate)) return true;
        Monitor.Exit(gate);
        return false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingNonLinkedPollDoesNotBlockAnotherProgram(bool throughProvider)
    {
        var fixture = new Fixture();
        var compile = new TaskCompletionSource<Pipeline>();
        var unrelated = new TaskCompletionSource<Pipeline>();
        fixture.Add(1, compile.Task);
        fixture.Add(2, unrelated.Task);
        var first = OnThread(() => fixture.Poll(1, 0, throughProvider, out _));
        Task<bool>? second = null;
        bool firstReturned = false, secondReturned = false, gateHeld = false;
        var failure = new InvalidOperationException("CPU-only cleanup of the blocked baseline poll.");
        try
        {
            Assert.True(SpinWait.SpinUntil(() => first.IsCompleted || IsHeld(fixture.Pending), 3000));
            gateHeld = IsHeld(fixture.Pending);
            second = OnThread(() => fixture.Poll(2, 1, throughProvider, out _));
            secondReturned = await Task.WhenAny(second, Task.Delay(1000)) == second;
            firstReturned = first.IsCompleted;
        }
        finally
        {
            // Release the unmodified baseline from GetResult without reaching any native creation path.
            if (first.IsCompleted) compile.TrySetCanceled();
            else compile.TrySetException(failure);
            try { Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(3))); }
            catch (InvalidOperationException exception) { Assert.Same(failure, exception); }
            if (second is not null) Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(3)));
            unrelated.TrySetCanceled();
        }
        Assert.True(firstReturned && secondReturned,
            $"firstReturned={firstReturned}, secondReturned={secondReturned}, actualPendingMonitorHeld={gateHeld}");
        Assert.Equal(2, fixture.Pending.Count);
        Assert.Empty(fixture.Published);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task ConcurrentProviderPollsPublishCompletedTaskExactlyOnce(uint callees)
    {
        var fixture = new Fixture();
        fixture.Add(1, Task.FromResult(new Pipeline(456)));
        using var start = new ManualResetEventSlim();
        using var arrived = new CountdownEvent(2);
        Task<PipelineHandle> Poll() => OnThread(() =>
        {
            arrived.Signal();
            Assert.True(start.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(fixture.Poll(1, callees, true, out var handle));
            return handle;
        });
        var first = Poll();
        var second = Poll();
        try { Assert.True(SpinWait.SpinUntil(() => arrived.CurrentCount == 0, 3000)); }
        finally { start.Set(); }
        var handles = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(handles[0], handles[1]);
        Assert.NotEqual(default, handles[0]);
        Assert.Empty(fixture.Pending);
        Assert.Single(fixture.Published);
        Assert.Equal(1, fixture.Provider.ComputePipelineCount);
        var entry = fixture.Published.Values.Cast<object>().Single();
        Assert.Equal(new Pipeline(456), entry.GetType().GetField("Pipeline", Members)!.GetValue(entry));
        Assert.Equal(new PipelineLayout(123), entry.GetType().GetField("Layout", Members)!.GetValue(entry));
        Assert.Null(fixture.Provider.GetComputePipelineCompletion(new ShaderProgram(1)));
        Assert.True(fixture.Poll(1, callees, true, out var cached));
        Assert.Equal(handles[0], cached);
        Assert.Single(fixture.Published);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void FaultedTaskRetainsOwnershipAndPropagatesTheOriginalException(uint callees)
    {
        var fixture = new Fixture();
        var failure = new InvalidOperationException("Compiler failure.");
        var task = Task.FromException<Pipeline>(failure);
        fixture.Add(1, task);
        for (var poll = 0; poll < 2; poll++)
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => fixture.Poll(1, callees, true, out _)));
        Assert.Single(fixture.Pending);
        Assert.Empty(fixture.Published);
        Assert.Same(task, fixture.Provider.GetComputePipelineCompletion(new ShaderProgram(1)));
    }
}
