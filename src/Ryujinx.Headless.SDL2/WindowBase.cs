using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.SDL2.Common;
using Silk.NET.SDL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AntiAliasing = Ryujinx.Common.Configuration.AntiAliasing;
using ScalingFilter = Ryujinx.Common.Configuration.ScalingFilter;
using Switch = Ryujinx.HLE.Switch;
using Thread = System.Threading.Thread;

namespace Ryujinx.Headless.SDL2
{
    abstract class WindowBase : IHostUIHandler, IDisposable
    {
        protected const int DefaultWidth = 1280;
        protected const int DefaultHeight = 720;
        private const int TargetFps = 60;
        private WindowFlags DefaultFlags = WindowFlags.AllowHighdpi | WindowFlags.Resizable | WindowFlags.InputFocus | WindowFlags.Shown;
        private WindowFlags FullscreenFlag = 0;

        private static readonly ConcurrentQueue<Action> _mainThreadActions = new();
        

        public static void QueueMainThreadAction(Action action)
        {
            _mainThreadActions.Enqueue(action);
        }

        public NpadManager NpadManager { get; }
        public TouchScreenManager TouchScreenManager { get; }
        public Switch Device { get; private set; }
        public IRenderer Renderer { get; private set; }

        public event EventHandler<StatusUpdatedEventArgs> StatusUpdatedEvent;

        protected unsafe Silk.NET.SDL.Window* WindowHandle { get; set; }

        public IHostUITheme HostUITheme { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int DisplayId { get; set; }
        public bool IsFullscreen { get; set; }
        public bool IsExclusiveFullscreen { get; set; }
        public int ExclusiveFullscreenWidth { get; set; }
        public int ExclusiveFullscreenHeight { get; set; }
        public AntiAliasing AntiAliasing { get; set; }
        public ScalingFilter ScalingFilter { get; set; }
        public int ScalingFilterLevel { get; set; }

        protected SDL2MouseDriver MouseDriver;
        private readonly InputManager _inputManager;
        private readonly IKeyboard _keyboardInterface;
        private readonly GraphicsDebugLevel _glLogLevel;
        private readonly Stopwatch _chrono;
        private readonly long _ticksPerFrame;
        private readonly CancellationTokenSource _gpuCancellationTokenSource;
        private readonly ManualResetEvent _exitEvent;
        private readonly ManualResetEvent _gpuDoneEvent;
        private Sdl _sdl = Sdl.GetApi();

        private long _ticks;
        private bool _isActive;
        private bool _isStopped;
        private uint _windowId;

        private string _gpuDriverName;

        private readonly AspectRatio _aspectRatio;
        private readonly bool _enableMouse;
        private IHostUIHandler _hostUiHandlerImplementation;

        public WindowBase(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode)
        {
            MouseDriver = new SDL2MouseDriver(hideCursorMode);
            _inputManager = inputManager;
            _inputManager.SetMouseDriver(MouseDriver);
            NpadManager = _inputManager.CreateNpadManager();
            TouchScreenManager = _inputManager.CreateTouchScreenManager();
            _keyboardInterface = (IKeyboard)_inputManager.KeyboardDriver.GetGamepad("0");
            _glLogLevel = glLogLevel;
            _chrono = new Stopwatch();
            _ticksPerFrame = Stopwatch.Frequency / TargetFps;
            _gpuCancellationTokenSource = new CancellationTokenSource();
            _exitEvent = new ManualResetEvent(false);
            _gpuDoneEvent = new ManualResetEvent(false);
            _aspectRatio = aspectRatio;
            _enableMouse = enableMouse;
            HostUITheme = new HeadlessHostUiTheme();

            SDL2Driver.Instance.Initialize();
        }

        public void Initialize(Switch device, List<InputConfig> inputConfigs, bool enableKeyboard, bool enableMouse)
        {
            Device = device;

            IRenderer renderer = Device.Gpu.Renderer;

            if (renderer is ThreadedRenderer tr)
            {
                renderer = tr.BaseRenderer;
            }

            Renderer = renderer;

            NpadManager.Initialize(device, inputConfigs, enableKeyboard, enableMouse);
            TouchScreenManager.Initialize(device);
        }

        private void SetWindowIcon()
        {
            Stream iconStream = typeof(WindowBase).Assembly.GetManifestResourceStream("Ryujinx.Headless.SDL2.Ryujinx.bmp");
            if (iconStream == null)
            {
                Logger.Error?.Print(LogClass.Application, "Icon stream could not be found.");
                return;
            }

            int headerSize = 54;
            int imageSize = (int)iconStream.Length - headerSize;
            byte[] iconBytes = new byte[imageSize];

            iconStream.Seek(headerSize, SeekOrigin.Begin);
            if (iconStream.Read(iconBytes, 0, imageSize) != imageSize)
            {
                Logger.Error?.Print(LogClass.Application, "Failed to read the icon pixel data.");
                return;
            }

            iconStream.Close();

            int width = 48;
            int depth = 32;
            int pitch = width * (depth / 8);
            uint format = Sdl.PixelformatArgb8888;

            unsafe
            {
                fixed (byte* iconPtr = iconBytes)
                {
                    // Create an SDL surface using the pixel data and specified format
                    Surface* surfacePtr = _sdl.CreateRGBSurfaceWithFormatFrom(iconPtr, width, 48, depth, pitch, format);
                    if (surfacePtr == null)
                    {
                        Logger.Error?.Print(LogClass.Application, "Failed to create SDL surface from icon bytes.");
                        return;
                    }

                    _sdl.SetWindowIcon(WindowHandle, surfacePtr);
                    _sdl.FreeSurface(surfacePtr);
                }
            }
        }


        private unsafe void InitializeWindow()
        {
            var activeProcess = Device.Processes.ActiveApplication;
            var nacp = activeProcess.ApplicationControlProperties;
            int desiredLanguage = (int)Device.System.State.DesiredTitleLanguage;

            string titleNameSection = string.IsNullOrWhiteSpace(nacp.Title[desiredLanguage].NameString.ToString()) ? string.Empty : $" - {nacp.Title[desiredLanguage].NameString.ToString()}";
            string titleVersionSection = string.IsNullOrWhiteSpace(nacp.DisplayVersionString.ToString()) ? string.Empty : $" v{nacp.DisplayVersionString.ToString()}";
            string titleIdSection = string.IsNullOrWhiteSpace(activeProcess.ProgramIdText) ? string.Empty : $" ({activeProcess.ProgramIdText.ToUpper()})";
            string titleArchSection = activeProcess.Is64Bit ? " (64-bit)" : " (32-bit)";

            Width = DefaultWidth;
            Height = DefaultHeight;

            if (IsExclusiveFullscreen)
            {
                Width = ExclusiveFullscreenWidth;
                Height = ExclusiveFullscreenHeight;

                DefaultFlags = WindowFlags.AllowHighdpi;
                FullscreenFlag = WindowFlags.Fullscreen;
            }
            else if (IsFullscreen)
            {
                DefaultFlags = WindowFlags.AllowHighdpi;
                FullscreenFlag = WindowFlags.FullscreenDesktop;
            }

            WindowHandle = _sdl.CreateWindow($"Ryujinx {Program.Version}{titleNameSection}{titleVersionSection}{titleIdSection}{titleArchSection}", Sdl.WindowposCenteredDisplay(DisplayId), Sdl.WindowposCenteredDisplay(DisplayId), Width, Height, (uint)(DefaultFlags | FullscreenFlag | GetWindowFlags()));

            if (WindowHandle == (void*)IntPtr.Zero)
            {
                string errorMessage = $"SDL_CreateWindow failed with error \"{_sdl.GetErrorS()}\"";

                Logger.Error?.Print(LogClass.Application, errorMessage);

                throw new Exception(errorMessage);
            }

            SetWindowIcon();

            _windowId = _sdl.GetWindowID(WindowHandle);
            SDL2Driver.Instance.RegisterWindow(_windowId, HandleWindowEvent);
        }

        private void HandleWindowEvent(Event evnt)
        {
            if (evnt.Type == (UIntPtr)EventType.Windowevent)
            {
                switch (evnt.Window.Event)
                {
                    case (byte)WindowEventID.SizeChanged:
                        // Unlike on Windows, this event fires on macOS when triggering fullscreen mode.
                        // And promptly crashes the process because `Renderer?.window.SetSize` is undefined.
                        // As we don't need this to fire in either case we can test for fullscreen.
                        if (!IsFullscreen && !IsExclusiveFullscreen)
                        {
                            Width = evnt.Window.Data1;
                            Height = evnt.Window.Data2;
                            Renderer?.Window.SetSize(Width, Height);
                            MouseDriver.SetClientSize(Width, Height);
                        }
                        break;

                    case (byte)WindowEventID.Close:
                        Exit();
                        break;
                }
            }
            else
            {
                MouseDriver.Update(evnt);
            }
        }

        protected abstract void InitializeWindowRenderer();

        protected abstract void InitializeRenderer();

        protected abstract void FinalizeWindowRenderer();

        protected abstract void SwapBuffers();

        public abstract WindowFlags GetWindowFlags();

        private string GetGpuDriverName()
        {
            return Renderer.GetHardwareInfo().GpuDriver;
        }

        private void SetAntiAliasing()
        {
            Renderer?.Window.SetAntiAliasing((Graphics.GAL.AntiAliasing)AntiAliasing);
        }

        private void SetScalingFilter()
        {
            Renderer?.Window.SetScalingFilter((Graphics.GAL.ScalingFilter)ScalingFilter);
            Renderer?.Window.SetScalingFilterLevel(ScalingFilterLevel);
        }

        public void Render()
        {
            InitializeWindowRenderer();

            Device.Gpu.Renderer.Initialize(_glLogLevel);

            InitializeRenderer();

            SetAntiAliasing();

            SetScalingFilter();

            _gpuDriverName = GetGpuDriverName();

            Device.Gpu.Renderer.RunLoop(() =>
            {
                Device.Gpu.SetGpuThread();
                Device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                while (_isActive)
                {
                    if (_isStopped)
                    {
                        return;
                    }

                    _ticks += _chrono.ElapsedTicks;

                    _chrono.Restart();

                    if (Device.WaitFifo())
                    {
                        Device.Statistics.RecordFifoStart();
                        Device.ProcessFrame();
                        Device.Statistics.RecordFifoEnd();
                    }

                    while (Device.ConsumeFrameAvailable())
                    {
                        Device.PresentFrame(SwapBuffers);
                    }

                    if (_ticks >= _ticksPerFrame)
                    {
                        string dockedMode = Device.System.State.DockedMode ? "Docked" : "Handheld";
                        float scale = GraphicsConfig.ResScale;
                        if (scale != 1)
                        {
                            dockedMode += $" ({scale}x)";
                        }

                        StatusUpdatedEvent?.Invoke(this, new StatusUpdatedEventArgs(
                            Device.EnableDeviceVsync,
                            dockedMode,
                            Device.Configuration.AspectRatio.ToText(),
                            $"Game: {Device.Statistics.GetGameFrameRate():00.00} FPS ({Device.Statistics.GetGameFrameTime():00.00} ms)",
                            $"FIFO: {Device.Statistics.GetFifoPercent():0.00} %",
                            $"GPU: {_gpuDriverName}"));

                        _ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame);
                    }
                }

                // Make sure all commands in the run loop are fully executed before leaving the loop.
                if (Device.Gpu.Renderer is ThreadedRenderer threaded)
                {
                    threaded.FlushThreadedCommands();
                }

                _gpuDoneEvent.Set();
            });

            FinalizeWindowRenderer();
        }

        public void Exit()
        {
            TouchScreenManager?.Dispose();
            NpadManager?.Dispose();

            if (_isStopped)
            {
                return;
            }

            _gpuCancellationTokenSource.Cancel();

            _isStopped = true;
            _isActive = false;

            _exitEvent.WaitOne();
            _exitEvent.Dispose();
        }

        public static void ProcessMainThreadQueue()
        {
            while (_mainThreadActions.TryDequeue(out Action action))
            {
                action();
            }
        }

        public void MainLoop()
        {
            while (_isActive)
            {
                UpdateFrame();

                _sdl.PumpEvents();

                ProcessMainThreadQueue();

                // Polling becomes expensive if it's not slept
                Thread.Sleep(1);
            }

            _exitEvent.Set();
        }

        private void NvidiaStutterWorkaround()
        {
            while (_isActive)
            {
                // When NVIDIA Threaded Optimization is on, the driver will snapshot all threads in the system whenever the application creates any new ones.
                // The ThreadPool has something called a "GateThread" which terminates itself after some inactivity.
                // However, it immediately starts up again, since the rules regarding when to terminate and when to start differ.
                // This creates a new thread every second or so.
                // The main problem with this is that the thread snapshot can take 70ms, is on the OpenGL thread and will delay rendering any graphics.
                // This is a little over budget on a frame time of 16ms, so creates a large stutter.
                // The solution is to keep the ThreadPool active so that it never has a reason to terminate the GateThread.

                // TODO: This should be removed when the issue with the GateThread is resolved.

                ThreadPool.QueueUserWorkItem(state => { });
                Thread.Sleep(300);
            }
        }

        private bool UpdateFrame()
        {
            if (!_isActive)
            {
                return true;
            }

            if (_isStopped)
            {
                return false;
            }

            NpadManager.Update();

            // Touchscreen
            bool hasTouch = false;

            // Get screen touch position
            if (!_enableMouse)
            {
                hasTouch = TouchScreenManager.Update(true, (_inputManager.MouseDriver as SDL2MouseDriver).IsButtonPressed(MouseButton.Button1), _aspectRatio.ToFloat());
            }

            if (!hasTouch)
            {
                TouchScreenManager.Update(false);
            }

            Device.Hid.DebugPad.Update();

            // TODO: Replace this with MouseDriver.CheckIdle() when mouse motion events are received on every supported platform.
            MouseDriver.UpdatePosition();

            return true;
        }

        public void Execute()
        {
            _chrono.Restart();
            _isActive = true;

            InitializeWindow();

            Thread renderLoopThread = new(Render)
            {
                Name = "GUI.RenderLoop",
            };
            renderLoopThread.Start();

            Thread nvidiaStutterWorkaround = null;
            if (Renderer is OpenGLRenderer)
            {
                nvidiaStutterWorkaround = new Thread(NvidiaStutterWorkaround)
                {
                    Name = "GUI.NvidiaStutterWorkaround",
                };
                nvidiaStutterWorkaround.Start();
            }

            MainLoop();

            // NOTE: The render loop is allowed to stay alive until the renderer itself is disposed, as it may handle resource dispose.
            // We only need to wait for all commands submitted during the main gpu loop to be processed.
            _gpuDoneEvent.WaitOne();
            _gpuDoneEvent.Dispose();
            nvidiaStutterWorkaround?.Join();

            Exit();
        }

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            // SDL2 doesn't support input dialogs
            userText = "Ryujinx";

            return true;
        }

        public unsafe bool DisplayMessageDialog(string title, string message)
        {
            _sdl.ShowSimpleMessageBox((uint)MessageBoxFlags.Information, title, message, WindowHandle);

            return true;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            string playerCount = args.PlayerCountMin == args.PlayerCountMax ? $"exactly {args.PlayerCountMin}" : $"{args.PlayerCountMin}-{args.PlayerCountMax}";

            string message = $"Application requests {playerCount} player(s) with:\n\n"
                           + $"TYPES: {args.SupportedStyles}\n\n"
                           + $"PLAYERS: {string.Join(", ", args.SupportedPlayers)}\n\n"
                           + (args.IsDocked ? "Docked mode set. Handheld is also invalid.\n\n" : "")
                           + "Please reconfigure Input now and then press OK.";

            return DisplayMessageDialog("Controller Applet", message);
        }
        
        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            return new HeadlessDynamicTextInputHandler();
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);

            Exit();
        }

        public unsafe bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
        {
            byte[] titleBytes = Encoding.UTF8.GetBytes(title + "\0");
            byte* pTitle = (byte*)Marshal.AllocHGlobal(titleBytes.Length);
            Marshal.Copy(titleBytes, 0, (IntPtr)pTitle, titleBytes.Length);

            byte[] messageBytes = Encoding.UTF8.GetBytes(message + "\0");
            byte* pMessage = (byte*)Marshal.AllocHGlobal(messageBytes.Length);
            Marshal.Copy(messageBytes, 0, (IntPtr)pMessage, messageBytes.Length);

            MessageBoxData data = new()
            {
                Title = pTitle,
                Message = pMessage,
                Buttons = (MessageBoxButtonData*)Marshal.AllocHGlobal(sizeof(MessageBoxButtonData) * buttonsText.Length),
                Numbuttons = buttonsText.Length,
                Window = WindowHandle,
            };

            for (int i = 0; i < buttonsText.Length; i++)
            {
                byte[] textBytes = Encoding.UTF8.GetBytes(buttonsText[i] + "\0");
                byte* pText = (byte*)Marshal.AllocHGlobal(textBytes.Length);
                Marshal.Copy(textBytes, 0, (IntPtr)pText, textBytes.Length);

                data.Buttons[i] = new MessageBoxButtonData()
                {
                    Buttonid = i,
                    Text = pText,
                };
            }

            int buttonId = 0;
            _sdl.ShowMessageBox(in data, ref buttonId);

            // Free the allocated memory
            for (int i = 0; i < buttonsText.Length; i++)
            {
                Marshal.FreeHGlobal((IntPtr)data.Buttons[i].Text);
            }
            Marshal.FreeHGlobal((IntPtr)data.Buttons);
            Marshal.FreeHGlobal((IntPtr)pTitle);
            Marshal.FreeHGlobal((IntPtr)pMessage);

            return true;
        }


        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isActive = false;
                TouchScreenManager?.Dispose();
                NpadManager.Dispose();

                SDL2Driver.Instance.UnregisterWindow(_windowId);

                _sdl.DestroyWindow(WindowHandle);

                SDL2Driver.Instance.Dispose();
            }
        }
    }
}
