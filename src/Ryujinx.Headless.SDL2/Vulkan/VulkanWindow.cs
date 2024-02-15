using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input.HLE;
using Ryujinx.SDL2.Common;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using VkHandle = Silk.NET.Core.Native.VkHandle;

namespace Ryujinx.Headless.SDL2.Vulkan
{
    class VulkanWindow : WindowBase
    {
        private readonly GraphicsDebugLevel _glLogLevel;
        private Sdl _sdl = Sdl.GetApi();

        public VulkanWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode)
            : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode)
        {
            _glLogLevel = glLogLevel;
        }

        public override WindowFlags GetWindowFlags() => WindowFlags.Vulkan;

        protected override void InitializeWindowRenderer() { }

        protected override void InitializeRenderer()
        {
            if (IsExclusiveFullscreen)
            {
                Renderer?.Window.SetSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
                MouseDriver.SetClientSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
            }
            else
            {
                Renderer?.Window.SetSize(DefaultWidth, DefaultHeight);
                MouseDriver.SetClientSize(DefaultWidth, DefaultHeight);
            }
        }

        private static void BasicInvoke(Action action)
        {
            action();
        }

        public unsafe ulong? CreateWindowSurface(IntPtr instance)
        {
            VkNonDispatchableHandle surfaceHandle = new VkNonDispatchableHandle();

            void CreateSurface()
            {
                if (_sdl.VulkanCreateSurface(WindowHandle, new VkHandle(instance), ref surfaceHandle) == SdlBool.False)
                {
                    string errorMessage = $"SDL_Vulkan_CreateSurface failed with error \"{_sdl.GetErrorS()}\"";

                    Logger.Error?.Print(LogClass.Application, errorMessage);

                    throw new Exception(errorMessage);
                }
            }

            if (SDL2Driver.MainThreadDispatcher != null)
            {
                SDL2Driver.MainThreadDispatcher(CreateSurface);
            }
            else
            {
                CreateSurface();
            }

            return surfaceHandle.Handle;
        }

        public unsafe string[] GetRequiredInstanceExtensions()
        {
            uint extensionsCount = new uint();
            if (_sdl.VulkanGetInstanceExtensions(WindowHandle, ref extensionsCount, (byte**)IntPtr.Zero) == SdlBool.True)
            {
                IntPtr[] rawExtensions = new IntPtr[(int)extensionsCount];
                string[] extensions = new string[(int)extensionsCount];

                fixed (IntPtr* rawExtensionsPtr = rawExtensions)
                {
                    if (_sdl.VulkanGetInstanceExtensions(WindowHandle, ref extensionsCount, (byte**)rawExtensionsPtr) == SdlBool.True)
                    {
                        for (int i = 0; i < extensions.Length; i++)
                        {
                            extensions[i] = Marshal.PtrToStringUTF8(rawExtensions[i]);
                        }

                        return extensions;
                    }
                }
            }

            string errorMessage = $"SDL_Vulkan_GetInstanceExtensions failed with error \"{_sdl.GetErrorS()}\"";

            Logger.Error?.Print(LogClass.Application, errorMessage);

            throw new Exception(errorMessage);
        }

        protected override void FinalizeWindowRenderer()
        {
            Device.DisposeGpu();
        }

        protected override void SwapBuffers() { }
    }
}
