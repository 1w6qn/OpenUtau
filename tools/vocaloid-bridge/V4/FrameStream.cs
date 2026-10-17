using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VocaloidBridge.V4
{
    /// <summary>
    /// Resumable frame stream over DSCL's Daisy buffer and DSE's incremental
    /// synthesis: the engine is advanced frame by frame, so a host can serve
    /// sequential windows without re-synthesizing from the phrase start.
    /// </summary>
    public sealed class FrameStream : IDisposable
    {
        const int SampleRate = 44100;
        const int FrameSize = 256;
        const int EntrySize = 20;
        const int EntryGuardBytes = 16;
        const byte EntryGuardValue = 0x6B;
        const int EventBufferLimit = 16 * 1024 * 1024;
        const int MaxEventsPerFrame = 65536;
        const int MaxEntries = 5000000;
        const int MaxDurationMs = 10 * 60 * 1000;
        const int PcmGuardBytes = 16;
        const int PcmOverflowSlackBytes = 3072;
        const byte PcmGuardValue = 0xA7;
        const byte PcmOverflowValue = 0xD3;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void SeekToBeginDelegate(IntPtr daisyBuffer);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate int ReadNextEntryDelegate(IntPtr daisyBuffer, IntPtr entry20Bytes);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate uint ReadEventDelegate(IntPtr daisyBuffer, uint eventOffset, IntPtr destination, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string fileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        readonly NativeApi api;
        readonly uint handle;
        readonly IntPtr daisy;
        IntPtr dscl;
        IntPtr entryMemory, rawEventMemory, eventRecordMemory, pcmMemory, pcmSamples;
        SeekToBeginDelegate seek;
        ReadNextEntryDelegate readNext;
        ReadEventDelegate readEvent;
        long frameStart, frameLimit, frameIndex;
        uint currentEventMs, currentEventOffset, currentEventLength;
        long entryCount;
        bool haveEntry;
        bool started;
        int endMs;

        public int SampleRateHz { get { return SampleRate; } }
        public int FrameSizeSamples { get { return FrameSize; } }
        public long FrameIndex { get { return frameIndex; } }
        public long TotalFrames { get { return ((frameLimit - frameStart) / FrameSize) + 1; } }
        public double StartSeconds { get { return (double)frameStart / SampleRate; } }

        public FrameStream(NativeApi api, uint handle, IntPtr daisyBuffer)
        {
            if (IntPtr.Size != 4) throw new InvalidOperationException("VOCALOID4/DSCL4 streaming requires a 32-bit process.");
            this.api = api;
            this.handle = handle;
            this.daisy = daisyBuffer;
            dscl = LoadLibraryW(Path.Combine(Path.GetDirectoryName(api.LibraryPath), "DSCL4.dll"));
            if (dscl == IntPtr.Zero) throw new InvalidOperationException("LoadLibrary(DSCL4.dll) failed: " + Marshal.GetLastWin32Error());
            try
            {
                seek = Bind<SeekToBeginDelegate>("?SeekToBegin@CDaisyMidiBuffer@@QAEXXZ");
                readNext = Bind<ReadNextEntryDelegate>("?ReadNextEntry@CDaisyMidiBuffer@@QAEHPAX@Z");
                readEvent = Bind<ReadEventDelegate>("?ReadEvent@CDaisyMidiBuffer@@QAEKKPAEH@Z");
                entryMemory = Marshal.AllocHGlobal(EntrySize + EntryGuardBytes);
                rawEventMemory = Marshal.AllocHGlobal(EventBufferLimit);
                eventRecordMemory = Marshal.AllocHGlobal(checked(MaxEventsPerFrame * 8));
                pcmMemory = Marshal.AllocHGlobal(PcmGuardBytes + FrameSize * 2 + PcmOverflowSlackBytes + PcmGuardBytes);
                pcmSamples = IntPtr.Add(pcmMemory, PcmGuardBytes);
                endMs = Marshal.ReadInt32(daisy, 15 * 4);
                if (endMs < 0 || endMs > MaxDurationMs) throw new InvalidDataException("Daisy end time is outside 0..10 minutes.");
                seek(daisy);
                Fill(entryMemory, EntrySize, EntryGuardBytes, EntryGuardValue);
                if (ReadNextEntryChecked() == 0) throw new InvalidDataException("DaisyMidiBuffer contains no first entry.");
                currentEventOffset = ReadUInt32(entryMemory, 4);
                currentEventLength = ReadUInt32(entryMemory, 8);
                currentEventMs = ReadUInt32(entryMemory, 12);
                haveEntry = true;
                entryCount = 1;
                long firstSample = MillisecondsToSample(currentEventMs);
                long endSample = MillisecondsToSample((uint)endMs);
                frameStart = firstSample - firstSample % FrameSize;
                frameLimit = endSample - endSample % FrameSize;
                frameIndex = 0;
                int result = api.Start(handle);
                if (result != 0) throw new InvalidOperationException("DSEStart failed with code " + result + ".");
                started = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Advance the engine to the given absolute frame, discarding audio.</summary>
        public double PreRoll(long targetFrame)
        {
            long count = Math.Max(0, targetFrame - frameIndex);
            var scratch = new List<float>();
            while (count > 0)
            {
                int chunk = (int)Math.Min(count, 2048);
                scratch.Clear();
                ReadFrames(chunk, scratch);
                count -= chunk;
            }
            return 0.0;
        }

        /// <summary>Render the next frames and append them to the caller's buffer.</summary>
        public void ReadFrames(int count, List<float> output)
        {
            for (int i = 0; i < count && frameIndex <= (frameLimit - frameStart) / FrameSize; i++)
            {
                long frameStartSample = frameStart + frameIndex * FrameSize;
                long frameEndSample = frameStartSample + FrameSize;
                var frameEvents = new List<DseMidiEventRecord>();
                int activeEventIndex = -1;
                int activeDataByteIndex = 0;
                while (haveEntry && MillisecondsToSample(currentEventMs) < frameEndSample)
                {
                    if (currentEventLength != 0)
                    {
                        if (currentEventLength > EventBufferLimit) throw new InvalidDataException("Daisy event payload exceeds 16 MiB.");
                        uint bytesRead = readEvent(daisy, currentEventOffset, rawEventMemory, currentEventLength);
                        if (bytesRead != currentEventLength) throw new InvalidDataException("DSCL4 returned a short Daisy event payload.");
                        AppendMidiEvents(rawEventMemory, (int)currentEventLength, (int)(MillisecondsToSample(currentEventMs) & 0xff), frameEvents, ref activeEventIndex, ref activeDataByteIndex);
                    }
                    haveEntry = ReadNextEntryChecked() != 0;
                    if (haveEntry)
                    {
                        entryCount++;
                        if (entryCount > MaxEntries) throw new InvalidDataException("DaisyMidiBuffer exceeds the entry limit.");
                        currentEventOffset = ReadUInt32(entryMemory, 4);
                        uint payloadSize = ReadUInt32(entryMemory, 8);
                        if (payloadSize > EventBufferLimit) throw new InvalidDataException("Daisy event payload exceeds 16 MiB.");
                        currentEventLength = payloadSize;
                        uint nextEventMs = ReadUInt32(entryMemory, 12);
                        if (nextEventMs < currentEventMs) throw new InvalidDataException("Daisy timestamps are not monotonic.");
                        currentEventMs = nextEventMs;
                    }
                    else currentEventLength = 0;
                }

                IntPtr eventPointer = IntPtr.Zero;
                if (frameEvents.Count != 0)
                {
                    if (frameEvents.Count > MaxEventsPerFrame) throw new InvalidDataException("Too many MIDI events in one frame.");
                    eventPointer = eventRecordMemory;
                    for (int e = 0; e < frameEvents.Count; e++)
                    {
                        Marshal.WriteInt32(eventRecordMemory, e * 8, frameEvents[e].SampleOffsetIn256);
                        Marshal.WriteInt32(eventRecordMemory, e * 8 + 4, unchecked((int)frameEvents[e].PackedMidiMessage));
                    }
                }
                var pcm = new DsePcmBuffer();
                pcm.SampleCount = FrameSize;
                pcm.Samples = pcmSamples;
                Fill(pcmMemory, 0, PcmGuardBytes, PcmGuardValue);
                Fill(pcmSamples, 0, FrameSize * 2, 0);
                Fill(pcmSamples, FrameSize * 2, PcmOverflowSlackBytes, PcmOverflowValue);
                Fill(pcmSamples, FrameSize * 2 + PcmOverflowSlackBytes, PcmGuardBytes, PcmGuardValue);
                int stepResult = api.DoStepSynthesis(handle, frameEvents.Count, eventPointer, ref pcm);
                if (stepResult != 0) throw new InvalidOperationException("DSEDoStepSynthesis failed with code " + stepResult + ".");
                if (FindUnexpectedByte(pcmSamples, FrameSize * 2, PcmOverflowSlackBytes, PcmOverflowValue) >= 0)
                    throw new InvalidDataException("DSEDoStepSynthesis wrote past its 256-sample PCM block.");
                if (pcm.SampleCount < 0 || pcm.SampleCount > FrameSize) throw new InvalidDataException("DSE returned an invalid PCM sample count.");
                if (pcm.SampleCount == 0) break;
                for (int s = 0; s < pcm.SampleCount; s++) output.Add(Marshal.ReadInt16(pcmSamples, s * 2) / 32768.0f);
                frameIndex++;
            }
        }

        int ReadNextEntryChecked()
        {
            int result = readNext(daisy, entryMemory);
            if (FindUnexpectedByte(entryMemory, EntrySize, EntryGuardBytes, EntryGuardValue) >= 0)
                throw new InvalidDataException("DSCL4 ReadNextEntry wrote past its 20-byte entry record.");
            return result;
        }

        static long MillisecondsToSample(uint milliseconds) { return ((long)SampleRate * milliseconds) / 1000L; }
        static uint ReadUInt32(IntPtr address, int offset) { return unchecked((uint)Marshal.ReadInt32(address, offset)); }

        static void Fill(IntPtr address, int offset, int count, byte value)
        {
            for (int i = 0; i < count; i++) Marshal.WriteByte(address, offset + i, value);
        }

        static int FindUnexpectedByte(IntPtr address, int offset, int count, byte expected)
        {
            for (int i = 0; i < count; i++) if (Marshal.ReadByte(address, offset + i) != expected) return i;
            return -1;
        }

        static void AppendMidiEvents(IntPtr bytes, int length, int sampleOffset, List<DseMidiEventRecord> events, ref int eventIndex, ref int dataByteIndex)
        {
            for (int i = 0; i < length; i++)
            {
                byte value = Marshal.ReadByte(bytes, i);
                if ((value & 0x80) != 0)
                {
                    if (events.Count >= MaxEventsPerFrame) throw new InvalidDataException("Too many MIDI events in one frame.");
                    var record = new DseMidiEventRecord();
                    record.SampleOffsetIn256 = sampleOffset;
                    events.Add(record);
                    eventIndex = events.Count - 1;
                    dataByteIndex = 0;
                }
                if (eventIndex < 0) throw new InvalidDataException("Daisy MIDI payload starts with a data byte.");
                if (dataByteIndex >= 4) throw new InvalidDataException("A packed DSE MIDI event exceeds four bytes.");
                var current = events[eventIndex];
                current.PackedMidiMessage |= ((uint)value << (8 * dataByteIndex));
                events[eventIndex] = current;
                dataByteIndex++;
            }
        }

        T Bind<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(dscl, name);
            if (address == IntPtr.Zero) throw new MissingMethodException("DSCL4.dll export is missing: " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        public void Dispose()
        {
            if (started) { try { api.Stop(handle); } catch { } started = false; }
            if (pcmMemory != IntPtr.Zero) { Marshal.FreeHGlobal(pcmMemory); pcmMemory = IntPtr.Zero; }
            if (eventRecordMemory != IntPtr.Zero) { Marshal.FreeHGlobal(eventRecordMemory); eventRecordMemory = IntPtr.Zero; }
            if (rawEventMemory != IntPtr.Zero) { Marshal.FreeHGlobal(rawEventMemory); rawEventMemory = IntPtr.Zero; }
            if (entryMemory != IntPtr.Zero) { Marshal.FreeHGlobal(entryMemory); entryMemory = IntPtr.Zero; }
            if (dscl != IntPtr.Zero) { FreeLibrary(dscl); dscl = IntPtr.Zero; }
        }
    }
}
