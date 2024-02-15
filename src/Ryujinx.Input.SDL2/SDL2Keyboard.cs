using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Silk.NET.SDL;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

using ConfigKey = Ryujinx.Common.Configuration.Hid.Key;

namespace Ryujinx.Input.SDL2
{
    class SDL2Keyboard : IKeyboard
    {
        private class ButtonMappingEntry
        {
            public readonly GamepadButtonInputId To;
            public readonly Key From;

            public ButtonMappingEntry(GamepadButtonInputId to, Key from)
            {
                To = to;
                From = from;
            }
        }

        private readonly object _userMappingLock = new();

#pragma warning disable IDE0052 // Remove unread private member
        private readonly SDL2KeyboardDriver _driver;
#pragma warning restore IDE0052
        private StandardKeyboardInputConfig _configuration;
        private readonly List<ButtonMappingEntry> _buttonsUserMapping;
        private static Sdl _sdl = Sdl.GetApi();


        private static readonly KeyCode[] _keysDriverMapping = new KeyCode[(int)Key.Count]
        {
            // INVALID
            KeyCode.K0,
            // Presented as modifiers, so invalid here.
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,

            KeyCode.KF1,
            KeyCode.KF2,
            KeyCode.KF3,
            KeyCode.KF4,
            KeyCode.KF5,
            KeyCode.KF6,
            KeyCode.KF7,
            KeyCode.KF8,
            KeyCode.KF9,
            KeyCode.KF10,
            KeyCode.KF11,
            KeyCode.KF12,
            KeyCode.KF13,
            KeyCode.KF14,
            KeyCode.KF15,
            KeyCode.KF16,
            KeyCode.KF17,
            KeyCode.KF18,
            KeyCode.KF19,
            KeyCode.KF20,
            KeyCode.KF21,
            KeyCode.KF22,
            KeyCode.KF23,
            KeyCode.KF24,

            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,
            KeyCode.K0,

            KeyCode.KUp,
            KeyCode.KDown,
            KeyCode.KLeft,
            KeyCode.KRight,
            KeyCode.KReturn,
            KeyCode.KEscape,
            KeyCode.KSpace,
            KeyCode.KTab,
            KeyCode.KBackspace,
            KeyCode.KInsert,
            KeyCode.KDelete,
            KeyCode.KPageup,
            KeyCode.KPagedown,
            KeyCode.KHome,
            KeyCode.KEnd,
            KeyCode.KCapslock,
            KeyCode.KScrolllock,
            KeyCode.KPrintscreen,
            KeyCode.KPause,
            KeyCode.KNumlockclear,
            KeyCode.KClear,
            KeyCode.KKP0,
            KeyCode.KKP1,
            KeyCode.KKP2,
            KeyCode.KKP3,
            KeyCode.KKP4,
            KeyCode.KKP5,
            KeyCode.KKP6,
            KeyCode.KKP7,
            KeyCode.KKP8,
            KeyCode.KKP9,
            KeyCode.KKPDivide,
            KeyCode.KKPMultiply,
            KeyCode.KKPMinus,
            KeyCode.KKPPlus,
            KeyCode.KKPDecimal,
            KeyCode.KKPEnter,
            KeyCode.KA,
            KeyCode.KB,
            KeyCode.KC,
            KeyCode.KD,
            KeyCode.KE,
            KeyCode.KF,
            KeyCode.KG,
            KeyCode.KH,
            KeyCode.KI,
            KeyCode.KJ,
            KeyCode.KK,
            KeyCode.KL,
            KeyCode.KM,
            KeyCode.KN,
            KeyCode.KO,
            KeyCode.KP,
            KeyCode.KQ,
            KeyCode.KR,
            KeyCode.KS,
            KeyCode.KT,
            KeyCode.KU,
            KeyCode.KV,
            KeyCode.KW,
            KeyCode.KX,
            KeyCode.KY,
            KeyCode.KZ,
            KeyCode.K0,
            KeyCode.K1,
            KeyCode.K2,
            KeyCode.K3,
            KeyCode.K4,
            KeyCode.K5,
            KeyCode.K6,
            KeyCode.K7,
            KeyCode.K8,
            KeyCode.K9,
            KeyCode.KBackquote,
            KeyCode.KBackquote,
            KeyCode.KMinus,
            KeyCode.KPlus,
            KeyCode.KLeftbracket,
            KeyCode.KRightbracket,
            KeyCode.KSemicolon,
            KeyCode.KQuote,
            KeyCode.KComma,
            KeyCode.KPeriod,
            KeyCode.KSlash,
            KeyCode.KBackslash,

            // Invalids
            KeyCode.K0,
        };

        public SDL2Keyboard(SDL2KeyboardDriver driver, string id, string name)
        {
            _driver = driver;
            Id = id;
            Name = name;
            _buttonsUserMapping = new List<ButtonMappingEntry>();
        }

        private bool HasConfiguration => _configuration != null;

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.None;

        public void Dispose()
        {
            // No operations
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ToSDL2Scancode(Key key)
        {
            if (key >= Key.Unknown && key <= Key.Menu)
            {
                return -1;
            }

            return (int)_sdl.GetScancodeFromKey((int)_keysDriverMapping[(int)key]);
        }

        private static Keymod GetKeyboardModifierMask(Key key)
        {
            return key switch
            {
                Key.ShiftLeft => Keymod.Lshift,
                Key.ShiftRight => Keymod.Rshift,
                Key.ControlLeft => Keymod.Lctrl,
                Key.ControlRight => Keymod.Rctrl,
                Key.AltLeft => Keymod.Lalt,
                Key.AltRight => Keymod.Ralt,
                Key.WinLeft => Keymod.Lgui,
                Key.WinRight => Keymod.Rgui,
                // NOTE: Menu key isn't supported by SDL2.
                _ => Keymod.None,
            };
        }

        public KeyboardStateSnapshot GetKeyboardStateSnapshot()
        {
            ReadOnlySpan<byte> rawKeyboardState;
            Keymod rawKeyboardModifierState = _sdl.GetModState();

            unsafe
            {
                int numKeys = new int();
                byte* statePtr = _sdl.GetKeyboardState(ref numKeys);

                rawKeyboardState = new ReadOnlySpan<byte>(statePtr, numKeys);
            }

            bool[] keysState = new bool[(int)Key.Count];

            for (Key key = 0; key < Key.Count; key++)
            {
                int index = ToSDL2Scancode(key);
                if (index == -1)
                {
                    Keymod modifierMask = GetKeyboardModifierMask(key);

                    if (modifierMask == Keymod.None)
                    {
                        continue;
                    }

                    keysState[(int)key] = (rawKeyboardModifierState & modifierMask) == modifierMask;
                }
                else
                {
                    keysState[(int)key] = rawKeyboardState[index] == 1;
                }
            }

            return new KeyboardStateSnapshot(keysState);
        }

        private static float ConvertRawStickValue(short value)
        {
            const float ConvertRate = 1.0f / (short.MaxValue + 0.5f);

            return value * ConvertRate;
        }

        private static (short, short) GetStickValues(ref KeyboardStateSnapshot snapshot, JoyconConfigKeyboardStick<ConfigKey> stickConfig)
        {
            short stickX = 0;
            short stickY = 0;

            if (snapshot.IsPressed((Key)stickConfig.StickUp))
            {
                stickY += 1;
            }

            if (snapshot.IsPressed((Key)stickConfig.StickDown))
            {
                stickY -= 1;
            }

            if (snapshot.IsPressed((Key)stickConfig.StickRight))
            {
                stickX += 1;
            }

            if (snapshot.IsPressed((Key)stickConfig.StickLeft))
            {
                stickX -= 1;
            }

            Vector2 stick = Vector2.Normalize(new Vector2(stickX, stickY));

            return ((short)(stick.X * short.MaxValue), (short)(stick.Y * short.MaxValue));
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            KeyboardStateSnapshot rawState = GetKeyboardStateSnapshot();
            GamepadStateSnapshot result = default;

            lock (_userMappingLock)
            {
                if (!HasConfiguration)
                {
                    return result;
                }

                foreach (ButtonMappingEntry entry in _buttonsUserMapping)
                {
                    if (entry.From == Key.Unknown || entry.From == Key.Unbound || entry.To == GamepadButtonInputId.Unbound)
                    {
                        continue;
                    }

                    // Do not touch state of button already pressed
                    if (!result.IsPressed(entry.To))
                    {
                        result.SetPressed(entry.To, rawState.IsPressed(entry.From));
                    }
                }

                (short leftStickX, short leftStickY) = GetStickValues(ref rawState, _configuration.LeftJoyconStick);
                (short rightStickX, short rightStickY) = GetStickValues(ref rawState, _configuration.RightJoyconStick);

                result.SetStick(StickInputId.Left, ConvertRawStickValue(leftStickX), ConvertRawStickValue(leftStickY));
                result.SetStick(StickInputId.Right, ConvertRawStickValue(rightStickX), ConvertRawStickValue(rightStickY));
            }

            return result;
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            throw new NotSupportedException();
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            throw new NotSupportedException();
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            throw new NotSupportedException();
        }

        public bool IsPressed(Key key)
        {
            // We only implement GetKeyboardStateSnapshot.
            throw new NotSupportedException();
        }

        public void SetConfiguration(InputConfig configuration)
        {
            lock (_userMappingLock)
            {
                _configuration = (StandardKeyboardInputConfig)configuration;

                // First clear the buttons mapping
                _buttonsUserMapping.Clear();

                // Then configure left joycon
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftStick, (Key)_configuration.LeftJoyconStick.StickButton));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadUp, (Key)_configuration.LeftJoycon.DpadUp));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadDown, (Key)_configuration.LeftJoycon.DpadDown));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadLeft, (Key)_configuration.LeftJoycon.DpadLeft));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadRight, (Key)_configuration.LeftJoycon.DpadRight));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Minus, (Key)_configuration.LeftJoycon.ButtonMinus));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftShoulder, (Key)_configuration.LeftJoycon.ButtonL));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftTrigger, (Key)_configuration.LeftJoycon.ButtonZl));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger0, (Key)_configuration.LeftJoycon.ButtonSr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger0, (Key)_configuration.LeftJoycon.ButtonSl));

                // Finally configure right joycon
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightStick, (Key)_configuration.RightJoyconStick.StickButton));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.A, (Key)_configuration.RightJoycon.ButtonA));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.B, (Key)_configuration.RightJoycon.ButtonB));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.X, (Key)_configuration.RightJoycon.ButtonX));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Y, (Key)_configuration.RightJoycon.ButtonY));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Plus, (Key)_configuration.RightJoycon.ButtonPlus));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightShoulder, (Key)_configuration.RightJoycon.ButtonR));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightTrigger, (Key)_configuration.RightJoycon.ButtonZr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger1, (Key)_configuration.RightJoycon.ButtonSr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger1, (Key)_configuration.RightJoycon.ButtonSl));
            }
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            // No operations
        }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            // No operations
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            // No operations

            return Vector3.Zero;
        }
    }
}
