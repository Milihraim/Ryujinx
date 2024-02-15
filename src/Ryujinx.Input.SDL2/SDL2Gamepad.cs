using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using Silk.NET.Input;
using Silk.NET.SDL;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Input.SDL2
{
    class SDL2Gamepad : IGamepad
    {
        private bool HasConfiguration => _configuration != null;

        private Sdl _sdl = Sdl.GetApi();

        private record struct ButtonMappingEntry(GamepadButtonInputId To, GamepadButtonInputId From);

        private StandardControllerInputConfig _configuration;

        private static readonly GameControllerButton[] _buttonsDriverMapping = new GameControllerButton[(int)GamepadButtonInputId.Count]
        {
            // Unbound, ignored.
            GameControllerButton.Invalid,

            GameControllerButton.A,
            GameControllerButton.B,
            GameControllerButton.X,
            GameControllerButton.Y,
            GameControllerButton.Leftstick,
            GameControllerButton.Rightstick,
            GameControllerButton.Leftshoulder,
            GameControllerButton.Rightshoulder,

            // NOTE: The left and right trigger are axis, we handle those differently
            GameControllerButton.Invalid,
            GameControllerButton.Invalid,

            GameControllerButton.DpadUp,
            GameControllerButton.DpadDown,
            GameControllerButton.DpadLeft,
            GameControllerButton.DpadRight,
            GameControllerButton.Back,
            GameControllerButton.Start,
            GameControllerButton.Guide,
            GameControllerButton.Misc1,
            GameControllerButton.Paddle1,
            GameControllerButton.Paddle2,
            GameControllerButton.Paddle3,
            GameControllerButton.Paddle4,
            GameControllerButton.Touchpad,

            // Virtual buttons are invalid, ignored.
            GameControllerButton.Invalid,
            GameControllerButton.Invalid,
            GameControllerButton.Invalid,
            GameControllerButton.Invalid,
        };

        private readonly object _userMappingLock = new();

        private readonly List<ButtonMappingEntry> _buttonsUserMapping;

        private readonly StickInputId[] _stickUserMapping = new StickInputId[(int)StickInputId.Count]
        {
            StickInputId.Unbound,
            StickInputId.Left,
            StickInputId.Right,
        };

        public GamepadFeaturesFlag Features { get; }

        private unsafe GameController* _gamepadHandle;

        private float _triggerThreshold;

        public unsafe SDL2Gamepad(GameController* gamepadHandle, string driverId)
        {
            _gamepadHandle = gamepadHandle;
            _buttonsUserMapping = new List<ButtonMappingEntry>(20);

            Name = _sdl.GameControllerNameS(_gamepadHandle);
            Id = driverId;
            Features = GetFeaturesFlag();
            _triggerThreshold = 0.0f;

            // Enable motion tracking
            if (Features.HasFlag(GamepadFeaturesFlag.Motion))
            {
                if (_sdl.GameControllerSetSensorEnabled(_gamepadHandle, SensorType.Accel, SdlBool.True) != 0)
                {
                    Logger.Error?.Print(LogClass.Hid, $"Could not enable data reporting for SensorType {SensorType.Accel}.");
                }

                if (_sdl.GameControllerSetSensorEnabled(_gamepadHandle, SensorType.Gyro, SdlBool.True) != 0)
                {
                    Logger.Error?.Print(LogClass.Hid, $"Could not enable data reporting for SensorType {SensorType.Gyro}.");
                }
            }
        }

        private unsafe GamepadFeaturesFlag GetFeaturesFlag()
        {
            GamepadFeaturesFlag result = GamepadFeaturesFlag.None;

            if (_sdl.GameControllerHasSensor(_gamepadHandle, SensorType.Accel) == SdlBool.True &&
                _sdl.GameControllerHasSensor(_gamepadHandle, SensorType.Gyro) == SdlBool.True)
            {
                result |= GamepadFeaturesFlag.Motion;
            }

            int error = _sdl.GameControllerRumble(_gamepadHandle, 0, 0, 100);

            if (error == 0)
            {
                result |= GamepadFeaturesFlag.Rumble;
            }

            return result;
        }

        public string Id { get; }
        public string Name { get; }

        public unsafe bool IsConnected => _sdl.GameControllerGetAttached(_gamepadHandle) == SdlBool.True;

        protected virtual unsafe void Dispose(bool disposing)
        {
            if (disposing && _gamepadHandle != (void*)IntPtr.Zero)
            {
                _sdl.GameControllerClose(_gamepadHandle);

                _gamepadHandle = (GameController*)IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            _triggerThreshold = triggerThreshold;
        }

        public unsafe void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            if (Features.HasFlag(GamepadFeaturesFlag.Rumble))
            {
                ushort lowFrequencyRaw = (ushort)(lowFrequency * ushort.MaxValue);
                ushort highFrequencyRaw = (ushort)(highFrequency * ushort.MaxValue);

                if (durationMs == uint.MaxValue)
                {
                    if (_sdl.GameControllerRumble(_gamepadHandle, lowFrequencyRaw, highFrequencyRaw, Sdl.HapticInfinity) != 0)
                    {
                        Logger.Error?.Print(LogClass.Hid, "Rumble is not supported on this game controller.");
                    }
                }
                else if (durationMs > Sdl.HapticInfinity)
                {
                    Logger.Error?.Print(LogClass.Hid, $"Unsupported rumble duration {durationMs}");
                }
                else
                {
                    if (_sdl.GameControllerRumble(_gamepadHandle, lowFrequencyRaw, highFrequencyRaw, durationMs) != 0)
                    {
                        Logger.Error?.Print(LogClass.Hid, "Rumble is not supported on this game controller.");
                    }
                }
            }
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            SensorType sensorType = SensorType.Invalid;

            if (inputId == MotionInputId.Accelerometer)
            {
                sensorType = SensorType.Accel;
            }
            else if (inputId == MotionInputId.Gyroscope)
            {
                sensorType = SensorType.Gyro;
            }

            if (Features.HasFlag(GamepadFeaturesFlag.Motion) && sensorType != SensorType.Invalid)
            {
                const int ElementCount = 3;

                unsafe
                {
                    float* values = stackalloc float[ElementCount];

                    int result = _sdl.GameControllerGetSensorData(_gamepadHandle, sensorType, values, ElementCount);

                    if (result == 0)
                    {
                        Vector3 value = new(values[0], values[1], values[2]);

                        if (inputId == MotionInputId.Gyroscope)
                        {
                            return RadToDegree(value);
                        }

                        if (inputId == MotionInputId.Accelerometer)
                        {
                            return GsToMs2(value);
                        }

                        return value;
                    }
                }
            }

            return Vector3.Zero;
        }

        private static Vector3 RadToDegree(Vector3 rad)
        {
            return rad * (180 / MathF.PI);
        }

        private static Vector3 GsToMs2(Vector3 gs)
        {
            
            return gs / 9.80665F;
        }

        public void SetConfiguration(InputConfig configuration)
        {
            lock (_userMappingLock)
            {
                _configuration = (StandardControllerInputConfig)configuration;

                _buttonsUserMapping.Clear();

                // First update sticks
                _stickUserMapping[(int)StickInputId.Left] = (StickInputId)_configuration.LeftJoyconStick.Joystick;
                _stickUserMapping[(int)StickInputId.Right] = (StickInputId)_configuration.RightJoyconStick.Joystick;

                // Then left joycon
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftStick, (GamepadButtonInputId)_configuration.LeftJoyconStick.StickButton));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadUp, (GamepadButtonInputId)_configuration.LeftJoycon.DpadUp));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadDown, (GamepadButtonInputId)_configuration.LeftJoycon.DpadDown));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadLeft, (GamepadButtonInputId)_configuration.LeftJoycon.DpadLeft));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadRight, (GamepadButtonInputId)_configuration.LeftJoycon.DpadRight));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Minus, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonMinus));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftShoulder, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonL));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftTrigger, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonZl));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger0, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonSr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger0, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonSl));

                // Finally right joycon
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightStick, (GamepadButtonInputId)_configuration.RightJoyconStick.StickButton));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.A, (GamepadButtonInputId)_configuration.RightJoycon.ButtonA));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.B, (GamepadButtonInputId)_configuration.RightJoycon.ButtonB));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.X, (GamepadButtonInputId)_configuration.RightJoycon.ButtonX));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Y, (GamepadButtonInputId)_configuration.RightJoycon.ButtonY));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Plus, (GamepadButtonInputId)_configuration.RightJoycon.ButtonPlus));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightShoulder, (GamepadButtonInputId)_configuration.RightJoycon.ButtonR));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightTrigger, (GamepadButtonInputId)_configuration.RightJoycon.ButtonZr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger1, (GamepadButtonInputId)_configuration.RightJoycon.ButtonSr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger1, (GamepadButtonInputId)_configuration.RightJoycon.ButtonSl));

                SetTriggerThreshold(_configuration.TriggerThreshold);
            }
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            GamepadStateSnapshot rawState = GetStateSnapshot();
            GamepadStateSnapshot result = default;

            lock (_userMappingLock)
            {
                if (_buttonsUserMapping.Count == 0)
                {
                    return rawState;
                }

                foreach (ButtonMappingEntry entry in _buttonsUserMapping)
                {
                    if (entry.From == GamepadButtonInputId.Unbound || entry.To == GamepadButtonInputId.Unbound)
                    {
                        continue;
                    }

                    // Do not touch state of button already pressed
                    if (!result.IsPressed(entry.To))
                    {
                        result.SetPressed(entry.To, rawState.IsPressed(entry.From));
                    }
                }

                (float leftStickX, float leftStickY) = rawState.GetStick(_stickUserMapping[(int)StickInputId.Left]);
                (float rightStickX, float rightStickY) = rawState.GetStick(_stickUserMapping[(int)StickInputId.Right]);

                result.SetStick(StickInputId.Left, leftStickX, leftStickY);
                result.SetStick(StickInputId.Right, rightStickX, rightStickY);
            }

            return result;
        }

        private static float ConvertRawStickValue(short value)
        {
            const float ConvertRate = 1.0f / (short.MaxValue + 0.5f);

            return value * ConvertRate;
        }

        public unsafe (float, float) GetStick(StickInputId inputId)
        {
            if (inputId == StickInputId.Unbound)
            {
                return (0.0f, 0.0f);
            }

            short stickX;
            short stickY;

            if (inputId == StickInputId.Left)
            {
                stickX = _sdl.GameControllerGetAxis(_gamepadHandle, GameControllerAxis.Leftx);
                stickY = _sdl.GameControllerGetAxis(_gamepadHandle, GameControllerAxis.Lefty);
            }
            else if (inputId == StickInputId.Right)
            {
                stickX = _sdl.GameControllerGetAxis(_gamepadHandle, GameControllerAxis.Rightx);
                stickY = _sdl.GameControllerGetAxis(_gamepadHandle, GameControllerAxis.Righty);
            }
            else
            {
                throw new NotSupportedException($"Unsupported stick {inputId}");
            }

            float resultX = ConvertRawStickValue(stickX);
            float resultY = -ConvertRawStickValue(stickY);

            if (HasConfiguration)
            {
                if ((inputId == StickInputId.Left && _configuration.LeftJoyconStick.InvertStickX) ||
                    (inputId == StickInputId.Right && _configuration.RightJoyconStick.InvertStickX))
                {
                    resultX = -resultX;
                }

                if ((inputId == StickInputId.Left && _configuration.LeftJoyconStick.InvertStickY) ||
                    (inputId == StickInputId.Right && _configuration.RightJoyconStick.InvertStickY))
                {
                    resultY = -resultY;
                }

                if ((inputId == StickInputId.Left && _configuration.LeftJoyconStick.Rotate90CW) ||
                    (inputId == StickInputId.Right && _configuration.RightJoyconStick.Rotate90CW))
                {
                    float temp = resultX;
                    resultX = resultY;
                    resultY = -temp;
                }
            }

            return (resultX, resultY);
        }

        public unsafe bool IsPressed(GamepadButtonInputId inputId)
        {
            if (inputId == GamepadButtonInputId.LeftTrigger)
            {
                return ConvertRawStickValue(_sdl.GameControllerGetAxis(_gamepadHandle, GameControllerAxis.Triggerleft)) > _triggerThreshold;
            }

            if (inputId == GamepadButtonInputId.RightTrigger)
            {
                return ConvertRawStickValue(_sdl.GameControllerGetAxis(_gamepadHandle, GameControllerAxis.Triggerright)) > _triggerThreshold;
            }

            if (_buttonsDriverMapping[(int)inputId] == GameControllerButton.Invalid)
            {
                return false;
            }

            return _sdl.GameControllerGetButton(_gamepadHandle, _buttonsDriverMapping[(int)inputId]) == 1;
        }
    }
}
