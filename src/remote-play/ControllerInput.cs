using System.IO;
using Windows.Gaming.Input;

namespace Ovrix.Engine;

public readonly record struct PadSnapshot(string Name, uint Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY)
{
    public void Write(BinaryWriter writer)
    {
        writer.Write(Buttons);
        writer.Write(LeftTrigger);
        writer.Write(RightTrigger);
        writer.Write(LeftX);
        writer.Write(LeftY);
        writer.Write(RightX);
        writer.Write(RightY);
        for (int i = 0; i < 2; i++)
        {
            writer.Write((sbyte)-1);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
        }
        writer.Write(new byte[33]); // No motion sample.
    }
}

public static class ControllerInput
{
    private static readonly (GamepadButtons Source, int Target)[] Mapping =
    [
        (GamepadButtons.DPadUp, 0), (GamepadButtons.DPadDown, 1), (GamepadButtons.DPadLeft, 2),
        (GamepadButtons.DPadRight, 3), (GamepadButtons.A, 4), (GamepadButtons.B, 5),
        (GamepadButtons.X, 6), (GamepadButtons.Y, 7), (GamepadButtons.LeftShoulder, 8),
        (GamepadButtons.RightShoulder, 9), (GamepadButtons.LeftThumbstick, 10),
        (GamepadButtons.RightThumbstick, 11), (GamepadButtons.View, 12), (GamepadButtons.Menu, 13)
    ];

    private static short Axis(double value) => (short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue);
    private static byte Trigger(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static double Stick(double raw) => Math.Abs(raw - .5) < .04 ? 0 : raw * 2 - 1;

    public static PadSnapshot Read()
    {
        try
        {
            // USB and Bluetooth DualSense/DualSense Edge report through RawGameController.
            var sony = RawGameController.RawGameControllers.FirstOrDefault(p =>
                p.HardwareVendorId == 0x054c && p.HardwareProductId is 0x0ce6 or 0x0df2 &&
                p.AxisCount >= 6 && p.ButtonCount >= 12);
            if (sony is not null) return ReadSony(sony);
            var pad = Gamepad.Gamepads.FirstOrDefault();
            if (pad is null) return new("", 0, 0, 0, 0, 0, 0, 0);
            var state = pad.GetCurrentReading();
            uint keys = 0;
            foreach (var (source, target) in Mapping)
                if ((state.Buttons & source) != 0) keys |= 1u << target;
            return new("Windows Gamepad", keys, Trigger(state.LeftTrigger), Trigger(state.RightTrigger),
                Axis(state.LeftThumbstickX), Axis(-state.LeftThumbstickY),
                Axis(state.RightThumbstickX), Axis(-state.RightThumbstickY));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return new("", 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private static PadSnapshot ReadSony(RawGameController pad)
    {
        var buttons = new bool[pad.ButtonCount];
        var axes = new double[pad.AxisCount];
        var hat = new GameControllerSwitchPosition[pad.SwitchCount];
        pad.GetCurrentReading(buttons, hat, axes);
        uint bits = 0;
        int[] indices = [6, 4, 5, 7, 8, 9, -1, -1, 12, 13, 10, 11, 15, 14];
        for (int i = 0; i < Math.Min(buttons.Length, indices.Length); i++)
            if (buttons[i] && indices[i] >= 0) bits |= 1u << indices[i];
        if (hat.Length > 0)
        {
            var d = hat[0];
            if (d is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.UpRight) bits |= 1;
            if (d is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownLeft or GameControllerSwitchPosition.DownRight) bits |= 2;
            if (d is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft) bits |= 4;
            if (d is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight) bits |= 8;
        }
        return new(pad.DisplayName, bits, Trigger(axes[3]), Trigger(axes[4]),
            Axis(Stick(axes[0])), Axis(Stick(axes[1])), Axis(Stick(axes[2])), Axis(Stick(axes[5])));
    }
}
