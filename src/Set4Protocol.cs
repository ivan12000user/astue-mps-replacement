using System;
using System.Collections.Generic;
using System.Text;

namespace AstueMpsReplacement
{
    /// <summary>
    /// Minimal read-only implementation of the SET-4TM.02-compatible binary protocol.
    /// Only commands that are documented in the public OWEN example are implemented here.
    /// </summary>
    internal static class Set4Protocol
    {
        public static byte[] OpenChannel(byte address, string password)
        {
            var pwd = NormalizePassword(password);
            var body = new List<byte>(8) { address, 0x01 };
            body.AddRange(Encoding.ASCII.GetBytes(pwd));
            return AddCrc(body.ToArray());
        }

        public static byte[] ReadFloat(byte address, byte dataArray, byte rwri)
        {
            // 08h = read parameters/data, 1Bh = floating-point data.
            return AddCrc(new[] { address, (byte)0x08, (byte)0x1B, dataArray, rwri });
        }

        public static byte[] AddCrc(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var crc = Crc16(payload, 0, payload.Length);
            var frame = new byte[payload.Length + 2];
            Buffer.BlockCopy(payload, 0, frame, 0, payload.Length);
            frame[frame.Length - 2] = (byte)(crc & 0xFF);       // CRC low byte first
            frame[frame.Length - 1] = (byte)((crc >> 8) & 0xFF);
            return frame;
        }

        public static bool CheckCrc(byte[] frame)
        {
            if (frame == null || frame.Length < 3) return false;
            var crc = Crc16(frame, 0, frame.Length - 2);
            return frame[frame.Length - 2] == (byte)(crc & 0xFF) &&
                   frame[frame.Length - 1] == (byte)((crc >> 8) & 0xFF);
        }

        public static ushort Crc16(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length) throw new ArgumentOutOfRangeException();

            ushort crc = 0xFFFF;
            for (var i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (var bit = 0; bit < 8; bit++)
                    crc = (ushort)(((crc & 1) != 0) ? ((crc >> 1) ^ 0xA001) : (crc >> 1));
            }
            return crc;
        }

        public static float ParseFloatResponse(byte[] response, byte expectedAddress)
        {
            ValidateCommon(response, expectedAddress);

            if (response.Length == 4)
                throw new InvalidOperationException("SET4 status 0x" + response[1].ToString("X2"));
            if (response.Length != 7)
                throw new InvalidOperationException("SET4: unexpected float response length " + response.Length);

            var b = new byte[4];
            Buffer.BlockCopy(response, 1, b, 0, 4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            var value = BitConverter.ToSingle(b, 0);
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException("SET4: invalid IEEE754 value");
            return value;
        }

        public static void ValidateOpenResponse(byte[] response, byte expectedAddress)
        {
            ValidateCommon(response, expectedAddress);
            if (response.Length != 4)
                throw new InvalidOperationException("SET4 OPEN: unexpected response length " + response.Length);
            if (response[1] != 0x00)
                throw new InvalidOperationException("SET4 OPEN status 0x" + response[1].ToString("X2"));
        }

        private static void ValidateCommon(byte[] response, byte expectedAddress)
        {
            if (response == null || response.Length == 0) throw new TimeoutException("SET4: timeout");
            if (response.Length < 4) throw new InvalidOperationException("SET4: short response");
            if (!CheckCrc(response)) throw new InvalidOperationException("SET4: CRC error: " + Hex(response));
            if (response[0] != expectedAddress)
                throw new InvalidOperationException("SET4: wrong address: " + Hex(response));
        }

        public static string NormalizePassword(string password)
        {
            var p = (password ?? string.Empty).Trim();
            if (p.StartsWith("{A}", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("{X}", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(3);
            if (p.Length == 0) p = "000000";
            if (p.Length > 6) p = p.Substring(0, 6);
            return p.PadRight(6, '0');
        }

        private static string Hex(byte[] b)
        {
            return b == null ? "<null>" : BitConverter.ToString(b).Replace("-", " ");
        }
    }
}
