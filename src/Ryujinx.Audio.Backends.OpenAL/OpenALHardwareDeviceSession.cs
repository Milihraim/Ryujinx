using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Memory;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using Silk.NET.OpenAL.Extensions.EXT.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ryujinx.Audio.Backends.OpenAL
{
    class OpenALHardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private readonly OpenALHardwareDeviceDriver _driver;
        private readonly BufferFormat _targetFormat;
        private bool _isActive;
        private AL _al;
        private readonly Queue<OpenALAudioBuffer> _queuedBuffers;
        private ulong _playedSampleCount;
        private UInt32 sourceId;

        private readonly object _lock = new();

        public OpenALHardwareDeviceSession(OpenALHardwareDeviceDriver driver, IVirtualMemoryManager memoryManager, SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount, float requestedVolume) : base(memoryManager, requestedSampleFormat, requestedSampleRate, requestedChannelCount)
        {
            _driver = driver;
            _queuedBuffers = new Queue<OpenALAudioBuffer>();
            _al = AL.GetApi();
            sourceId = _al.GenSource();
            _targetFormat = GetALFormat();
            _isActive = false;
            _playedSampleCount = 0;
            SetVolume(requestedVolume);
        }

        private BufferFormat GetALFormat()
        {
            return RequestedSampleFormat switch
            {
                SampleFormat.PcmInt16 => RequestedChannelCount switch
                {
                    1 => BufferFormat.Mono16,
                    2 => BufferFormat.Stereo16,
                    6 => (BufferFormat)MCBufferFormat.S51Chn16,
                    _ => throw new NotImplementedException($"Unsupported channel config {RequestedChannelCount}"),
                },
                _ => throw new NotImplementedException($"Unsupported sample format {RequestedSampleFormat}"),
            };
        }

        public override void PrepareToClose() { }

        private void StartIfNotPlaying()
        {
            _al.GetSourceProperty(sourceId, GetSourceInteger.SourceState, out int stateInt);

            SourceState State = (SourceState)stateInt;

            if (State != SourceState.Playing)
            {
                _al.SourcePlay(sourceId);
            }
        }

        public override unsafe void QueueBuffer(AudioBuffer buffer)
        {
            lock (_lock)
            {
                OpenALAudioBuffer driverBuffer = new()
                {
                    DriverIdentifier = buffer.DataPointer,
                    BufferId = _al.GenBuffer(),
                    SampleCount = GetSampleCount(buffer),
                };

                _al.BufferData(driverBuffer.BufferId, _targetFormat, buffer.Data, (int)RequestedSampleRate);

                _queuedBuffers.Enqueue(driverBuffer);

                // Use fixed statement to pin bufferIds array and obtain a pointer
                uint[] bufferIds = new uint[] { driverBuffer.BufferId };
                fixed (uint* bufferIdsPtr = bufferIds)
                {
                    _al.SourceQueueBuffers(sourceId, 1, bufferIdsPtr);
                }

                if (_isActive)
                {
                    StartIfNotPlaying();
                }
            }
        }

        public override void SetVolume(float volume)
        {
            lock (_lock)
            {
                _al.SetSourceProperty(sourceId, SourceFloat.Gain, volume);
            }
        }

        public override float GetVolume()
        {
            _al.GetSourceProperty(sourceId, SourceFloat.Gain, out float volume);

            return volume;
        }

        public override void Start()
        {
            lock (_lock)
            {
                _isActive = true;

                StartIfNotPlaying();
            }
        }

        public override void Stop()
        {
            lock (_lock)
            {
                SetVolume(0.0f);

                _al.SourceStop(sourceId);

                _isActive = false;
            }
        }

        public override void UnregisterBuffer(AudioBuffer buffer) { }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            lock (_lock)
            {
                if (!_queuedBuffers.TryPeek(out OpenALAudioBuffer driverBuffer))
                {
                    return true;
                }

                return driverBuffer.DriverIdentifier != buffer.DataPointer;
            }
        }

        public override ulong GetPlayedSampleCount()
        {
            lock (_lock)
            {
                return _playedSampleCount;
            }
        }

        public unsafe bool Update()
        {
            lock (_lock)
            {
                if (_isActive)
                {
                    _al.GetSourceProperty(sourceId, GetSourceInteger.BuffersProcessed, out int releasedCount);

                    if (releasedCount > 0)
                    {
                        uint[] bufferIds = new uint[releasedCount];

                        // Pin bufferIds array in memory and get a pointer to it
                        fixed (uint* bufferIdsPtr = bufferIds)
                        {
                            _al.SourceUnqueueBuffers(sourceId, releasedCount, bufferIdsPtr);
                        }

                        int i = 0;

                        while (_queuedBuffers.TryPeek(out OpenALAudioBuffer buffer) && i < bufferIds.Length)
                        {
                            if (buffer.BufferId == bufferIds[i])
                            {
                                _playedSampleCount += buffer.SampleCount;

                                _queuedBuffers.TryDequeue(out _);

                                i++;
                            }
                        }

                        Debug.Assert(i == bufferIds.Length, "Unknown buffer ids found!");

                        _al.DeleteBuffers(bufferIds);
                    }

                    return releasedCount > 0;
                }

                return false;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _driver.Unregister(this))
            {
                lock (_lock)
                {
                    PrepareToClose();
                    Stop();

                    _al.DeleteSource(sourceId);
                }
            }
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
