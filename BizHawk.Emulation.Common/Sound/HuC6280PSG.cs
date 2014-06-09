﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using BizHawk.Common;

namespace BizHawk.Emulation.Common.Components
{
	// Emulates PSG audio unit of a PC Engine / Turbografx-16 / SuperGrafx.
	// It is embedded on the CPU and doesn't have its own part number. None the less, it is emulated separately from the 6280 CPU.

	public sealed class HuC6280PSG : ISoundProvider
	{
		public class PSGChannel
		{
			public ushort Frequency;
			public byte Panning;
			public byte Volume;
			public bool Enabled;
			public bool NoiseChannel;
			public bool DDA;
			public ushort NoiseFreq;
			public short DDAValue;
			public short[] Wave = new short[32];
			public float SampleOffset;
		}

		public PSGChannel[] Channels = new PSGChannel[8];

		public bool[] UserMute = new bool[8];

		public byte VoiceLatch;
		private byte WaveTableWriteOffset;

		private readonly Queue<QueuedCommand> commands = new Queue<QueuedCommand>(256);
		private long frameStartTime, frameStopTime;

		const int SampleRate = 44100;
		const int PsgBase = 3580000;
		static readonly byte[] LogScale = { 0, 0, 10, 10, 13, 13, 16, 16, 20, 20, 26, 26, 32, 32, 40, 40, 51, 51, 64, 64, 81, 81, 102, 102, 128, 128, 161, 161, 203, 203, 255, 255 };
		static readonly byte[] VolumeReductionTable = { 0x1F, 0x1D, 0x1B, 0x19, 0x17, 0x15, 0x13, 0x10, 0x0F, 0x0D, 0x0B, 0x09, 0x07, 0x05, 0x03, 0x00 };

		public byte MainVolumeLeft;
		public byte MainVolumeRight;
		public int MaxVolume { get; set; }

		public HuC6280PSG()
		{
			MaxVolume = short.MaxValue;
			Waves.InitWaves();
			for (int i = 0; i < 8; i++)
			{
				Channels[i] = new PSGChannel();
			}
		}

		public void BeginFrame(long cycles)
		{
			while (commands.Count > 0)
			{
				var cmd = commands.Dequeue();
				WritePSGImmediate(cmd.Register, cmd.Value);
			}
			frameStartTime = cycles;
		}

		public void EndFrame(long cycles)
		{
			frameStopTime = cycles;
		}

		public void WritePSG(byte register, byte value, long cycles)
		{
			commands.Enqueue(new QueuedCommand { Register = register, Value = value, Time = cycles - frameStartTime });
		}

		public void WritePSGImmediate(int register, byte value)
		{
			register &= 0x0F;
			switch (register)
			{
				case 0: // Set Voice Latch
					VoiceLatch = (byte)(value & 7);
					break;
				case 1: // Global Volume select;
					MainVolumeLeft = (byte)((value >> 4) & 0x0F);
					MainVolumeRight = (byte)(value & 0x0F);
					break;
				case 2: // Frequency LSB
					Channels[VoiceLatch].Frequency &= 0xFF00;
					Channels[VoiceLatch].Frequency |= value;
					break;
				case 3: // Frequency MSB
					Channels[VoiceLatch].Frequency &= 0x00FF;
					Channels[VoiceLatch].Frequency |= (ushort)(value << 8);
					Channels[VoiceLatch].Frequency &= 0x0FFF;
					break;
				case 4: // Voice Volume
					Channels[VoiceLatch].Volume = (byte)(value & 0x1F);
					Channels[VoiceLatch].Enabled = (value & 0x80) != 0;
					Channels[VoiceLatch].DDA = (value & 0x40) != 0;
					if (Channels[VoiceLatch].Enabled == false && Channels[VoiceLatch].DDA)
					{
						//for the soudn debugger, this might be a useful indication that a new note has begun.. but not for sure
						WaveTableWriteOffset = 0;
					}
					break;
				case 5: // Panning
					Channels[VoiceLatch].Panning = value;
					break;
				case 6: // Wave data
					if (Channels[VoiceLatch].DDA == false)
					{
						Channels[VoiceLatch].Wave[WaveTableWriteOffset++] = (short)((value * 2047) - 32767);
						WaveTableWriteOffset &= 31;
					}
					else
					{
						Channels[VoiceLatch].DDAValue = (short)((value * 2047) - 32767);
					}
					break;
				case 7: // Noise
					Channels[VoiceLatch].NoiseChannel = ((value & 0x80) != 0) && VoiceLatch >= 4;
					if ((value & 0x1F) == 0x1F)
						value &= 0xFE;
					Channels[VoiceLatch].NoiseFreq = (ushort)(PsgBase / (64 * (0x1F - (value & 0x1F))));
					break;
				case 8: // LFO
					// TODO: implement LFO
					break;
				case 9: // LFO Control
					if ((value & 0x80) == 0 && (value & 3) != 0)
					{
						Console.WriteLine("****************      LFO ON !!!!!!!!!!       *****************");
						Channels[1].Enabled = false;
					}
					else
					{
						Channels[1].Enabled = true;
					}
					break;
			}
		}

		public void DiscardSamples() { }
		public void GetSamples(short[] samples)
		{
			int elapsedCycles = (int)(frameStopTime - frameStartTime);
			int start = 0;
			while (commands.Count > 0)
			{
				var cmd = commands.Dequeue();
				int pos = (int)((cmd.Time * samples.Length) / elapsedCycles) & ~1;
				MixSamples(samples, start, pos - start);
				start = pos;
				WritePSGImmediate(cmd.Register, cmd.Value);
			}
			MixSamples(samples, start, samples.Length - start);
		}

		void MixSamples(short[] samples, int start, int len)
		{
			for (int i = 0; i < 6; i++)
			{
				if (UserMute[i]) continue;
				MixChannel(samples, start, len, Channels[i]);
			}
		}

		void MixChannel(short[] samples, int start, int len, PSGChannel channel)
		{
			if (channel.Enabled == false) return;
			if (channel.DDA == false && channel.Volume == 0) return;

			short[] wave = channel.Wave;
			int freq;

			if (channel.NoiseChannel)
			{
				wave = Waves.NoiseWave;
				freq = channel.NoiseFreq;
			}
			else if (channel.DDA)
			{
				freq = 0;
			}
			else
			{
				if (channel.Frequency <= 1) return;
				freq = PsgBase / (32 * ((int)channel.Frequency));
			}

			int globalPanFactorLeft = VolumeReductionTable[MainVolumeLeft];
			int globalPanFactorRight = VolumeReductionTable[MainVolumeRight];
			int channelPanFactorLeft = VolumeReductionTable[channel.Panning >> 4];
			int channelPanFactorRight = VolumeReductionTable[channel.Panning & 0xF];
			int channelVolumeFactor = 0x1f - channel.Volume;

			int volumeLeft = 0x1F - globalPanFactorLeft - channelPanFactorLeft - channelVolumeFactor;
			if (volumeLeft < 0)
				volumeLeft = 0;

			int volumeRight = 0x1F - globalPanFactorRight - channelPanFactorRight - channelVolumeFactor;
			if (volumeRight < 0)
				volumeRight = 0;

			float adjustedWaveLengthInSamples = SampleRate / (channel.NoiseChannel ? freq / (float)(channel.Wave.Length * 128) : freq);
			float moveThroughWaveRate = wave.Length / adjustedWaveLengthInSamples;

			int end = start + len;
			for (int i = start; i < end; )
			{
				channel.SampleOffset %= wave.Length;
				short value = channel.DDA ? channel.DDAValue : wave[(int)channel.SampleOffset];

				samples[i++] += (short)(value * LogScale[volumeLeft] / 255f / 6f * MaxVolume / short.MaxValue);
				samples[i++] += (short)(value * LogScale[volumeRight] / 255f / 6f * MaxVolume / short.MaxValue);

				channel.SampleOffset += moveThroughWaveRate;
				channel.SampleOffset %= wave.Length;
			}
		}

		public void SyncState(Serializer ser)
		{
			ser.BeginSection("PSG");
			ser.Sync("MainVolumeLeft", ref MainVolumeLeft);
			ser.Sync("MainVolumeRight", ref MainVolumeRight);
			ser.Sync("VoiceLatch", ref VoiceLatch);
			ser.Sync("WaveTableWriteOffset", ref WaveTableWriteOffset);

			for (int i = 0; i < 6; i++)
			{
				ser.BeginSection("Channel"+i);
				ser.Sync("Frequency", ref Channels[i].Frequency);
				ser.Sync("Panning", ref Channels[i].Panning);
				ser.Sync("Volume", ref Channels[i].Volume);
				ser.Sync("Enabled", ref Channels[i].Enabled);
				if (i.In(4, 5))
				{
					ser.Sync("NoiseChannel", ref Channels[i].NoiseChannel);
					ser.Sync("NoiseFreq", ref Channels[i].NoiseFreq);
				}

				ser.Sync("DDA", ref Channels[i].DDA);
				ser.Sync("DDAValue", ref Channels[i].DDAValue);
				ser.Sync("SampleOffset", ref Channels[i].SampleOffset);
				ser.Sync("Wave", ref Channels[i].Wave, false);
				ser.EndSection();
			}

			ser.EndSection();
		}

		class QueuedCommand
		{
			public byte Register;
			public byte Value;
			public long Time;
		}
	}
}