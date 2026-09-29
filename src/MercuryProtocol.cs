using System;
using System.Collections.Generic;

namespace AstueMpsReplacement
{
    public static class MercuryProtocol
    {
        // Протокол Меркурий использует CRC16 с полиномом MODBUS (0xA001).
        public static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (var i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (var bit = 0; bit < 8; bit++)
                    crc = (ushort)(((crc & 1) != 0) ? ((crc >> 1) ^ 0xA001) : (crc >> 1));
            }
            return crc;
        }

        public static byte[] Frame(byte address, params byte[] commandAndPayload)
        {
            var body = new List<byte>(1 + commandAndPayload.Length + 2) { address };
            body.AddRange(commandAndPayload);
            var crc = Crc16(body.ToArray(), 0, body.Count);
            body.Add((byte)(crc & 0xFF));
            body.Add((byte)(crc >> 8));
            return body.ToArray();
        }

        public static bool CheckCrc(byte[] frame)
        {
            if (frame == null || frame.Length < 3) return false;
            var expected = Crc16(frame, 0, frame.Length - 2);
            return frame[frame.Length - 2] == (byte)(expected & 0xFF) && frame[frame.Length - 1] == (byte)(expected >> 8);
        }
    }
}
