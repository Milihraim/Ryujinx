using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input;
using Silk.NET.SDL;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Ryujinx.Headless.SDL2
{
    class SDL2MouseDriver : IGamepadDriver
    {
        private const int CursorHideIdleTime = 5; // seconds

        private bool _isDisposed;
        private readonly HideCursorMode _hideCursorMode;
        private bool _isHidden;
        private long _lastCursorMoveTime;
        private Sdl _sdl = Sdl.GetApi();

        public bool[] PressedButtons { get; }

        public Vector2 CurrentPosition { get; private set; }
        public Vector2 Scroll { get; private set; }
        public Size ClientSize;

        public SDL2MouseDriver(HideCursorMode hideCursorMode)
        {
            PressedButtons = new bool[(int)MouseButton.Count];
            _hideCursorMode = hideCursorMode;

            if (_hideCursorMode == HideCursorMode.Always)
            {
                if (_sdl.ShowCursor(Sdl.Disable) != Sdl.Disable)
                {
                    Logger.Error?.PrintMsg(LogClass.Application, "Failed to disable the cursor.");
                }

                _isHidden = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static MouseButton DriverButtonToMouseButton(uint rawButton)
        {
            Debug.Assert(rawButton > 0 && rawButton <= (int)MouseButton.Count);

            return (MouseButton)(rawButton - 1);
        }

        public void UpdatePosition()
        {
            int posX = 0;
            int posY = 0;
            _ = _sdl.GetMouseState(ref posX, ref posY);
            Vector2 position = new(posX, posY);

            if (CurrentPosition != position)
            {
                CurrentPosition = position;
                _lastCursorMoveTime = Stopwatch.GetTimestamp();
            }

            CheckIdle();
        }

        private void CheckIdle()
        {
            if (_hideCursorMode != HideCursorMode.OnIdle)
            {
                return;
            }

            long cursorMoveDelta = Stopwatch.GetTimestamp() - _lastCursorMoveTime;

            if (cursorMoveDelta >= CursorHideIdleTime * Stopwatch.Frequency)
            {
                if (!_isHidden)
                {
                    if (_sdl.ShowCursor(Sdl.Disable) != Sdl.Disable)
                    {
                        Logger.Error?.PrintMsg(LogClass.Application, "Failed to disable the cursor.");
                    }

                    _isHidden = true;
                }
            }
            else
            {
                if (_isHidden)
                {
                    if (_sdl.ShowCursor(Sdl.Enable) != Sdl.Enable)
                    {
                        Logger.Error?.PrintMsg(LogClass.Application, "Failed to enable the cursor.");
                    }

                    _isHidden = false;
                }
            }
        }

        public void Update(Event evnt)
        {
            switch (evnt.Type)
            {
                case (uint)EventType.Mousebuttondown:
                case (uint)EventType.Mousebuttonup:
                    uint rawButton = evnt.Button.Button;

                    if (rawButton > 0 && rawButton <= (int)MouseButton.Count)
                    {
                        PressedButtons[(int)DriverButtonToMouseButton(rawButton)] = evnt.Type == (UIntPtr)EventType.Mousebuttondown;

                        CurrentPosition = new Vector2(evnt.Button.X, evnt.Button.Y);
                    }

                    break;

                // NOTE: On Linux using Wayland mouse motion events won't be received at all.
                case (uint)EventType.Mousemotion:
                    CurrentPosition = new Vector2(evnt.Motion.X, evnt.Motion.Y);
                    _lastCursorMoveTime = Stopwatch.GetTimestamp();

                    break;

                case (uint)EventType.Mousewheel:
                    Scroll = new Vector2(evnt.Wheel.X, evnt.Wheel.Y);

                    break;
            }
        }

        public void SetClientSize(int width, int height)
        {
            ClientSize = new Size(width, height);
        }

        public bool IsButtonPressed(MouseButton button)
        {
            return PressedButtons[(int)button];
        }

        public Size GetClientSize()
        {
            return ClientSize;
        }

        public string DriverName => "SDL2";

        public event Action<string> OnGamepadConnected
        {
            add { }
            remove { }
        }

        public event Action<string> OnGamepadDisconnected
        {
            add { }
            remove { }
        }

        public ReadOnlySpan<string> GamepadsIds => new[] { "0" };

        public IGamepad GetGamepad(string id)
        {
            return new SDL2Mouse(this);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }
    }
}
