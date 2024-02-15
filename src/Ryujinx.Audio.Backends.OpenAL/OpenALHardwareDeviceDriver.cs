using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using Silk.NET.OpenAL;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Audio.Backends.OpenAL
{
    public class OpenALHardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly unsafe Context* _context;
        private ALContext _alc;
        private unsafe Device* device;
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;
        private readonly ConcurrentDictionary<OpenALHardwareDeviceSession, byte> _sessions;
        private bool _stillRunning;
        private readonly Thread _updaterThread;

        public unsafe OpenALHardwareDeviceDriver()
        {
            _alc = ALContext.GetApi();
            device = _alc.OpenDevice("");
            _context = _alc.CreateContext(device, null);
            _updateRequiredEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _sessions = new ConcurrentDictionary<OpenALHardwareDeviceSession, byte>();

            _stillRunning = true;
            _updaterThread = new Thread(Update)
            {
                Name = "HardwareDeviceDriver.OpenAL",
            };

            _updaterThread.Start();
        }

        public static unsafe bool IsSupported
        {
            get
            {
                try
                {
                    var alc = ALContext.GetApi();
                    var _device = alc.OpenDevice("");

                    if (alc.IsExtensionPresent(_device, "ALC_ENUMERATION_EXT") == false)
                    {
                        return false;
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
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
                throw new ArgumentException($"{direction}");
            }
            else if (!SupportsChannelCount(channelCount))
            {
                throw new ArgumentException($"{channelCount}");
            }

            OpenALHardwareDeviceSession session = new(this, memoryManager, sampleFormat, sampleRate, channelCount, volume);

            _sessions.TryAdd(session, 0);

            return session;
        }

        internal bool Unregister(OpenALHardwareDeviceSession session)
        {
            return _sessions.TryRemove(session, out _);
        }

        public ManualResetEvent GetUpdateRequiredEvent()
        {
            return _updateRequiredEvent;
        }

        public ManualResetEvent GetPauseEvent()
        {
            return _pauseEvent;
        }

        private unsafe void Update()
        {
            _alc.MakeContextCurrent(_context);

            while (_stillRunning)
            {
                bool updateRequired = false;

                foreach (OpenALHardwareDeviceSession session in _sessions.Keys)
                {
                    if (session.Update())
                    {
                        updateRequired = true;
                    }
                }

                if (updateRequired)
                {
                    _updateRequiredEvent.Set();
                }

                // If it's not slept it will waste cycles.
                Thread.Sleep(10);
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stillRunning = false;

                foreach (OpenALHardwareDeviceSession session in _sessions.Keys)
                {
                    session.Dispose();
                }

                _alc.DestroyContext(_context);
                _alc.CloseDevice(device);

                _pauseEvent.Dispose();
            }
        }

        public bool SupportsSampleRate(uint sampleRate)
        {
            return true;
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return true;
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            return channelCount == 1 || channelCount == 2 || channelCount == 6;
        }

        public bool SupportsDirection(Direction direction)
        {
            return direction == Direction.Output;
        }
    }
}
