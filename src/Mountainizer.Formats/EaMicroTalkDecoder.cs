using System.Buffers.Binary;
using Mountainizer.Core;

namespace Mountainizer.Formats;

/// <summary>
/// Decodes Electronic Arts MicroTalk (EA-MT/UTalk), including the version-3
/// per-frame PCM correction blocks used by SSX 3 BNKl banks.
/// </summary>
/// <remarks>
/// Independently ported from vgmstream's utkdec implementation, which is based
/// on Electronic Arts code analysis and Andrew D'Addesio's public-domain
/// utkencode project. The vgmstream implementation is distributed under the
/// ISC license; see THIRD_PARTY_NOTICES.md.
/// </remarks>
public static class EaMicroTalkDecoder
{
    public const int SamplesPerFrame = 432;

    private static readonly float[] ReflectionCoefficients =
    [
        +0.000000f, -0.996776f, -0.990327f, -0.983879f, -0.977431f, -0.970982f, -0.964534f, -0.958085f,
        -0.951637f, -0.930754f, -0.904960f, -0.879167f, -0.853373f, -0.827579f, -0.801786f, -0.775992f,
        -0.750198f, -0.724405f, -0.698611f, -0.670635f, -0.619048f, -0.567460f, -0.515873f, -0.464286f,
        -0.412698f, -0.361111f, -0.309524f, -0.257937f, -0.206349f, -0.154762f, -0.103175f, -0.051587f,
        +0.000000f, +0.051587f, +0.103175f, +0.154762f, +0.206349f, +0.257937f, +0.309524f, +0.361111f,
        +0.412698f, +0.464286f, +0.515873f, +0.567460f, +0.619048f, +0.670635f, +0.698611f, +0.724405f,
        +0.750198f, +0.775992f, +0.801786f, +0.827579f, +0.853373f, +0.879167f, +0.904960f, +0.930754f,
        +0.951637f, +0.958085f, +0.964534f, +0.970982f, +0.977431f, +0.983879f, +0.990327f, +0.996776f
    ];

    private static readonly byte[][] Codebooks =
    [
        Convert.FromBase64String("BAYFCQQGBQ0EBgUKBAYFEQQGBQkEBgUOBAYFCgQGBRUEBgUJBAYFDQQGBQoEBgUSBAYFCQQGBQ4EBgUKBAYFGQQGBQkEBgUNBAYFCgQGBREEBgUJBAYFDgQGBQoEBgUWBAYFCQQGBQ0EBgUKBAYFEgQGBQkEBgUOBAYFCgQGBQAEBgUJBAYFDQQGBQoEBgURBAYFCQQGBQ4EBgUKBAYFFQQGBQkEBgUNBAYFCgQGBRIEBgUJBAYFDgQGBQoEBgUaBAYFCQQGBQ0EBgUKBAYFEQQGBQkEBgUOBAYFCgQGBRYEBgUJBAYFDQQGBQoEBgUSBAYFCQQGBQ4EBgUKBAYFAg=="),
        Convert.FromBase64String("BAsHDwQMCBMECwcQBAwIFwQLBw8EDAgUBAsHEAQMCBsECwcPBAwIEwQLBxAEDAgYBAsHDwQMCBQECwcQBAwIAQQLBw8EDAgTBAsHEAQMCBcECwcPBAwIFAQLBxAEDAgcBAsHDwQMCBMECwcQBAwIGAQLBw8EDAgUBAsHEAQMCAMECwcPBAwIEwQLBxAEDAgXBAsHDwQMCBQECwcQBAwIGwQLBw8EDAgTBAsHEAQMCBgECwcPBAwIFAQLBxAEDAgBBAsHDwQMCBMECwcQBAwIFwQLBw8EDAgUBAsHEAQMCBwECwcPBAwIEwQLBxAEDAgYBAsHDwQMCBQECwcQBAwIAw==")
    ];

    private static readonly Command[] Commands =
    [
        new(1, 8, 0), new(1, 7, 0), new(0, 8, 0), new(0, 7, 0), new(0, 2, 0),
        new(0, 2, -1), new(0, 2, +1), new(0, 3, -1), new(0, 3, +1),
        new(1, 4, -2), new(1, 4, +2), new(1, 3, -2), new(1, 3, +2),
        new(1, 5, -3), new(1, 5, +3), new(1, 4, -3), new(1, 4, +3),
        new(1, 6, -4), new(1, 6, +4), new(1, 5, -4), new(1, 5, +4),
        new(1, 7, -5), new(1, 7, +5), new(1, 6, -5), new(1, 6, +5),
        new(1, 8, -6), new(1, 8, +6), new(1, 7, -6), new(1, 7, +6)
    ];

    public static short[] DecodeBankSection(BnklBankAsset bank, BnklSoundInfoSection section)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(section);
        if (section.Codec != 4)
            throw new NotSupportedException($"BNKl codec {section.Codec} is not EA MicroTalk 10:1.");
        if (section.ChannelCount != 1 || section.ChannelOffsets.Count != 1)
            throw new NotSupportedException("Only the mono EA MicroTalk layout shipped by SSX 3 is supported.");
        var offset = checked((int)section.ChannelOffsets[0] - bank.BodyOffset);
        if ((uint)offset >= (uint)bank.Body.Length)
            throw new FormatException("BNKl MicroTalk channel offset is outside the bank body.", offset, 1, bank.Body.Length);
        var usesPcmCorrections = section.StreamVersion >= 3;
        if (section.LoopStart is not int loopStart || loopStart <= 0 || section.MicroTalkLoopRelativeOffset is not uint loopRelativeOffset)
            return Decode(bank.Body.AsSpan(offset), section.SampleCount, usesPcmCorrections);

        if (loopStart > section.SampleCount)
            throw new FormatException("BNKl MicroTalk loop start exceeds the declared sample count.", offset, 1, bank.Body.Length - offset);
        var loopOffset = checked(offset + (int)loopRelativeOffset);
        if ((uint)loopOffset >= (uint)bank.Body.Length)
            throw new FormatException("BNKl MicroTalk loop offset is outside the bank body.", loopOffset, 1, bank.Body.Length);
        var output = new short[section.SampleCount];
        Decode(bank.Body.AsSpan(offset), loopStart, usesPcmCorrections).CopyTo(output, 0);
        Decode(bank.Body.AsSpan(loopOffset), section.SampleCount - loopStart, usesPcmCorrections).CopyTo(output, loopStart);
        return output;
    }

    public static short[] Decode(ReadOnlySpan<byte> encoded, int sampleCount, bool hasPcmCorrectionBlocks)
    {
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (sampleCount == 0)
            return [];
        if (encoded.IsEmpty)
            throw new FormatException("EA MicroTalk payload is empty.", 0, 1, 0);

        var decoder = new Decoder(encoded, hasPcmCorrectionBlocks);
        var output = new short[sampleCount];
        var written = 0;
        while (written < output.Length)
        {
            decoder.DecodeFrame();
            var count = Math.Min(SamplesPerFrame, output.Length - written);
            for (var index = 0; index < count; index++)
            {
                var value = decoder.Samples[index];
                var rounded = (int)(value >= 0 ? value + 0.5f : value - 0.5f);
                output[written++] = (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
            }
        }
        return output;
    }

    public static byte[] CreatePcm16Wave(IReadOnlyList<short> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var dataSize = checked(samples.Count * sizeof(short));
        var wave = new byte[checked(44 + dataSize)];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), checked((uint)(wave.Length - 8)));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), checked((uint)(sampleRate * sizeof(short))));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), checked((uint)dataSize));
        for (var index = 0; index < samples.Count; index++)
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + index * 2), samples[index]);
        return wave;
    }

    private sealed class Decoder
    {
        private readonly byte[] _encoded;
        private readonly bool _hasPcmCorrectionBlocks;
        private readonly float[] _fixedGains = new float[64];
        private readonly float[] _reflectionData = new float[12];
        private readonly float[] _synthesisHistory = new float[12];
        private readonly float[] _subframes = new float[324 + SamplesPerFrame];
        private int _byteOffset;
        private uint _bitsValue;
        private int _bitsCount;
        private bool _parsedHeader;
        private bool _reducedBandwidth;
        private int _multipulseThreshold;

        public Decoder(ReadOnlySpan<byte> encoded, bool hasPcmCorrectionBlocks)
        {
            _encoded = encoded.ToArray();
            _hasPcmCorrectionBlocks = hasPcmCorrectionBlocks;
        }

        public ReadOnlySpan<float> Samples => _subframes.AsSpan(324, SamplesPerFrame);

        public void DecodeFrame()
        {
            var pcmDataPresent = _hasPcmCorrectionBlocks && ReadByte() == 0xee;
            DecodeFrameMain();
            if (!_hasPcmCorrectionBlocks)
                return;

            // The bit reader has prefetched the byte at which the optional PCM block starts.
            _byteOffset--;
            _bitsCount = 0;
            if (!pcmDataPresent)
                return;
            var offset = ReadInt16BigEndian();
            var count = ReadInt16BigEndian();
            if (offset < 0 || offset > SamplesPerFrame || count < 0 || count > SamplesPerFrame - offset)
                return; // EA/vgmstream retain the synthesized frame when a trailing correction header is invalid.
            for (var index = 0; index < count; index++)
                _subframes[324 + offset + index] = ReadInt16BigEndian();
        }

        private void DecodeFrameMain()
        {
            Span<float> excitation = stackalloc float[5 + 108 + 5];
            Span<float> reflectionDelta = stackalloc float[12];
            InitializeBits();
            if (!_parsedHeader)
            {
                ParseHeader();
                _parsedHeader = true;
            }

            var useMultipulse = false;
            for (var index = 0; index < 12; index++)
            {
                int coefficientIndex;
                if (index == 0)
                {
                    coefficientIndex = ReadBits(6);
                    useMultipulse = coefficientIndex < _multipulseThreshold;
                }
                else if (index < 4)
                {
                    coefficientIndex = ReadBits(6);
                }
                else
                {
                    coefficientIndex = 16 + ReadBits(5);
                }
                reflectionDelta[index] = (ReflectionCoefficients[coefficientIndex] - _reflectionData[index]) * 0.25f;
            }

            for (var subframe = 0; subframe < 4; subframe++)
            {
                var pitchLag = ReadBits(8);
                var pitchGain = ReadBits(4) / 15.0f;
                var fixedGain = _fixedGains[ReadBits(6)];
                if (!_reducedBandwidth)
                {
                    DecodeExcitation(useMultipulse, excitation, 5, 1);
                }
                else
                {
                    var alignment = ReadBits(1);
                    var zeroFlag = ReadBits(1) != 0;
                    DecodeExcitation(useMultipulse, excitation, 5 + alignment, 2);
                    if (zeroFlag)
                    {
                        for (var index = 0; index < 54; index++)
                            excitation[5 + 1 - alignment + 2 * index] = 0;
                    }
                    else
                    {
                        excitation[..5].Clear();
                        excitation[(5 + 108)..].Clear();
                        InterpolateRest(excitation, 5 + 1 - alignment);
                        fixedGain *= 0.5f;
                    }
                }

                for (var index = 0; index < 108; index++)
                {
                    var adaptiveIndex = Math.Max(0, 108 * subframe + 216 - pitchLag + index);
                    var fixedValue = fixedGain * excitation[5 + index];
                    var adaptiveValue = pitchGain * _subframes[adaptiveIndex];
                    _subframes[324 + 108 * subframe + index] = fixedValue + adaptiveValue;
                }
            }

            Array.Copy(_subframes, 324 + 108, _subframes, 0, 324);
            for (var subframe = 0; subframe < 4; subframe++)
            {
                for (var index = 0; index < 12; index++)
                    _reflectionData[index] += reflectionDelta[index];
                SynthesisFilter(12 * subframe, subframe < 3 ? 1 : 33);
            }
        }

        private void ParseHeader()
        {
            _reducedBandwidth = ReadBits(1) != 0;
            var baseThreshold = ReadBits(4);
            var baseGain = ReadBits(4);
            var baseMultiplier = ReadBits(6);
            _multipulseThreshold = 32 - baseThreshold;
            _fixedGains[0] = 8.0f * (1 + baseGain);
            var multiplier = 1.04f + baseMultiplier * 0.001f;
            for (var index = 1; index < _fixedGains.Length; index++)
                _fixedGains[index] = _fixedGains[index - 1] * multiplier;
        }

        private void DecodeExcitation(bool useMultipulse, Span<float> output, int offset, int stride)
        {
            var index = 0;
            if (useMultipulse)
            {
                var model = 0;
                while (index < 108)
                {
                    var commandIndex = Codebooks[model][PeekBits(8)];
                    var command = Commands[commandIndex];
                    model = command.NextModel;
                    ConsumeBits(command.CodeSize);
                    if (commandIndex > 3)
                    {
                        output[offset + index] = command.PulseValue;
                        index += stride;
                    }
                    else if (commandIndex > 1)
                    {
                        var count = 7 + ReadBits(6);
                        if (index + count * stride > 108)
                            count = (108 - index) / stride;
                        while (count-- > 0)
                        {
                            output[offset + index] = 0;
                            index += stride;
                        }
                    }
                    else
                    {
                        var value = 7;
                        while (ReadBits(1) != 0)
                            value++;
                        if (ReadBits(1) == 0)
                            value = -value;
                        output[offset + index] = value;
                        index += stride;
                    }
                }
                return;
            }

            while (index < 108)
            {
                var code = PeekBits(2);
                var bits = (code & 1) == 0 ? 1 : 2;
                var value = code switch { 1 => -2.0f, 3 => 2.0f, _ => 0.0f };
                ConsumeBits(bits);
                output[offset + index] = value;
                index += stride;
            }
        }

        private void SynthesisFilter(int sampleOffset, int blocks)
        {
            Span<float> linearPrediction = stackalloc float[12];
            ReflectionToLinearPrediction(linearPrediction);
            var output = 324 + sampleOffset;
            for (var block = 0; block < blocks; block++)
            {
                for (var sample = 0; sample < 12; sample++)
                {
                    var value = _subframes[output];
                    var coefficient = 0;
                    for (; coefficient < sample; coefficient++)
                        value += linearPrediction[coefficient] * _synthesisHistory[coefficient - sample + 12];
                    for (; coefficient < 12; coefficient++)
                        value += linearPrediction[coefficient] * _synthesisHistory[coefficient - sample];
                    _synthesisHistory[11 - sample] = value;
                    _subframes[output++] = value;
                }
            }
        }

        private void ReflectionToLinearPrediction(Span<float> output)
        {
            Span<float> first = stackalloc float[12];
            Span<float> second = stackalloc float[12];
            for (var index = 10; index >= 0; index--)
                second[index + 1] = _reflectionData[index];
            second[0] = 1;
            for (var index = 0; index < 12; index++)
            {
                var value = -(_reflectionData[11] * second[11]);
                for (var coefficient = 10; coefficient >= 0; coefficient--)
                {
                    value -= _reflectionData[coefficient] * second[coefficient];
                    second[coefficient + 1] = value * _reflectionData[coefficient] + second[coefficient];
                }
                second[0] = value;
                first[index] = value;
                for (var coefficient = 0; coefficient < index; coefficient++)
                    value -= first[index - 1 - coefficient] * output[coefficient];
                output[index] = value;
            }
        }

        private static void InterpolateRest(Span<float> excitation, int offset)
        {
            for (var index = 0; index < 108; index += 2)
            {
                var first = (excitation[offset + index - 5] + excitation[offset + index + 5]) * 0.01803268f;
                var second = (excitation[offset + index - 3] + excitation[offset + index + 3]) * 0.11459156f;
                var third = (excitation[offset + index - 1] + excitation[offset + index + 1]) * 0.59738597f;
                excitation[offset + index] = first - second + third;
            }
        }

        private byte ReadByte()
        {
            if ((uint)_byteOffset >= (uint)_encoded.Length)
            {
                // The EA decoder returns zero after its stream callback reaches EOF. Output
                // remains bounded by the header's validated sample count, so this also models
                // the codec's intentional final-frame over-read without an unbounded decode.
                _byteOffset++;
                return 0;
            }
            return _encoded[_byteOffset++];
        }

        private short ReadInt16BigEndian() => (short)((ReadByte() << 8) | ReadByte());

        private void InitializeBits()
        {
            if (_bitsCount == 0)
            {
                _bitsValue = ReadByte();
                _bitsCount = 8;
            }
        }

        private int PeekBits(int count) => (int)(_bitsValue & ((1u << count) - 1));

        private int ReadBits(int count)
        {
            var value = PeekBits(count);
            _bitsValue >>= count;
            _bitsCount -= count;
            if (_bitsCount < 8)
            {
                _bitsValue |= (uint)ReadByte() << _bitsCount;
                _bitsCount += 8;
            }
            return value;
        }

        private void ConsumeBits(int count) => ReadBits(count);
    }

    private readonly record struct Command(int NextModel, int CodeSize, float PulseValue);
}
