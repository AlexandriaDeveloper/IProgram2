using System;
using System.Text;

namespace PdfTest
{
    class Program
    {
        static void Main(string[] args)
        {
            string input = "ﻥﺎﻤﻳﺍ";
            Console.WriteLine($"Original: {input}");

            string kc = input.Normalize(NormalizationForm.FormKC);
            Console.WriteLine($"FormKC: {kc}");

            char[] arr = kc.ToCharArray();
            Array.Reverse(arr);
            string reversed = new string(arr);
            Console.WriteLine($"Reversed FormKC: {reversed}");
        }
    }
}
