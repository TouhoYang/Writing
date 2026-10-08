using System;
using System.Globalization;

namespace Writing
{
    // 字数统计:总字数(不含空白)、净字数(再剔除标点)
    public static class TextStats
    {
        public static int TotalChars(string text)
        {
            int n = 0;
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c)) n++;
            }
            return n;
        }

        public static int NetChars(string text)
        {
            int n = 0;
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c) && !IsPunct(c)) n++;
            }
            return n;
        }

        public static bool IsPunct(char c)
        {
            UnicodeCategory cat = char.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.ConnectorPunctuation || cat == UnicodeCategory.DashPunctuation ||
                cat == UnicodeCategory.OpenPunctuation || cat == UnicodeCategory.ClosePunctuation ||
                cat == UnicodeCategory.InitialQuotePunctuation || cat == UnicodeCategory.FinalQuotePunctuation ||
                cat == UnicodeCategory.OtherPunctuation)
            {
                return true;
            }
            // 全角运算符与常用符号也按标点剔除
            switch (c)
            {
                case '…': case '—': case '·': case '～': case '〜': case '‖': case '〃':
                case '＋': case '－': case '＝': case '＜': case '＞': case '／': case '＼':
                case '＾': case '｜': case '｀': case '＆': case '＄': case '％': case '＃':
                case '＠': case '＊': case '§': case '※': case '∴': case '∵':
                case '★': case '☆': case '○': case '●': case '◇': case '◆':
                case '□': case '■': case '△': case '▲': case '→': case '←':
                case '↑': case '↓':
                    return true;
            }
            return false;
        }
    }
}
