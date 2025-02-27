﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cosmos.Common;
using Cosmos.Core;
using Cosmos.Debug.Kernel;

namespace Cosmos.HAL.BlockDevice
{
	// We need this class as a fallback for debugging/boot/debugstub in the future even when
	// we create DMA and other method support
	//
	// AtaPio is also used as the detection class to detect drives. If another ATA mode is used,
	// the instnace of AtaPio should be discarded in favour of another ATA class. AtaPio is used
	// to do this as capabilities can also be detected, but all ATA devices must support PIO
	// and thus it can also be used to read the partition table and perform other tasks before
	// initializing another ATA class in favour of AtaPio
	public class ATA_PIO : Ata
	{
        #region Properties
        public override BlockDeviceType Type
        {
            get
            {
                if (DriveType == SpecLevel.ATAPI)
                {
                    return BlockDeviceType.RemovableCD;
                }
                return BlockDeviceType.HardDrive;
            }
        }
        protected Core.IOGroup.ATA IO;

		protected SpecLevel mDriveType = SpecLevel.Null;
		public SpecLevel DriveType
		{
			get { return mDriveType; }
		}

		protected string mSerialNo;
		public string SerialNo
		{
			get { return mSerialNo; }
		}

		protected string mFirmwareRev;
		public string FirmwareRev
		{
			get { return mFirmwareRev; }
		}

		protected string mModelNo;
		public string ModelNo
		{
			get { return mModelNo; }
		}
        #endregion
        #region Enums
        [Flags]
		public enum Status : byte
		{
			None = 0x00,
			Busy = 0x80,
			ATA_SR_DRD = 0x40,
			ATA_SR_DF = 0x20,
			ATA_SR_DSC = 0x10,
			DRQ = 0x08,
			ATA_SR_COR = 0x04,
			ATA_SR_IDX = 0x02,
			Error = 0x01
		};

		[Flags]
		enum Error : byte
		{
			ATA_ER_BBK = 0x80,
			ATA_ER_UNC = 0x40,
			ATA_ER_MC = 0x20,
			ATA_ER_IDNF = 0x10,
			ATA_ER_MCR = 0x08,
			ATA_ER_ABRT = 0x04,
			ATA_ER_TK0NF = 0x02,
			ATA_ER_AMNF = 0x01
		};

		[Flags]
		public enum DvcSelVal : byte
		{
			// Bits 0-3: Head Number for CHS.
			// Bit 4: Slave Bit. (0: Selecting Master Drive, 1: Selecting Slave Drive).
			Slave = 0x10,
			//* Bit 6: LBA (0: CHS, 1: LBA).
			LBA = 0x40,
			//* Bit 5: Obsolete and isn't used, but should be set.
			//* Bit 7: Obsolete and isn't used, but should be set.
			Default = 0xA0
		};

		public enum Cmd : byte
		{
			ReadPio = 0x20,
			ReadPioExt = 0x24,
			ReadDma = 0xC8,
			ReadDmaExt = 0x25,
            ReadNativeMaxAdressExt =0x27,
			WritePio = 0x30,
			WritePioExt = 0x34,
			WriteDma = 0xCA,
			WriteDmaExt = 0x35,
			CacheFlush = 0xE7,
			CacheFlushExt = 0xEA,
			Packet = 0xA0,
			IdentifyPacket = 0xA1,
			Identify = 0xEC,
			Read = 0xA8,
			Eject = 0x1B,
            ReadNativeMaxAdress = 0xF8
        }

		public enum Ident : byte
		{
			DEVICETYPE = 0,
			CYLINDERS = 2,
			HEADS = 6,
			SECTORS = 12,
			SERIAL = 20,
			MODEL = 54,
			CAPABILITIES = 98,
			FIELDVALID = 106,
			MAX_LBA = 120,
			COMMANDSETS = 164,
			MAX_LBA_EXT = 200
		}

		// TODO: Null is used for unknown, none, etc. Nullable type would be better, but we dont have support for them yet
		public enum SpecLevel
		{
			Null,
			ATA,
			ATAPI
		}
		#endregion

	    internal static Debugger mDebugger = new Debugger("HAL", "AtaPio");

        /// <summary>
        /// Internal Debugger method
        /// </summary>
        /// <param name="message"></param>
	    private static void Debug(string message)
	    {
	        mDebugger.Send("AtaPio debug: " + message);
	    }

        /// <summary>
        /// Internal Debugger method
        /// </summary>
        /// <param name="message"></param>
        /// <param name="number"></param>
	    private static void DebugHex(string message, uint number)
	    {
	        Debug(message);
	        mDebugger.SendNumber(number);
	    }

        /// <summary>
        /// ATA_PIO mode constructor
        /// </summary>
        /// <param name="aIO"></param>
        /// <param name="aControllerId"></param>
        /// <param name="aBusPosition"></param>
        public ATA_PIO(Core.IOGroup.ATA aIO, Ata.ControllerIdEnum aControllerId, Ata.BusPositionEnum aBusPosition)
		{
			IO = aIO;
			mControllerID = aControllerId;
			mBusPosition = aBusPosition;
			// Disable IRQs, we use polling currently
			IOPort.Write8(IO.Control, 0x02);

            mDriveType = DiscoverDrive();
			if (mDriveType != SpecLevel.Null)
			{
				InitDrive();
			}
		}

		// Directions:
		//#define      ATA_READ      0x00
		//#define      ATA_WRITE     0x01

        /// <summary>
        /// Does this drive even exist? Are there any drives out there in the wild ATA desert?
        /// </summary>
        /// <returns></returns>
		public SpecLevel DiscoverDrive()
		{
			SelectDrive(0);

			// Read status before sending command. If 0xFF, it's a floating
			// bus (nothing connected)
			if (IOPort.Read8(IO.Status) == 0xFF)
			{
				return SpecLevel.Null;
			}

			var xIdentifyStatus = SendCmd(Cmd.Identify, false);
			// No drive found, go to next
			if (xIdentifyStatus == Status.None)
			{
				return SpecLevel.Null;
			}
			else if ((xIdentifyStatus & Status.Error) != 0)
			{
				// Can look in Error port for more info
				// Device is not ATA
				// This is also triggered by ATAPI devices
				int xTypeId = IOPort.Read8(IO.LBA2) << 8 | IOPort.Read8(IO.LBA1);
				if (xTypeId == 0xEB14 || xTypeId == 0x9669)
				{
					return SpecLevel.ATAPI;
				}
				else
				{
					// Unknown type. Might not be a device.
					return SpecLevel.Null;
				}
			}
			else if ((xIdentifyStatus & Status.DRQ) == 0)
			{
				// Error
				return SpecLevel.Null;
			}
			return SpecLevel.ATA;
		}

		// ATA requires a wait of 400 nanoseconds.
		// Read the Status register FIVE TIMES, and only pay attention to the value
		// returned by the last one -- after selecting a new master or slave device. The point being that
		// you can assume an IO port read takes approximately 100ns, so doing the first four creates a 400ns
		// delay -- which allows the drive time to push the correct voltages onto the bus.
		// Since we read status again later, we wait by reading it 4 times.
		protected void Wait()
		{
            IOPort.Wait();
            IOPort.Wait();
            IOPort.Wait();
            IOPort.Wait();
		}

        /// <summary>
        /// Selects the drive
        /// </summary>
        /// <param name="aLbaHigh4"></param>
		public void SelectDrive(byte aLbaHigh4)
		{
            IOPort.Write8(IO.DeviceSelect, (byte)((byte)(DvcSelVal.Default | DvcSelVal.LBA | (mBusPosition == BusPositionEnum.Slave ? DvcSelVal.Slave : 0)) | aLbaHigh4));
            Wait();
		}

        /// <summary>
        /// Returns the status of the sent command, and always throws an exception
        /// </summary>
        /// <param name="aCmd"></param>
        /// <returns></returns>
		public Status SendCmd(Cmd aCmd)
		{
			return SendCmd(aCmd, true);
		}

        /// <summary>
        /// Returns the status of a sent command, and throws an exception depending on the status
        /// </summary>
        /// <param name="aCmd"></param>
        /// <param name="aThrowOnError"></param>
        /// <returns></returns>
		public Status SendCmd(Cmd aCmd, bool aThrowOnError)
		{
            IOPort.Write8(IO.Command, (byte)aCmd);
			Status xStatus;
			do
			{
				Wait();
				xStatus = (Status)IOPort.Read8(IO.Status);
			} while ((xStatus & Status.Busy) != 0);

			// Error occurred
			if (aThrowOnError && (xStatus & Status.Error) != 0)
			{
				// TODO: Read error port
			    Debug("ATA Error in SendCmd.");
			    DebugHex("Cmd", (byte)aCmd);
			    DebugHex("Status", (byte)xStatus);
				throw new Exception("ATA Error");
			}
			return xStatus;
		}

        /// <summary>
        ///
        /// </summary>
        /// <param name="aBuffer"></param>
        /// <param name="aIndexStart"></param>
        /// <param name="aStringLength"></param>
        /// <returns></returns>
		protected string GetString(ushort[] aBuffer, int aIndexStart, int aStringLength)
		{
            //Convert ushort[] to byte[]
            byte[] array = new byte[aStringLength];
            int counter = 0;
            for (int i = aIndexStart; i < aIndexStart + aStringLength / 2; i++)
            {
                var item = aBuffer[i];
                var bytes = BitConverter.GetBytes(item);
                array[counter++] = bytes[1];
                array[counter++] = bytes[0];
            }
            //Convert byte[] to string
            return Encoding.ASCII.GetString(array);
        }

		public bool LBA48Bit;

        /// <summary>
        /// Initializes the ATA drive. This includes ATAPI devices too AFAIK
        /// </summary>
		protected void InitDrive()
		{
			if (mDriveType == SpecLevel.ATA)
			{
				SendCmd(Cmd.Identify);
			}
			else
			{
				SendCmd(Cmd.IdentifyPacket);
			}
			//IDENTIFY command
			// Not sure if all this is needed, its different than documented elsewhere but might not be bad
			// to add code to do all listed here:
			//
			//To use the IDENTIFY command, select a target drive by sending 0xA0 for the master drive, or 0xB0 for the slave, to the "drive select" IO port. On the Primary bus, this would be port 0x1F6.
			// Then set the Sectorcount, LBAlo, LBAmid, and LBAhi IO ports to 0 (port 0x1F2 to 0x1F5).
			// Then send the IDENTIFY command (0xEC) to the Command IO port (0x1F7).
			// Then read the Status port (0x1F7) again. If the value read is 0, the drive does not exist. For any other value: poll the Status port (0x1F7) until bit 7 (BSY, value = 0x80) clears.
			// Because of some ATAPI drives that do not follow spec, at this point you need to check the LBAmid and LBAhi ports (0x1F4 and 0x1F5) to see if they are non-zero.
			// If so, the drive is not ATA, and you should stop polling. Otherwise, continue polling one of the Status ports until bit 3 (DRQ, value = 8) sets, or until bit 0 (ERR, value = 1) sets.
			// At that point, if ERR is clear, the data is ready to read from the Data port (0x1F0). Read 256 words, and store them.

			// Read Identification Space of the Device
			var xBuff = new ushort[256];
            IOPort.Read16(IO.Data, xBuff);
			mSerialNo = GetString(xBuff, 10, 20);
			mFirmwareRev = GetString(xBuff, 23, 8);
			mModelNo = GetString(xBuff, 27, 40);

			//Words (61:60) shall contain the value one greater than the total number of user-addressable
			//sectors in 28-bit addressing and shall not exceed 0FFFFFFFh.  The content of words (61:60) shall
			//be greater than or equal to one and less than or equal to 268,435,455.
			// We need 28 bit addressing - small drives on VMWare and possibly other cases are 28 bit
			mBlockCount = ((uint)xBuff[61] << 16 | xBuff[60]) - 1;

			//Words (103:100) shall contain the value one greater than the total number of user-addressable
			//sectors in 48-bit addressing and shall not exceed 0000FFFFFFFFFFFFh.
			//The contents of words (61:60) and (103:100) shall not be used to determine if 48-bit addressing is
			//supported. IDENTIFY DEVICE bit 10 word 83 indicates support for 48-bit addressing.
			LBA48Bit = (xBuff[83] & 0x40) != 0;
			// LBA48Bit = mBlockCount > 0x0FFFFFFF;
			if (LBA48Bit)
			{
				mBlockCount = ((ulong)xBuff[102] << 32 | (ulong)xBuff[101] << 16 | (ulong)xBuff[100]) - 1;
			}
		}

        /// <summary>
        /// SelectSector selects the specified starting sector and the following sectors.
        /// </summary>
        /// <param name="aSectorNo"></param>
        /// <param name="aSectorCount"></param>
		protected void SelectSector(ulong aSectorNo, ulong aSectorCount)
		{
			CheckBlockNo(aSectorNo, aSectorCount);
			//TODO: Check for 48 bit sectorno mode and select 48 bits
			SelectDrive((byte)(aSectorNo >> 24));
			if (LBA48Bit)
			{
                IOPort.Write16(IO.SectorCount, (ushort)aSectorCount);
                IOPort.Write8(IO.LBA0, (byte)(aSectorNo >> 24));
                IOPort.Write8(IO.LBA0, (byte)aSectorNo);
                IOPort.Write8(IO.LBA1, (byte)(aSectorNo >> 32));
                IOPort.Write8(IO.LBA1, (byte)(aSectorNo >> 8));
                IOPort.Write8(IO.LBA2, (byte)(aSectorNo >> 40));
                IOPort.Write8(IO.LBA2, (byte)(aSectorNo >> 16));
			}
			else
			{
				// Number of sectors to read
                IOPort.Write8(IO.SectorCount, (byte)aSectorCount);
                IOPort.Write8(IO.LBA0, (byte)aSectorNo);
                IOPort.Write8(IO.LBA1, (byte)(aSectorNo >> 8));
                IOPort.Write8(IO.LBA2, (byte)(aSectorNo >> 16));
				//IO.LBA0.Byte = (byte)(aSectorNo & 0xFF);
				//IO.LBA1.Byte = (byte)((aSectorNo & 0xFF00) >> 8);
				//IO.LBA2.Byte = (byte)((aSectorNo & 0xFF0000) >> 16);
			}
			//TODO LBA3  ...
		}

        /// <summary>
        /// Reads the data from a specific starting block using aBlockCount (how many blocks to be read), and fills the specified byte[] with the block.
        /// </summary>
        /// <param name="aBlockNo"></param>
        /// <param name="aBlockCount"></param>
        /// <param name="aData"></param>
		public override void ReadBlock(ulong aBlockNo, ulong aBlockCount, ref byte[] aData)
		{
			CheckDataSize(aData, aBlockCount);
			SelectSector(aBlockNo, aBlockCount);
			SendCmd(LBA48Bit ? Cmd.ReadPioExt : Cmd.ReadPio);
            IOPort.Read8(IO.Data, aData);
		}

        /// <summary>
        /// Writes the specific block of data using the starting block,
        /// and the count of how many blocks to be written.
        /// </summary>
        /// <param name="aBlockNo"></param>
        /// <param name="aBlockCount"></param>
        /// <param name="aData"></param>
		public override void WriteBlock(ulong aBlockNo, ulong aBlockCount, ref byte[] aData)
		{
            CheckDataSize(aData, aBlockCount);
            SelectSector(aBlockNo, aBlockCount);
			SendCmd(LBA48Bit ? Cmd.WritePioExt : Cmd.WritePio);

            ushort xValue;

			for (long i = 0; i < aData.Length / 2; i++)
			{
				xValue = (ushort)((aData[i * 2 + 1] << 8) | aData[i * 2]);
                IOPort.Write16(IO.Data, xValue);
				Wait();
				// There must be a tiny delay between each OUTSW output word. A jmp $+2 size of delay.
				// But that delay is cpu specific? so how long of a delay?
			}

			SendCmd(Cmd.CacheFlush);
		}

        /// <summary>
        ///
        /// </summary>
        /// <returns></returns>
	    public override string ToString()
	    {
	        return "AtaPio";
	    }
	}
}
