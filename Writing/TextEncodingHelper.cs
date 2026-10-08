using System;
using System.IO;
using System.Text;

namespace Writing
{
    // txt 编码检测与保存:UTF-8(BOM/无BOM)、UTF-16、GB18030/GBK
    public static class TextEncodingHelper
    {
        public static string Decode(byte[] bytes, out string encodingName)
        {
            // 1. BOM 检测
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encodingName = "utf-8-bom";
                return new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encodingName = "utf-16le";
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encodingName = "utf-16be";
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            // 2. 严格 UTF-8(非法字节会抛异常)
            try
            {
                string t = new UTF8Encoding(false, true).GetString(bytes);
                encodingName = "utf-8";
                return t;
            }
            catch { }
            // 3. 严格 GB18030(覆盖 GBK/GB2312)
            try
            {
                string t = StrictGB(bytes);
                encodingName = "gb18030";
                return t;
            }
            catch { }
            // 4. 宽松 GB18030(非法字节替换为 ?)
            encodingName = "gb18030";
            return LooseGB(bytes);
        }

        static string StrictGB(byte[] bytes)
        {
            Encoding enc = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return enc.GetString(bytes);
        }

        static string LooseGB(byte[] bytes)
        {
            return Encoding.GetEncoding(54936).GetString(bytes);
        }

        public static byte[] Encode(string text, string encodingName)
        {
            if (encodingName == "utf-8-bom") return new UTF8Encoding(true).GetBytes(text);
            if (encodingName == "utf-16le")
            {
                byte[] body = Encoding.Unicode.GetBytes(text);
                byte[] all = new byte[body.Length + 2];
                all[0] = 0xFF; all[1] = 0xFE;
                Array.Copy(body, 0, all, 2, body.Length);
                return all;
            }
            if (encodingName == "utf-16be")
            {
                byte[] body = Encoding.BigEndianUnicode.GetBytes(text);
                byte[] all = new byte[body.Length + 2];
                all[0] = 0xFE; all[1] = 0xFF;
                Array.Copy(body, 0, all, 2, body.Length);
                return all;
            }
            if (encodingName == "gb18030") return Encoding.GetEncoding(54936).GetBytes(text);
            return new UTF8Encoding(false).GetBytes(text);
        }

        public static string DisplayName(string encodingName)
        {
            if (encodingName == "utf-8") return "UTF-8";
            if (encodingName == "utf-8-bom") return "UTF-8 BOM";
            if (encodingName == "utf-16le") return "UTF-16 LE";
            if (encodingName == "utf-16be") return "UTF-16 BE";
            if (encodingName == "gb18030") return "GBK/GB18030";
            return encodingName;
        }
    }
}
