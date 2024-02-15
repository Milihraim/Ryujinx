using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Silk.NET.SDL;
using Thread = System.Threading.Thread;

namespace Ryujinx.SDL2.Common
{
    public class SDL2Driver : IDisposable
    {
        private static SDL2Driver _instance;

        public static Sdl SdlApi = Sdl.GetApi();

        public static SDL2Driver Instance
        {
            get
            {
                _instance ??= new SDL2Driver();

                return _instance;
            }
        }

        public static Action<Action> MainThreadDispatcher { get; set; }

        private const uint SdlInitFlags = Sdl.InitEvents | Sdl.InitGamecontroller | Sdl.InitJoystick | Sdl.InitAudio | Sdl.InitVideo;

        private bool _isRunning;
        private uint _refereceCount;
        private Thread _worker;

        public event Action<int, int> OnJoyStickConnected;
        public event Action<int> OnJoystickDisconnected;

        private ConcurrentDictionary<uint, Action<Event>> _registeredWindowHandlers;

        private readonly object _lock = new();

        private SDL2Driver() { }

        //private const string SDL_HINT_JOYSTICK_HIDAPI_COMBINE_JOY_CONS = "SDL_JOYSTICK_HIDAPI_COMBINE_JOY_CONS";

        public void Initialize()
        {
            lock (_lock)
            {
                _refereceCount++;

                if (_isRunning)
                {
                    return;
                }

                SdlApi = Sdl.GetApi();

                SdlApi.SetHint(Sdl.HintJoystickHidapiPS4Rumble, 1);
                SdlApi.SetHint(Sdl.HintJoystickHidapiPS5Rumble, 1);
                SdlApi.SetHint(Sdl.HintJoystickAllowBackgroundEvents, 1);
                SdlApi.SetHint(Sdl.HintJoystickHidapiSwitchHomeLed, 0);
                SdlApi.SetHint(Sdl.HintJoystickHidapiJoyCons, 1);
                SdlApi.SetHint(Sdl.HintVideoAllowScreensaver, 1);


                // NOTE: As of SDL2 2.24.0, joycons are combined by default but the motion source only come from one of them.
                // We disable this behavior for now.
                SdlApi.SetHint(Sdl.HintJoystickHidapiCombineJoyCons, 0);

                if (SdlApi.Init(SdlInitFlags) != 0)
                {
                    string errorMessage = $"SDL2 initialization failed with error \"{SdlApi.GetErrorS()}\"";

                    Logger.Error?.Print(LogClass.Application, errorMessage);

                    throw new Exception(errorMessage);
                }

                // First ensure that we only enable joystick events (for connected/disconnected).
                if (SdlApi.GameControllerEventState(Sdl.Ignore) != 0)
                {
                    Logger.Error?.PrintMsg(LogClass.Application, "Couldn't change the state of game controller events.");
                }

                if (SdlApi.JoystickEventState(Sdl.Enable) < 0)
                {
                    Logger.Error?.PrintMsg(LogClass.Application, $"Failed to enable joystick event polling: {SdlApi.GetErrorS()}");
                }

                // Disable all joysticks information, we don't need them no need to flood the event queue for that.
                SdlApi.EventState((uint)EventType.Joyaxismotion, Sdl.Disable);
                SdlApi.EventState((uint)EventType.Joyballmotion, Sdl.Disable);
                SdlApi.EventState((uint)EventType.Joyhatmotion, Sdl.Disable);
                SdlApi.EventState((uint)EventType.Joybuttondown, Sdl.Disable);
                SdlApi.EventState((uint)EventType.Joybuttonup, Sdl.Disable);

                SdlApi.EventState((uint)EventType.Controllersensorupdate, Sdl.Disable);

                string gamepadDbPath = Path.Combine(AppDataManager.BaseDirPath, "SDL_GameControllerDB.txt");

                if (File.Exists(gamepadDbPath))
                {
                    SdlApi.GameControllerAddMapping(gamepadDbPath);
                }

                _registeredWindowHandlers = new ConcurrentDictionary<uint, Action<Event>>();
                _worker = new Thread(EventWorker);
                _isRunning = true;
                _worker.Start();
            }
        }

        public bool RegisterWindow(uint windowId, Action<Event> windowEventHandler)
        {
            return _registeredWindowHandlers.TryAdd(windowId, windowEventHandler);
        }

        public void UnregisterWindow(uint windowId)
        {
            _registeredWindowHandlers.Remove(windowId, out _);
        }

        private void HandleSDLEvent(ref Event evnt)
        {
            if (evnt.Type == (UIntPtr)EventType.Joydeviceadded)
            {
                int deviceId = evnt.Cbutton.Which;

                // SDL2 loves to be inconsistent here by providing the device id instead of the instance id (like on removed event), as such we just grab it and send it inside our system.
                int instanceId = SdlApi.JoystickGetDeviceInstanceID(deviceId);

                if (instanceId == -1)
                {
                    return;
                }

                Logger.Debug?.Print(LogClass.Application, $"Added joystick instance id {instanceId}");

                OnJoyStickConnected?.Invoke(deviceId, instanceId);
            }
            else if (evnt.Type == (UIntPtr)EventType.Joydeviceremoved)
            {
                Logger.Debug?.Print(LogClass.Application, $"Removed joystick instance id {evnt.Cbutton.Which}");

                OnJoystickDisconnected?.Invoke(evnt.Cbutton.Which);
            }
            else if (evnt.Type == (UIntPtr)EventType.Windowevent || evnt.Type == (UIntPtr)EventType.Mousebuttondown || evnt.Type == (UIntPtr)EventType.Mousebuttonup)
            {
                if (_registeredWindowHandlers.TryGetValue(evnt.Window.WindowID, out Action<Event> handler))
                {
                    handler(evnt);
                }
            }
        }

        private void EventWorker()
        {
            const int WaitTimeMs = 10;

            using ManualResetEventSlim waitHandle = new(false);

            while (_isRunning)
            {
                MainThreadDispatcher?.Invoke(() =>
                {
                    Event evnt = new Event();
                    while (SdlApi.PollEvent(ref evnt) != 0)
                    {
                        HandleSDLEvent(ref evnt);
                    }
                });

                waitHandle.Wait(WaitTimeMs);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            lock (_lock)
            {
                if (_isRunning)
                {
                    _refereceCount--;

                    if (_refereceCount == 0)
                    {
                        _isRunning = false;

                        _worker?.Join();

                        SdlApi.Quit();

                        OnJoyStickConnected = null;
                        OnJoystickDisconnected = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
    }
}
