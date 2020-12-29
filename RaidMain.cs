using System;
using System.Diagnostics;
using System.Collections;
using System.IO;
using CodeProject; // Kewl!

namespace RAIDFile
{
	class RaidMain
	{
		const int BLOCKSIZE = 1024;

		// lOp ^= rop for an entire array
		static void xorArray(byte []lop, byte []rop, int iPos)
		{
			for (int i = 0; i < lop.Length; i++)
				lop[i] ^= rop[i + iPos];
		}

		static byte[] calcParity(byte []data)
		{
			byte []parity = new byte[BLOCKSIZE];
			Array.Copy(data, 0, parity, 0, BLOCKSIZE);
			for (int i = 1; i < data.Length / BLOCKSIZE; i ++)
				xorArray(parity, data, i * BLOCKSIZE);
			return parity;
		}

		static byte []readData(Stream inp, int elem, long offset)
		{
			byte []data = new byte[BLOCKSIZE * elem];
			Array.Clear(data, 0, data.Length);
			long iSize = inp.Length;
			long iStep = iSize / (BLOCKSIZE * elem);
			for (int i = 0; i < elem; i++)
			{
				long iSeek = (iStep * BLOCKSIZE * i) + offset;
				if (iSeek < iSize)
				{
					inp.Seek(iSeek, SeekOrigin.Begin);
					inp.Read(data, i * BLOCKSIZE, BLOCKSIZE);
				}
			}
			return data;
		}

		static void createParity(Stream inp, Stream outp, int elem)
		{
			long iSize = inp.Length;
			for (long i = 0; i < iSize / elem; i += BLOCKSIZE)
			{
				byte []data = readData(inp, elem, i);
				byte []parity = calcParity(data);
				outp.Write(parity, 0, parity.Length);
			}
		}

		static void createCRC32(Stream inp, Stream outp)
		{
			byte []data = new byte[BLOCKSIZE];
			int iRead = inp.Read(data, 0, data.Length);
			while (iRead != 0)
			{
				CRC32 c = new CRC32();
				byte[] CRC = c.ComputeHash(data, 0, iRead);
				outp.Write(CRC, 0, CRC.Length);
				iRead = inp.Read(data, 0, data.Length);
			}
		}

		static string changeExtension(string name, string newExtension)
		{
			return name.Substring(0, name.LastIndexOf('.')) + newExtension;
		}

		static void protectFile(string inpFileName, int elem)
		{
			string raidFileName = changeExtension(inpFileName, ".raid");
			string crcFileName = changeExtension(inpFileName, ".crc32");

			using (FileStream inp = new FileStream(inpFileName, FileMode.Open, FileAccess.Read))
			{
				using (FileStream outp = new FileStream(raidFileName, FileMode.Create, FileAccess.Write))
				{
					createParity(inp, outp, elem);
				}
				inp.Seek(0, SeekOrigin.Begin);
				using (FileStream crc = new FileStream(crcFileName, FileMode.Create, FileAccess.Write))
				{
					createCRC32(inp, crc);
				}
			}
		}

		static uint toUint(byte []b)
		{
			return ((uint)b[0] << 24) | 
				   ((uint)b[1] << 16) | 
				   ((uint)b[2] << 8)  |
				   ((uint)b[3]);
		}

		static long[] damagedBlocks(string inpFileName)
		{
			string crcFileName = changeExtension(inpFileName, ".crc32");

			long []retval;

			using (FileStream inp = new FileStream(inpFileName, FileMode.Open, FileAccess.Read))
			{
				using (FileStream crc = new FileStream(crcFileName, FileMode.Open, FileAccess.Read))
				{
					ArrayList damaged = new ArrayList();
					byte []data = new byte[BLOCKSIZE];
					byte []check = new byte[4];
					int iRead = inp.Read(data, 0, data.Length);
					long iBlock = 0;
					while (iRead != 0)
					{
						CRC32 c = new CRC32();
						byte[] CRC = c.ComputeHash(data, 0, iRead);
						crc.Read(check, 0, check.Length);
						if (toUint(CRC) != toUint(check))
							damaged.Add(iBlock);
						iBlock++;
						iRead = inp.Read(data, 0, data.Length);
					}
					retval = new long[damaged.Count];
					damaged.CopyTo(retval);
				}
			}
			return retval;
		}

		static string createDamagedFile(string inpFileName, int elem, int nRandomChanges)
		{
			string damagedFileName = changeExtension(inpFileName, ".damaged");
			File.Copy(inpFileName, damagedFileName, true);
			Random r = new Random();
			using (FileStream outp = new FileStream(damagedFileName, FileMode.Open, FileAccess.ReadWrite))
			{
				for (int i = 0; i < nRandomChanges; i++)
				{
					double perc = r.NextDouble();
					long pos = (long)((double)outp.Length * perc);
					outp.Seek(pos, SeekOrigin.Begin);
					byte []bRand = new byte[100];
					r.NextBytes(bRand);
					outp.Write(bRand, 0, bRand.Length);
				}
				// Do not truncate file
				outp.Seek(0, SeekOrigin.End);
			}
			return damagedFileName;
		}

		static void recoverBlocks(string inpFileName, long[] dmBlocks, int elem)
		{
			string raidFileName = changeExtension(inpFileName, ".raid");

			using (FileStream raid = new FileStream(raidFileName, FileMode.Open, FileAccess.Read))
			{
				using (FileStream outp = new FileStream(inpFileName, FileMode.Open, FileAccess.ReadWrite))
				{
					for (int i = 0; i < dmBlocks.Length; i++)
					{
						// calculate the starting block 
						// of the group the damaged block
						// belongs to
						long nBlocks = outp.Length / (BLOCKSIZE * elem);
						long stBlock;
						if (nBlocks == 0)
						{
							nBlocks = 1;
							stBlock = 0;
						}
						else
							stBlock = dmBlocks[i] % nBlocks;
						// read the block group
						byte []data = readData(outp, elem, stBlock * BLOCKSIZE);
						// read the parity data
						byte []parity = new byte[BLOCKSIZE];
						raid.Seek(stBlock * BLOCKSIZE, SeekOrigin.Begin);
						raid.Read(parity, 0, parity.Length);
						// substitute the damaged block
						// data with parity data
						int dataBlock = (int)(dmBlocks[i] / nBlocks);
						Array.Copy(parity, 0, data, dataBlock * BLOCKSIZE, BLOCKSIZE);
						// XOR everything again
						byte []recoveredBlock = calcParity(data);
						// hopefully we recoverd the block, write it in the damaged file
						outp.Seek(BLOCKSIZE * dmBlocks[i], SeekOrigin.Begin);
						outp.Write(recoveredBlock, 0, recoveredBlock.Length);
					}
					// Do not truncate file
					outp.Seek(0, SeekOrigin.End);
				}
			}
		}

		static void Main(string[] args)
		{
			Console.WriteLine("Input file:");
			string fileName = Console.ReadLine();

			Console.WriteLine("Number of divisions (the .raid file will be approx. 1/X * file size):");
			int parityBlocks = Convert.ToInt32(Console.ReadLine());

			Console.WriteLine("Minimum number of file damages:");
			int fileDamages = Convert.ToInt32(Console.ReadLine());
			Debug.WriteLine(DateTime.Now);

			Console.WriteLine("Creating parity data file and CRC data file...");
			protectFile(fileName, parityBlocks);

			Console.WriteLine("Creating a dummy random-damaged file...");
			string damagedFileName = createDamagedFile(fileName, parityBlocks, fileDamages);

			Console.WriteLine("Detecting which blocks in file are damaged...");
			long []blocks = damagedBlocks(damagedFileName);
			Console.WriteLine("Detected " + blocks.Length + " damaged blocks in the file.");

			Console.WriteLine("Recovering the file with parity data...");
			recoverBlocks(damagedFileName, blocks, parityBlocks);

			Console.WriteLine("Detecting which blocks in file could not be recovered...");
			blocks = damagedBlocks(damagedFileName);

			Debug.WriteLine(DateTime.Now);

			if (blocks.Length == 0)
				Console.WriteLine("All the damaged blocks were recovered.");
			else
				Console.WriteLine("Detected " + blocks.Length + " damaged blocks which could not be recovered.");

			Console.WriteLine("Press <ENTER> to finish");
			Console.ReadLine();
		}
	}
}
