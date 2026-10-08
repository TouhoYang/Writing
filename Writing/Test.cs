using System;
using System.IO;
using System.Text;

namespace Writing.Test
{
    public static class Test
    {
        static int _failed = 0;

        public static void Main()
        {
            // 1. 字数统计
            Check("total-cn", TextStats.TotalChars("你好，世界！") == 6, TextStats.TotalChars("你好，世界！").ToString());
            Check("net-cn", TextStats.NetChars("你好，世界！") == 4, TextStats.NetChars("你好，世界！").ToString());
            Check("total-en", TextStats.TotalChars("Hello, world!") == 12, TextStats.TotalChars("Hello, world!").ToString());
            Check("net-en", TextStats.NetChars("Hello, world!") == 10, TextStats.NetChars("Hello, world!").ToString());
            Check("whitespace", TextStats.TotalChars("a b\tc\r\nd　e") == 5, TextStats.TotalChars("a b\tc\r\nd　e").ToString());

            // 2. UTF-8 无 BOM 检测
            string tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "测试UTF8文本", new UTF8Encoding(false));
            byte[] b1 = File.ReadAllBytes(tmp);
            string e1;
            string t1 = TextEncodingHelper.Decode(b1, out e1);
            Check("detect-utf8", e1 == "utf-8" && t1 == "测试UTF8文本", e1);

            // 3. UTF-8 BOM 检测
            File.WriteAllText(tmp, "测试BOM文本", new UTF8Encoding(true));
            b1 = File.ReadAllBytes(tmp);
            t1 = TextEncodingHelper.Decode(b1, out e1);
            Check("detect-utf8bom", e1 == "utf-8-bom" && t1 == "测试BOM文本", e1);

            // 4. GB18030 检测(GBK 编码的中文)
            byte[] gbk = Encoding.GetEncoding(54936).GetBytes("GBK编码的中文小说内容");
            t1 = TextEncodingHelper.Decode(gbk, out e1);
            Check("detect-gb18030", e1 == "gb18030" && t1 == "GBK编码的中文小说内容", e1);

            // 5. UTF-16 LE 检测
            File.WriteAllText(tmp, "UTF16文本", Encoding.Unicode);
            b1 = File.ReadAllBytes(tmp);
            t1 = TextEncodingHelper.Decode(b1, out e1);
            Check("detect-utf16le", e1 == "utf-16le" && t1 == "UTF16文本", e1);

            // 6. 编码回环:GB18030 保存 → 再读(含生僻字 𠀀)
            string special = "回环测试𠀀㐀";
            byte[] enc = TextEncodingHelper.Encode(special, "gb18030");
            string dec;
            dec = TextEncodingHelper.Decode(enc, out e1);
            Check("roundtrip-gb18030", dec == special && e1 == "gb18030", dec);

            // 7. UTF-8 保存回环
            enc = TextEncodingHelper.Encode(special, "utf-8");
            dec = TextEncodingHelper.Decode(enc, out e1);
            Check("roundtrip-utf8", dec == special && e1 == "utf-8", dec);

            File.Delete(tmp);

            Console.WriteLine(_failed == 0 ? "ALL_PASS" : ("FAILED_COUNT=" + _failed));
            Environment.Exit(_failed == 0 ? 0 : 1);
        }

        static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " detail=" + detail);
            if (!ok) _failed++;
        }
    }
}
