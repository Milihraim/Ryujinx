using OpenTK;
using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Input.HLE;
using Silk.NET.Maths;
using Silk.NET.SDL;
using System;

namespace Ryujinx.Headless.SDL2.OpenGL
{
    class OpenGLWindow : WindowBase
    {
        private static Sdl _sdl = Sdl.GetApi();
        private static void CheckResult(int result)
        {
            
            if (result < 0)
            {
                throw new InvalidOperationException($"SDL_GL function returned an error: {_sdl.GetErrorS()}");
            }
        }

        private static void SetupOpenGLAttributes(bool sharedContext, GraphicsDebugLevel debugLevel)
        {
            CheckResult(_sdl.GLSetAttribute(GLattr.ContextMajorVersion, 3));
            CheckResult(_sdl.GLSetAttribute(GLattr.ContextMinorVersion, 3));
            CheckResult(_sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GLprofile.Compatibility));
            CheckResult(_sdl.GLSetAttribute(GLattr.ContextFlags, debugLevel != GraphicsDebugLevel.None ? (int)GLcontextFlag.DebugFlag : 0));
            CheckResult(_sdl.GLSetAttribute(GLattr.ShareWithCurrentContext, sharedContext ? 1 : 0));

            CheckResult(_sdl.GLSetAttribute(GLattr.AcceleratedVisual, 1));
            CheckResult(_sdl.GLSetAttribute(GLattr.RedSize, 8));
            CheckResult(_sdl.GLSetAttribute(GLattr.GreenSize, 8));
            CheckResult(_sdl.GLSetAttribute(GLattr.BlueSize, 8));
            CheckResult(_sdl.GLSetAttribute(GLattr.AlphaSize, 8));
            CheckResult(_sdl.GLSetAttribute(GLattr.DepthSize, 16));
            CheckResult(_sdl.GLSetAttribute(GLattr.StencilSize, 0));
            CheckResult(_sdl.GLSetAttribute(GLattr.Doublebuffer, 1));
            CheckResult(_sdl.GLSetAttribute(GLattr.Stereo, 0));
        }

        private class OpenToolkitBindingsContext : IBindingsContext
        {
            public unsafe IntPtr GetProcAddress(string procName)
            {
                return (IntPtr)_sdl.GLGetProcAddress(procName);
            }
        }

        private class SDL2OpenGLContext : IOpenGLContext
        {
            private readonly unsafe void* _context;
            private readonly unsafe Window* _window;
            private readonly bool _shouldDisposeWindow;

            public unsafe SDL2OpenGLContext(void* context, Window* window, bool shouldDisposeWindow = true)
            {
                _context = context;
                _window = window;
                _shouldDisposeWindow = shouldDisposeWindow;
            }

            public static unsafe SDL2OpenGLContext CreateBackgroundContext(SDL2OpenGLContext sharedContext)
            {
                sharedContext.MakeCurrent();

                // Ensure we share our contexts.
                SetupOpenGLAttributes(true, GraphicsDebugLevel.None);
                Window* windowHandle = _sdl.CreateWindow("Ryujinx background context window", 0, 0, 1, 1, (uint)WindowFlags.Opengl |
                    (uint)WindowFlags.Hidden);
                void* context = _sdl.GLCreateContext(windowHandle);

                GL.LoadBindings(new OpenToolkitBindingsContext());

                CheckResult(_sdl.GLSetAttribute(GLattr.ShareWithCurrentContext, 0));

                CheckResult(_sdl.GLMakeCurrent(windowHandle, (void*)IntPtr.Zero));

                return new SDL2OpenGLContext(context, windowHandle);
            }

            public unsafe void MakeCurrent()
            {
                if (_sdl.GLGetCurrentContext() == _context || _sdl.GLGetCurrentWindow() == _window)
                {
                    return;
                }

                int res = _sdl.GLMakeCurrent(_window, _context);

                if (res != 0)
                {
                    string errorMessage = $"SDL_GL_CreateContext failed with error \"{_sdl.GetErrorS()}\"";

                    Logger.Error?.Print(LogClass.Application, errorMessage);

                    throw new Exception(errorMessage);
                }
            }

            public unsafe bool HasContext() => _sdl.GLGetCurrentContext() != (void*)IntPtr.Zero;

            public unsafe void Dispose()
            {
                _sdl.GLDeleteContext(_context);

                if (_shouldDisposeWindow)
                {
                    _sdl.DestroyWindow(_window);
                }
            }
        }

        private readonly GraphicsDebugLevel _glLogLevel;
        private SDL2OpenGLContext _openGLContext;

        public OpenGLWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode)
            : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode)
        {
            _glLogLevel = glLogLevel;
        }

        public override WindowFlags GetWindowFlags() => WindowFlags.Opengl;

        protected override unsafe void InitializeWindowRenderer()
        {
            // Ensure to not share this context with other contexts before this point.
            SetupOpenGLAttributes(false, _glLogLevel);
            void* context = _sdl.GLCreateContext(WindowHandle);
            CheckResult(_sdl.GLSetSwapInterval(1));

            if (context == (void*)IntPtr.Zero)
            {
                string errorMessage = $"SDL_GL_CreateContext failed with error \"{_sdl.GetErrorS()}\"";

                Logger.Error?.Print(LogClass.Application, errorMessage);

                throw new Exception(errorMessage);
            }

            // NOTE: The window handle needs to be disposed by the thread that created it and is handled separately.
            _openGLContext = new SDL2OpenGLContext(context, WindowHandle, false);

            // First take exclusivity on the OpenGL context.
            ((OpenGLRenderer)Renderer).InitializeBackgroundContext(SDL2OpenGLContext.CreateBackgroundContext(_openGLContext));

            _openGLContext.MakeCurrent();

            GL.ClearColor(0, 0, 0, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            SwapBuffers();

            if (IsExclusiveFullscreen)
            {
                Renderer?.Window.SetSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
                MouseDriver.SetClientSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
            }
            else if (IsFullscreen)
            {
                Rectangle<int> displayBounds = new Rectangle<int>();
                // NOTE: grabbing the main display's dimensions directly as OpenGL doesn't scale along like the VulkanWindow.
                if (_sdl.GetDisplayBounds(DisplayId, ref displayBounds) < 0)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Could not retrieve display bounds: {_sdl.GetErrorS()}");

                    // Fallback to defaults
                    displayBounds = new Rectangle<int>(0, 0, DefaultWidth, DefaultHeight);
                }

                Renderer?.Window.SetSize(displayBounds.Size.X, displayBounds.Size.Y);
                MouseDriver.SetClientSize(displayBounds.Size.X, displayBounds.Size.Y);
            }
            else
            {
                Renderer?.Window.SetSize(DefaultWidth, DefaultHeight);
                MouseDriver.SetClientSize(DefaultWidth, DefaultHeight);
            }
        }

        protected override void InitializeRenderer() { }

        protected override unsafe void FinalizeWindowRenderer()
        {
            // Try to bind the OpenGL context before calling the gpu disposal.
            _openGLContext.MakeCurrent();

            Device.DisposeGpu();

            // Unbind context and destroy everything
            CheckResult(_sdl.GLMakeCurrent(WindowHandle, (void*)IntPtr.Zero));
            _openGLContext.Dispose();
        }

        protected override unsafe void SwapBuffers()
        {
            _sdl.GLSwapWindow(WindowHandle);
        }
    }
}
