// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadKeyboardMappingTests
{
    [Theory]
    [InlineData(0x54, OrbisPadButton.TouchPad)]
    [InlineData(0x5A, OrbisPadButton.Cross)]
    [InlineData(0x09, OrbisPadButton.Options)]
    public void KeyMapsToItsPadButton(int virtualKey, uint expected)
    {
        Assert.Equal(expected, PadExports.ReadKeyboardButtons(new KeyboardInput(virtualKey)));
    }

    [Fact]
    public void NoKeyPressedMapsToNoButton()
    {
        Assert.Equal(0u, PadExports.ReadKeyboardButtons(new KeyboardInput()));
    }

    private sealed class KeyboardInput(params int[] pressed) : IHostInput
    {
        public void EnsureStarted() { }
        public int GetGamepadStates(Span<HostGamepadState> destination) => 0;
        public string? DescribeConnectedGamepad() => null;
        public void SetRumble(byte largeMotor, byte smallMotor) { }
        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) { }
        public void SetAdaptiveTriggerEffect(HostAdaptiveTriggerEffect? leftTrigger, HostAdaptiveTriggerEffect? rightTrigger) { }
        public void SetLightbar(byte red, byte green, byte blue) { }
        public void ResetLightbar() { }
        public bool IsHostWindowFocused() => true;
        public bool IsKeyDown(int virtualKey) => pressed.Contains(virtualKey);
    }
}
