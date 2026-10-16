using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VocaloidBridge.V4
{
    public sealed class NativeAudio
    {
        public double StartSeconds { get; private set; }
        public int SampleRate { get; private set; }
        public float[] Samples { get; private set; }

        internal NativeAudio(double startSeconds, int sampleRate, float[] samples)
        {
            StartSeconds = startSeconds;
            SampleRate = sampleRate;
            Samples = samples;
        }
    }

    /// <summary>
    /// Runs the installed DSCL4 DaisyMidiBuffer reader and DSE4 incremental PCM
    /// renderer in-process. The Daisy pointer remains owned by the caller.
    /// </summary>
    public static class NativeSynthesis
    {
        private const int SampleRate = 44100;
        private const int FrameSize = 256;
        // CStreamBuffer::Create is called with record size 0x14 (20). Its
        // ReadNextEntry tail target passes that size to the copy helper, which
        // writes 20 bytes even though DSCL consumers only read fields 1..3.
        private const int EntrySize = 20;
        private const int EntryGuardBytes = 16;
        private const byte EntryGuardValue = 0x6B;
        private const int EventBufferLimit = 16 * 1024 * 1024;
        private const int MaxEventsPerFrame = 65536;
        private const int MaxEntries = 5000000;
        private const int MaxDurationMs = 10 * 60 * 1000;
        private const int MaxOutputSamples = 27000000;
        private const int PcmGuardBytes = 16;
        private const int PcmOverflowSlackBytes = 3072;
        private const byte PcmGuardValue = 0xA7;
        private const byte PcmOverflowValue = 0xD3;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void SeekToBeginDelegate(IntPtr daisyBuffer);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int ReadNextEntryDelegate(IntPtr daisyBuffer, IntPtr entry20Bytes);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate uint ReadEventDelegate(IntPtr daisyBuffer, uint eventOffset, IntPtr destination, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        public static NativeAudio Render(NativeApi api, uint handle, IntPtr daisyBuffer)
        {
            return Render(api, handle, daisyBuffer, null);
        }

        public static NativeAudio Render(NativeApi api, uint handle, IntPtr daisyBuffer, Func<bool> continueRendering)
        {
            if (IntPtr.Size != 4)
                throw new InvalidOperationException("Vocaloid4/DSCL4 rendering requires a 32-bit process.");
            if (api == null)
                throw new ArgumentNullException("api");
            if (daisyBuffer == IntPtr.Zero)
                throw new ArgumentException("DaisyMidiBuffer pointer is null.", "daisyBuffer");

            string dsclPath = Path.Combine(Path.GetDirectoryName(api.LibraryPath), "DSCL4.dll");
            IntPtr dscl = LoadLibraryW(dsclPath);
            if (dscl == IntPtr.Zero)
                throw new InvalidOperationException("LoadLibrary(DSCL4.dll) failed: " + Marshal.GetLastWin32Error());

            IntPtr entryMemory = IntPtr.Zero;
            IntPtr rawEventMemory = IntPtr.Zero;
            IntPtr eventRecordMemory = IntPtr.Zero;
            IntPtr pcmMemory = IntPtr.Zero;
            bool started = false;
            try
            {
                SeekToBeginDelegate seek = Bind<SeekToBeginDelegate>(dscl, "?SeekToBegin@CDaisyMidiBuffer@@QAEXXZ");
                ReadNextEntryDelegate readNext = Bind<ReadNextEntryDelegate>(dscl, "?ReadNextEntry@CDaisyMidiBuffer@@QAEHPAX@Z");
                ReadEventDelegate readEvent = Bind<ReadEventDelegate>(dscl, "?ReadEvent@CDaisyMidiBuffer@@QAEKKPAEH@Z");

                entryMemory = Marshal.AllocHGlobal(EntrySize + EntryGuardBytes);
                FillBytes(entryMemory, EntrySize, EntryGuardBytes, EntryGuardValue);
                rawEventMemory = Marshal.AllocHGlobal(EventBufferLimit);
                eventRecordMemory = Marshal.AllocHGlobal(checked(MaxEventsPerFrame * 8));
                // Keep 3072 bytes after the advertised 256-sample block. DSE's
                // descriptor still says 256; the slack makes an over-write
                // observable without immediately corrupting the process heap.
                pcmMemory = Marshal.AllocHGlobal(PcmGuardBytes + FrameSize * 2 + PcmOverflowSlackBytes + PcmGuardBytes);
                IntPtr pcmSamples = IntPtr.Add(pcmMemory, PcmGuardBytes);

                int endMs = Marshal.ReadInt32(daisyBuffer, 15 * 4);
                if (endMs < 0 || endMs > MaxDurationMs)
                    throw new InvalidDataException("Daisy end time is outside the supported 0..10 minute range.");

                seek(daisyBuffer);
                if (ReadNextEntryChecked(readNext, daisyBuffer, entryMemory) == 0)
                    throw new InvalidDataException("DaisyMidiBuffer contains no first entry.");

                int currentEventOffset = 0;
                int currentEventLength = 0;
                uint firstMs = ReadUInt32(entryMemory, 12);
                if (firstMs > endMs)
                    throw new InvalidDataException("The first Daisy entry starts after the buffer end time.");
                currentEventOffset = unchecked((int)ReadUInt32(entryMemory, 4));
                uint firstEventLength = ReadUInt32(entryMemory, 8);
                if (firstEventLength > EventBufferLimit)
                    throw new InvalidDataException("Daisy event payload exceeds the 16 MiB limit.");
                currentEventLength = (int)firstEventLength;

                long firstSample = MillisecondsToSample(firstMs);
                long endSample = MillisecondsToSample((uint)endMs);
                long frameStart = firstSample - firstSample % FrameSize;
                long frameLimit = endSample - endSample % FrameSize;
                long frameCount = ((frameLimit - frameStart) / FrameSize) + 1;
                if (frameCount <= 0 || frameCount > (MaxOutputSamples / FrameSize) + 1L)
                    throw new InvalidDataException("Daisy render duration exceeds the configured PCM sample limit.");

                List<float> output = new List<float>(checked((int)Math.Min(frameCount * FrameSize, MaxOutputSamples)));
                uint currentEventMs = firstMs;
                long entryCount = 1;
                bool haveEntry = true;
                int nativeResult = api.Start(handle);
                if (nativeResult != 0)
                    throw new InvalidOperationException("DSEStart failed with code " + nativeResult + ".");
                started = true;

                for (long frameStartSample = frameStart; frameStartSample <= frameLimit; frameStartSample += FrameSize)
                {
                    if (continueRendering != null && !continueRendering())
                        throw new OperationCanceledException("V4 PCM rendering was cancelled.");

                    long frameEndSample = frameStartSample + FrameSize;
                    List<DseMidiEventRecord> frameEvents = new List<DseMidiEventRecord>();
                    int activeEventIndex = -1;
                    int activeDataByteIndex = 0;
                    while (haveEntry && MillisecondsToSample(currentEventMs) < frameEndSample)
                    {
                        if (currentEventLength != 0)
                        {
                            if (currentEventLength < 0 || currentEventLength > EventBufferLimit)
                                throw new InvalidDataException("Daisy event payload exceeds the 16 MiB limit.");
                            uint bytesRead = readEvent(daisyBuffer, unchecked((uint)currentEventOffset), rawEventMemory, (uint)currentEventLength);
                            if (bytesRead != (uint)currentEventLength)
                                throw new InvalidDataException("DSCL4 returned a short Daisy event payload.");
                            AppendMidiEvents(rawEventMemory, currentEventLength, (int)(MillisecondsToSample(currentEventMs) & 0xff), frameEvents, ref activeEventIndex, ref activeDataByteIndex);
                        }

                        haveEntry = ReadNextEntryChecked(readNext, daisyBuffer, entryMemory) != 0;
                        if (haveEntry)
                        {
                            entryCount++;
                            if (entryCount > MaxEntries)
                                throw new InvalidDataException("DaisyMidiBuffer exceeds the 5 million entry limit.");
                            currentEventOffset = unchecked((int)ReadUInt32(entryMemory, 4));
                            uint payloadSize = ReadUInt32(entryMemory, 8);
                            if (payloadSize > EventBufferLimit)
                                throw new InvalidDataException("Daisy event payload exceeds the 16 MiB limit.");
                            currentEventLength = (int)payloadSize;
                            uint nextEventMs = ReadUInt32(entryMemory, 12);
                            if (nextEventMs < currentEventMs)
                                throw new InvalidDataException("DaisyMidiBuffer entry timestamps are not monotonic.");
                            currentEventMs = nextEventMs;
                            if (currentEventMs > endMs)
                                throw new InvalidDataException("A Daisy entry timestamp exceeds the buffer end time.");
                        }
                        else
                        {
                            currentEventLength = 0;
                        }
                    }

                    IntPtr eventPointer = IntPtr.Zero;
                    if (frameEvents.Count > MaxEventsPerFrame)
                        throw new InvalidDataException("Too many MIDI events in one DSE frame.");
                    if (frameEvents.Count != 0)
                    {
                        eventPointer = eventRecordMemory;
                        for (int i = 0; i < frameEvents.Count; i++)
                        {
                            int offset = checked(i * 8);
                            Marshal.WriteInt32(eventRecordMemory, offset, frameEvents[i].SampleOffsetIn256);
                            Marshal.WriteInt32(eventRecordMemory, offset + 4, unchecked((int)frameEvents[i].PackedMidiMessage));
                        }
                    }

                    DsePcmBuffer pcm = new DsePcmBuffer();
                    pcm.SampleCount = FrameSize;
                    pcm.Samples = pcmSamples;
                    FillBytes(pcmMemory, 0, PcmGuardBytes, PcmGuardValue);
                    FillBytes(pcmSamples, 0, FrameSize * 2, 0);
                    FillBytes(pcmSamples, FrameSize * 2, PcmOverflowSlackBytes, PcmOverflowValue);
                    FillBytes(pcmSamples, FrameSize * 2 + PcmOverflowSlackBytes, PcmGuardBytes, PcmGuardValue);
                    int stepResult = api.DoStepSynthesis(handle, frameEvents.Count, eventPointer, ref pcm);
                    if (stepResult != 0)
                        throw new InvalidOperationException("DSEDoStepSynthesis failed with code " + stepResult + ".");
                    int overwritten = FindUnexpectedByte(pcmSamples, FrameSize * 2, PcmOverflowSlackBytes, PcmOverflowValue);
                    if (overwritten >= 0)
                        throw new InvalidDataException("DSEDoStepSynthesis wrote past its advertised 256-sample PCM block (first overflow byte +" + overwritten + ").");
                    if (FindUnexpectedByte(pcmMemory, 0, PcmGuardBytes, PcmGuardValue) >= 0 ||
                        FindUnexpectedByte(pcmSamples, FrameSize * 2 + PcmOverflowSlackBytes, PcmGuardBytes, PcmGuardValue) >= 0)
                        throw new InvalidDataException("DSEDoStepSynthesis overwrote the PCM allocation boundary canary.");
                    if (pcm.SampleCount < 0 || pcm.SampleCount > FrameSize)
                        throw new InvalidDataException("DSE returned an invalid PCM sample count.");
                    if (pcm.SampleCount == 0)
                        break;
                    if ((long)output.Count + pcm.SampleCount > MaxOutputSamples)
                        throw new InvalidDataException("DSE PCM output exceeds the configured sample limit.");
                    for (int i = 0; i < pcm.SampleCount; i++)
                    {
                        short sample = Marshal.ReadInt16(pcmSamples, i * 2);
                        output.Add(sample / 32768.0f);
                    }
                }

                return new NativeAudio((double)frameStart / SampleRate, SampleRate, output.ToArray());
            }
            finally
            {
                if (started)
                    api.Stop(handle);
                if (pcmMemory != IntPtr.Zero) Marshal.FreeHGlobal(pcmMemory);
                if (eventRecordMemory != IntPtr.Zero) Marshal.FreeHGlobal(eventRecordMemory);
                if (rawEventMemory != IntPtr.Zero) Marshal.FreeHGlobal(rawEventMemory);
                if (entryMemory != IntPtr.Zero) Marshal.FreeHGlobal(entryMemory);
                FreeLibrary(dscl);
            }
        }

        private static long MillisecondsToSample(uint milliseconds)
        {
            return ((long)SampleRate * milliseconds) / 1000L;
        }

        private static uint ReadUInt32(IntPtr address, int offset)
        {
            return unchecked((uint)Marshal.ReadInt32(address, offset));
        }

        private static void FillBytes(IntPtr address, int offset, int count, byte value)
        {
            for (int i = 0; i < count; i++)
                Marshal.WriteByte(address, offset + i, value);
        }

        private static int FindUnexpectedByte(IntPtr address, int offset, int count, byte expected)
        {
            for (int i = 0; i < count; i++)
                if (Marshal.ReadByte(address, offset + i) != expected)
                    return i;
            return -1;
        }

        private static int ReadNextEntryChecked(ReadNextEntryDelegate readNext, IntPtr daisyBuffer, IntPtr entryMemory)
        {
            int result = readNext(daisyBuffer, entryMemory);
            if (FindUnexpectedByte(entryMemory, EntrySize, EntryGuardBytes, EntryGuardValue) >= 0)
                throw new InvalidDataException("DSCL4 ReadNextEntry wrote past its 20-byte Daisy entry record.");
            return result;
        }

        private static void AppendMidiEvents(IntPtr bytes, int length, int sampleOffset, List<DseMidiEventRecord> events, ref int eventIndex, ref int dataByteIndex)
        {
            for (int i = 0; i < length; i++)
            {
                byte value = Marshal.ReadByte(bytes, i);
                if ((value & 0x80) != 0)
                {
                    if (events.Count >= MaxEventsPerFrame)
                        throw new InvalidDataException("Too many MIDI events in one DSE frame.");
                    DseMidiEventRecord record = new DseMidiEventRecord();
                    record.SampleOffsetIn256 = sampleOffset;
                    record.PackedMidiMessage = 0;
                    events.Add(record);
                    eventIndex = events.Count - 1;
                    dataByteIndex = 0;
                }
                if (eventIndex < 0)
                    throw new InvalidDataException("Daisy MIDI payload starts with a data byte instead of a status byte.");
                if (dataByteIndex >= 4)
                    throw new InvalidDataException("A packed DSE MIDI event contains more than four bytes.");
                DseMidiEventRecord current = events[eventIndex];
                current.PackedMidiMessage |= ((uint)value << (8 * dataByteIndex));
                events[eventIndex] = current;
                dataByteIndex++;
            }
        }

        private static T Bind<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero)
                throw new MissingMethodException("DSCL4.dll export is missing: " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
    }
}
