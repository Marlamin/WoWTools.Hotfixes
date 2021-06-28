using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WoWTools.HotfixDumper
{
    class Utils
    {
        private static readonly uint[] s_hashtable = new uint[] {
        0x486E26EE, 0xDCAA16B3, 0xE1918EEF, 0x202DAFDB,
        0x341C7DC7, 0x1C365303, 0x40EF2D37, 0x65FD5E49,
        0xD6057177, 0x904ECE93, 0x1C38024F, 0x98FD323B,
        0xE3061AE7, 0xA39B0FA1, 0x9797F25F, 0xE4444563,
        };

        public static uint Hash(string s)
        {
            uint v = 0x7fed7fed;
            var x = 0xeeeeeeee;
            for (var i = 0; i < s.Length; i++)
            {
                var c = (byte)s[i];
                v += x;
                v ^= s_hashtable[(c >> 4) & 0xf] - s_hashtable[c & 0xf];
                x = x * 33 + v + c + 3;
            }
            return v;
        }
    }

    public static class Extensions
    {
        public static T Read<T>(this BinaryReader bin)
        {
            Span<byte> bytes = bin.ReadBytes(Unsafe.SizeOf<T>());
            //var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            //T ret = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            //handle.Free();
            //return ret;
            return Unsafe.As<byte, T>(ref bytes[0]);
        }

        /// <summary> Reads the NULL terminated string from 
        /// the current stream and advances the current position of the stream by string length + 1.
        /// <seealso cref="BinaryReader.ReadString"/>
        /// </summary>
        public static string ReadCString(this BinaryReader reader)
        {
            return reader.ReadCString(Encoding.UTF8);
        }

        /// <summary> Reads the NULL terminated string from 
        /// the current stream and advances the current position of the stream by string length + 1.
        /// <seealso cref="BinaryReader.ReadString"/>
        /// </summary>
        public static string ReadCString(this BinaryReader reader, Encoding encoding)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                bytes.Add(b);
            return encoding.GetString(bytes.ToArray());
        }
    }

    public static class ByteArrayToHexStringExtensions
    {
        private static readonly uint[] _lookup32 = CreateLookup32();

        private static uint[] CreateLookup32()
        {
            var result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString("x2");
                result[i] = s[0] + ((uint)s[1] << 16);
            }
            return result;
        }

        public static string ToHexString(this byte[] bytes)
        {
            var lookup32 = _lookup32;
            Span<char> result = stackalloc char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                var val = lookup32[bytes[i]];
                result[2 * i] = (char)val;
                result[2 * i + 1] = (char)(val >> 16);
            }
            return new string(result);
        }

        public static string ToHexString(this Span<byte> bytes)
        {
            var lookup32 = _lookup32;
            Span<char> result = stackalloc char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                var val = lookup32[bytes[i]];
                result[2 * i] = (char)val;
                result[2 * i + 1] = (char)(val >> 16);
            }
            return new string(result);
        }
    }

    public static unsafe class ByteArrayToHexStringExtensionsUnsafe
    {
        private static readonly uint[] _lookup32Unsafe = CreateLookup32Unsafe();
        private static readonly uint* _lookup32UnsafeP = (uint*)GCHandle.Alloc(_lookup32Unsafe, GCHandleType.Pinned).AddrOfPinnedObject();

        private static uint[] CreateLookup32Unsafe()
        {
            var result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString("x2");
                if (BitConverter.IsLittleEndian)
                    result[i] = s[0] + ((uint)s[1] << 16);
                else
                    result[i] = s[1] + ((uint)s[0] << 16);
            }
            return result;
        }

        public static string ToHexStringUnsafe(this byte[] bytes)
        {
            var lookupP = _lookup32UnsafeP;
            var result = new string((char)0, bytes.Length * 2);
            fixed (byte* bytesP = bytes)
            fixed (char* resultP = result)
            {
                uint* resultP2 = (uint*)resultP;
                for (int i = 0; i < bytes.Length; i++)
                {
                    resultP2[i] = lookupP[bytesP[i]];
                }
            }
            return result;
        }

        public static string ToHexStringUnsafe(this Span<byte> bytes)
        {
            var lookupP = _lookup32UnsafeP;
            var result = new string((char)0, bytes.Length * 2);
            fixed (byte* bytesP = bytes)
            fixed (char* resultP = result)
            {
                uint* resultP2 = (uint*)resultP;
                for (int i = 0; i < bytes.Length; i++)
                {
                    resultP2[i] = lookupP[bytesP[i]];
                }
            }
            return result;
        }
    }
}
