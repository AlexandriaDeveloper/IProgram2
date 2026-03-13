using System;
using System.Text;

class Program
{
    static void Main()
    {
        string input = "ﻥﺎﻤﻳﺍ";
        Console.WriteLine($"Original: {input}");
        foreach(char c in input) {
            Console.WriteLine($"{(int)c:X4}");
        }

        string kc = input.Normalize(NormalizationForm.FormKC);
        Console.WriteLine($"FormKC: {kc}");
        foreach(char c in kc) {
            Console.WriteLine($"{(int)c:X4}");
        }

        string c = input.Normalize(NormalizationForm.FormC);
        Console.WriteLine($"FormC: {c}");

        string kc_reversed = new string(kc.ToCharArray());
        Array.Reverse(kc_reversed.ToCharArray()); // This just reverses the array but doesn't assign back if we just new it.
        char[] arr = kc.ToCharArray();
        Array.Reverse(arr);
        string reversed = new string(arr);
        Console.WriteLine($"Reversed FormKC: {reversed}");
    }
}
