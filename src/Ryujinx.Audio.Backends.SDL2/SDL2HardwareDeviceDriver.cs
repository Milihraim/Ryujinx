using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using Ryujinx.SDL2.Common;
using Silk.NET.SDL;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;


namespace Ryujinx.Audio.Backends.SDL2
{
    public class SDL2HardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;
        private readonly ConcurrentDictionary<SDL2HardwareDeviceSession, byte> _sessions;

        private readonly bool _supportSurroundConfiguration;
        public static Sdl sdl_Driver = SDL2Driver.SdlApi;

        public unsafe SDL2HardwareDeviceDriver()
        {
            _updateRequiredEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _sessions = new ConcurrentDictionary<SDL2HardwareDeviceSession, byte>();

            SDL2Driver.Instance.Initialize();


            AudioSpec spec;
            byte* nullBytePointer = null;
            byte** nullBytePointerPointer = &nullBytePointer; 
            int res = sdl_Driver.GetDefaultAudioInfo(nullBytePointerPointer, &spec, 0); 

            if (res != 0)
            {
                Logger.Error?.Print(LogClass.Application,
                    $"SDL_GetDefaultAudioInfo failed with error \"{sdl_Driver.GetErrorS()}\"");

                _supportSurroundConfiguration = true;
            }
            else
            {
                _supportSurroundConfiguration = spec.Channels >= 6;
            }
        }

        public static bool IsSupported => IsSupportedInternal();

        private static bool IsSupportedInternal()
        {
            uint device = OpenStream(SampleFormat.PcmInt16, Constants.TargetSampleRate, Constants.ChannelCountMax, Constants.TargetSampleCount);

            if (device != 0)
            {
                sdl_Driver.CloseAudioDevice(device);
            }

            return device != 0;
        }

        internal static unsafe uint OpenStream(SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount, uint sampleCount)
        {
            AudioSpec desired = GetSDL2Spec(requestedSampleFormat, requestedSampleRate, requestedChannelCount, sampleCount);
            
            AudioSpec got;

            uint device = sdl_Driver.OpenAudioDevice((byte*)null, 0, in desired, &got, 0);

            if (device == 0)
            {
                Logger.Error?.Print(LogClass.Application, $"SDL2 open audio device initialization failed with error \"{sdl_Driver.GetErrorS()}\"");

                return 0;
            }

            bool isValid = got.Format == desired.Format && got.Freq == desired.Freq && got.Channels == desired.Channels;

            if (!isValid)
            {
                Logger.Error?.Print(LogClass.Application, "SDL2 open audio device is not valid");
                sdl_Driver.CloseAudioDevice(device);

                return 0;
            }

            return device;
        }

        public ManualResetEvent GetUpdateRequiredEvent()
        {
            return _updateRequiredEvent;
        }

        public ManualResetEvent GetPauseEvent()
        {
            return _pauseEvent;
        }

        public IHardwareDeviceSession OpenDeviceSession(Direction direction, IVirtualMemoryManager memoryManager, SampleFormat sampleFormat, uint sampleRate, uint channelCount, float volume)
        {
            if (channelCount == 0)
            {
                channelCount = 2;
            }

            if (sampleRate == 0)
            {
                sampleRate = Constants.TargetSampleRate;
            }

            if (direction != Direction.Output)
            {
                throw new NotImplementedException("Input direction is currently not implemented on SDL2 backend!");
            }

            SDL2HardwareDeviceSession session = new(this, memoryManager, sampleFormat, sampleRate, channelCount, volume);

            _sessions.TryAdd(session, 0);

            return session;
        }

        internal bool Unregister(SDL2HardwareDeviceSession session)
        {
            return _sessions.TryRemove(session, out _);
        }

        private static AudioSpec GetSDL2Spec(SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount, uint sampleCount)
        {
            return new AudioSpec()
            {
                Channels = (byte)requestedChannelCount,
                Format = GetSDL2Format(requestedSampleFormat),
                Freq = (int)requestedSampleRate,
                Samples = (ushort)sampleCount,
            };
        }

        internal static ushort GetSDL2Format(SampleFormat format)
        {
            return format switch
            {
                SampleFormat.PcmInt8 => Sdl.AudioS8,
                SampleFormat.PcmInt16 => Sdl.AudioS16,
                SampleFormat.PcmInt32 => Sdl.AudioS32,
                SampleFormat.PcmFloat => Sdl.AudioF32,
                _ => throw new ArgumentException($"Unsupported sample format {format}"),
            };
        }

        internal static unsafe uint OpenStream(SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount, uint sampleCount, AudioCallback callback)
        {
            AudioSpec desired = GetSDL2Spec(requestedSampleFormat, requestedSampleRate, requestedChannelCount, sampleCount);

            desired.Callback = callback;
            
            AudioSpec got;

            uint device = sdl_Driver.OpenAudioDevice((byte*)null, 0, in desired, &got, 0);

            if (device == 0)
            {
                Logger.Error?.Print(LogClass.Application, $"SDL2 open audio device initialization failed with error \"{sdl_Driver.GetErrorS()}\"");

                return 0;
            }

            bool isValid = got.Format == desired.Format && got.Freq == desired.Freq && got.Channels == desired.Channels;

            if (!isValid)
            {
                Logger.Error?.Print(LogClass.Application, "SDL2 open audio device is not valid");
                sdl_Driver.CloseAudioDevice(device);

                return 0;
            }

            return device;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (SDL2HardwareDeviceSession session in _sessions.Keys)
                {
                    session.Dispose();
                }
                sdl_Driver?.Dispose();

                SDL2Driver.Instance.Dispose();

                _pauseEvent.Dispose();
            }
        }

        public bool SupportsSampleRate(uint sampleRate)
        {
            return true;
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return sampleFormat != SampleFormat.PcmInt24;
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            if (channelCount == 6)
            {
                return _supportSurroundConfiguration;
            }

            return true;
        }

        public bool SupportsDirection(Direction direction)
        {
            // TODO: add direction input when supported.
            return direction == Direction.Output;
        }
    }
}
